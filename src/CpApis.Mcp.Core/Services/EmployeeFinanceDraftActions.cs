using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using static CpApis.Mcp.Core.Services.EmployeeNativeSql;

namespace CpApis.Mcp.Core.Services;

/// <summary>Draft metadata only. No activation, signing, payment, export or automatic generation.</summary>
internal static class EmployeeFinanceDraftActions
{
    private sealed record Draft(Dictionary<string, object?>? Current, Dictionary<string, object?> Proposed,
        byte[] Hash, IReadOnlyList<EmployeeActionField> Changes);

    internal static async Task<EmployeeActionPlan> PrepareAsync(string operation, JsonElement payload,
        SqlConnection connection, SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        var draft = await BuildAsync(operation, payload, connection, transaction, actor, ct);
        var invoice=operation.StartsWith("invoice.",StringComparison.Ordinal);
        var directoryId=draft.Proposed.Number(invoice?"DirID":"Client_LocationsID");
        var clients=await RowsAsync(connection,transaction,"SELECT LEFT(COALESCE(NULLIF(Location_Name,''),Client_Name,''),150) ClientName FROM dbo.tbl_Directory WHERE DirID=@ID;",ct,("@ID",directoryId));
        var client=clients.Count==1?clients[0].Text("ClientName"):$"directory {directoryId}";
        var title=draft.Proposed.Text(invoice?"Title":"Contract_Name")??"";if(title.Length>150)title=title[..150];
        var verb=operation.EndsWith(".create",StringComparison.Ordinal)?"Create":operation.EndsWith(".delete",StringComparison.Ordinal)?"Delete":"Edit";
        var id=draft.Current?.Number(invoice?"InvoiceID":"Contract_ID");
        return new(Snapshot(draft.Hash,clients),$"{verb} draft {(invoice?"invoice":"contract")} {(id.HasValue?id.Value.ToString():"")} for {client}: {title}. Commercial terms are not verified.",draft.Changes);
    }

    internal static async Task<EmployeeActionResult> ApplyAsync(string operation, JsonElement payload,
        SqlConnection connection, SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        var draft = await BuildAsync(operation, payload, connection, transaction, actor, ct);
        var invoice = operation.StartsWith("invoice.", StringComparison.Ordinal); var delete = operation.EndsWith(".delete", StringComparison.Ordinal);
        var key = invoice ? "InvoiceID" : "Contract_ID";
        var id = draft.Current?.Number(key) ?? 0;
        if (delete)
        {
            await ExecuteAsync(connection, transaction, invoice
                ? "EXEC dbo.stp_Inv_Org_Loc_Inv_Delete_V2 @InvoiceID=@ID,@UserID=@Actor;"
                : "EXEC dbo.stp_Con_Delete @ContractID=@ID,@UserID=@Actor;", ct, ("@ID", id), ("@Actor", actor.UserId));
            if (invoice)
            {
                if ((await RowsAsync(connection, transaction, "SELECT InvoiceID FROM dbo.tbl_InvoiceHeader WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID;", ct, ("@ID", id))).Count != 0)
                    throw Locked("Native invoice deletion did not remove the reviewed draft.");
            }
            else
            {
                var actual = await OneAsync(connection, transaction, "SELECT ContractStatusTxt FROM dbo.tbl_Contracts WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID;", ct, ("@ID", id));
                if (actual.Text("ContractStatusTxt") != "Deleted") throw Locked("Native contract deletion did not deactivate the reviewed draft.");
            }
            return new(operation, id, invoice ? "Unsigned, unpaid, unexported draft invoice deleted atomically." : "Unsigned draft contract marked Deleted using the native procedure.");
        }
        var proposed = draft.Proposed;
        var xmlRoot = invoice ? new XElement("OrgLocInvSave",
            new XElement("InvoiceID", id), new XElement("DirID", proposed["DirID"]), new XElement("Title", proposed["Title"]),
            new XElement("InvoiceNo", proposed["InvoiceNo"]), new XElement("InvoiceType", "Invoice"),
            new XElement("Status", "Draft"), new XElement("UserID", actor.UserId))
            : new XElement("OrgLocConInfoSave", new XElement("Contract_ID", id), new XElement("Create_UserID", actor.UserId),
                new XElement("Update_UserID", actor.UserId), new XElement("Client_LocationsID", proposed["Client_LocationsID"]),
                new XElement("Contract_Name", proposed["Contract_Name"]), new XElement("ContractType", proposed["ContractType"]),
                new XElement("ContractStatus", "Draft"));
        foreach (var field in invoice ? new[] { "DueDate" } : new[] { "SLA_EOR", "SLA_IRT", "Over_Hours_Rate", "Under_Contract_Period", "StartDate" })
            if (proposed.TryGetValue(field, out var value) && value is not null) xmlRoot.Add(new XElement(field, Display(value)));
        if (!invoice && proposed.TryGetValue("Fixed_Rate_Cost", out var cost) && cost is not null) xmlRoot.Add(new XElement("Fixed_Price", Display(cost)));
        XDocument response;
        if (invoice) response = await XmlAsync(connection, transaction, "dbo.stp_xml_Inv_Org_Loc_Inv_Header_Save",
            "@XML_Invoice", new XDocument(xmlRoot), "@XML_OUT", ct);
        else
        {
            await using var command = Command(connection, transaction, "dbo.stp_xml_Con_Org_Loc_Con_Save"); command.CommandType = CommandType.StoredProcedure;
            command.Parameters.Add("@XML_IN", SqlDbType.Xml).Value = xmlRoot.ToString(SaveOptions.DisableFormatting);
            var output = command.Parameters.Add("@XML_OUT", SqlDbType.Xml); output.Direction = ParameterDirection.Output;
            command.Parameters.Add("@XML_Action", SqlDbType.Xml).Direction = ParameterDirection.Output;
            await command.ExecuteNonQueryAsync(ct);
            if (output.Value is not string text || text.Length > 100_000) throw Locked("The native contract result is invalid.");
            response = XDocument.Parse(text);
        }
        var ids = response.Descendants(key).Select(e => int.TryParse(e.Value, out var n) ? n : 0).Distinct().ToArray();
        if (ids.Length != 1 || ids[0] <= 0 || id != 0 && ids[0] != id) throw Locked("The native draft save returned an ambiguous record.");
        id = ids[0];
        var saved = invoice ? await InvoiceAsync(id, connection, transaction, ct) : await ContractAsync(id, connection, transaction, ct);
        foreach (var field in invoice ? new[] { "DirID", "Title", "InvoiceNo", "DueDate" } : new[] { "Client_LocationsID", "Contract_Name", "ContractType" })
            if (Display(saved[field]) != Display(proposed[field])) throw Locked("Native draft readback differs from the reviewed changes. The action was rolled back.");
        if (draft.Current is not null)
        {
            var mutable = invoice ? new[] { "Title", "DueDate", "ModifiedUser", "ModifiedDatetime" }
                : new[] { "Contract_Name", "Update_UserID", "Update_timestamp" };
            foreach (var field in draft.Current.Keys.Where(k => !mutable.Contains(k, StringComparer.Ordinal)))
                if (Display(saved[field]) != Display(draft.Current[field])) throw Locked("The native draft save changed an omitted field; the action was rolled back.");
        }
        return new(operation, id, "Draft metadata saved and native readback verified. Prices, signatures, accounting export and payment were not performed or certified.");
    }

    private static async Task<Draft> BuildAsync(string operation, JsonElement payload, SqlConnection connection,
        SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        var invoice = operation.StartsWith("invoice.", StringComparison.Ordinal); var create = operation.EndsWith(".create", StringComparison.Ordinal);
        var delete = operation.EndsWith(".delete", StringComparison.Ordinal);
        await ClientPortalMenuAccess.RequireAsync(connection, transaction, actor, invoice ? PortalMenu.Invoices : PortalMenu.Contracts, ct);
        if (invoice) await RequireScopeAsync(connection, transaction, actor, "Invoices.Get", false, true, ct);
        await RequireScopeAsync(connection, transaction, actor, invoice ? "Invoices.Edit" : "Contracts.Edit", false, false, ct);
        await RequireScopeAsync(connection, transaction, actor, invoice ? "Invoices.Delete" : "Contracts.Delete", false, false, ct);
        Payload(payload, invoice ? create ? ["billingDirectoryId", "title", "dueDate"] : delete ? ["invoiceId"] : ["invoiceId", "title", "dueDate"]
            : create ? ["clientDirectoryId", "name", "contractType"] : delete ? ["contractId"] : ["contractId", "name"]);
        if (create)
        {
            var client = await OneAsync(connection, transaction, """
                SELECT DirID,LocationTopFilter,Location_Name,Client_Name FROM dbo.tbl_Directory WITH(UPDLOCK,HOLDLOCK)
                WHERE DirID=@ID AND IsActive=1 AND DirectoryType IN('Client','Location');
                """, ct, ("@ID", Id(payload, invoice ? "billingDirectoryId" : "clientDirectoryId")));
            var proposed = invoice ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DirID"] = client["DirID"], ["Title"] = RequiredText(payload, "title", 1000), ["InvoiceNo"] = "",
                ["DueDate"] = OptionalDate(payload, "dueDate"), ["InvoiceType"] = "Invoice", ["Status"] = "Draft"
            } : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Client_LocationsID"] = client["DirID"], ["Contract_Name"] = RequiredText(payload, "name", 200),
                ["ContractType"] = RequiredText(payload, "contractType", 50), ["ContractStatusTxt"] = "Draft"
            };
            object? type = null;
            if (!invoice) type = await OneAsync(connection, transaction, "SELECT ValueCode,ValueDescription FROM dbo.vw_LookupV WHERE ViewName='Org_Loc_Con_Info' AND FieldName='ContractType' AND ValueDescription=@Type;", ct, ("@Type", proposed["ContractType"]));
            return new(null, proposed, Snapshot(client, proposed, type), proposed.Select(p => new EmployeeActionField(p.Key, null, Display(p.Value))).ToArray());
        }
        var current = invoice ? await InvoiceAsync(Id(payload, "invoiceId"), connection, transaction, ct)
            : await ContractAsync(Id(payload, "contractId"), connection, transaction, ct);
        var recordId = current.Number(invoice ? "InvoiceID" : "Contract_ID");
        var children = invoice ? await InvoiceChildrenAsync(recordId, connection, transaction, ct)
            : await ContractChildrenAsync(recordId, connection, transaction, ct);
        if (invoice)
        {
            if (!current.Flag("IsActive") || current.Text("Status") != "Draft" || current.Text("InvoiceType") != "Invoice"
                || current.Text("Paid") is not (null or "0.0000" or "0.00" or "0") || current.Number("SignedBy") != 0 || current["SignDate"] is not null
                || current.Text("QBConfirm") is not (null or "") || current.Text("QBError") is not (null or "")
                || current.Number("ContractID") != 0 || HasDocument(current["DSGUID"]) || children.Any(c => c.Text("Kind") != "line" || c.Flag("HasAccountingReference")))
                throw Locked("Only active, unsigned, unpaid, unexported, unlinked draft Invoice records can be changed. Use the native Portal for this record.");
        }
        else if (current.Text("ContractStatusTxt") != "Draft" || current.Number("Signed_DirID") != 0 || current["DateSigned"] is not null
            || current.Number("AutotaskID") != 0 || HasDocument(current["DSGUID"]) || current["ClosedDate"] is not null
            || children.Any(c => c.Text("Kind") != "price" && c.Text("Kind") != "pod"))
            throw Locked("Only unsigned, unlinked draft contracts can be changed. Active, signed, exported, document-linked or work-linked contracts require the native Portal.");
        var after = new Dictionary<string, object?>(current, StringComparer.Ordinal);
        if (!delete)
        {
            var field = invoice ? "Title" : "Contract_Name";
            after[field] = PatchText(payload, invoice ? "title" : "name", current[field], invoice ? 1000 : 200, false);
            if (invoice && payload.TryGetProperty("dueDate", out _)) after["DueDate"] = OptionalDate(payload, "dueDate");
        }
        var changes = delete ? new EmployeeActionField[] { new(invoice ? "Invoice" : "Contract", Display(current[invoice ? "Title" : "Contract_Name"]), null), new("State", "Draft", invoice ? "Deleted permanently" : "Deleted"),
                new("Linked draft details", JsonSerializer.Serialize(children, EmployeeActionJson.Options), null) }
            : after.Where(p => Display(current[p.Key]) != Display(p.Value)).Select(p => new EmployeeActionField(p.Key, Display(current[p.Key]), Display(p.Value))).ToArray();
        if (changes.Length == 0) throw new EmployeeActionException(400, "The proposal does not change any supported draft metadata.");
        return new(current, after, Snapshot(current, children), changes);
    }

    private static bool HasDocument(object? value) => value switch
    {
        null => false, Guid id => id != Guid.Empty,
        _ => !string.IsNullOrWhiteSpace(Display(value))
    };
    private static DateTime? OptionalDate(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var date)) return null;
        if (date.ValueKind != JsonValueKind.String || !DateTime.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            || result.Year < 2000 || result.Year > 2099) throw EmployeeActionJson.Invalid();
        return result;
    }

    private static Task<Dictionary<string, object?>> InvoiceAsync(int id, SqlConnection connection, SqlTransaction transaction, CancellationToken ct) =>
        OneAsync(connection, transaction, """
            SELECT InvoiceID,DirID,InvoiceNo,InvoiceType,InvoiceDate,DueDate,Title,Status,Tax,Paid,IsActive,ContractID,
              QBConfirm,QBError,SignedBy,SignDate,DSGUID,ModifiedUser,ModifiedDatetime
            FROM dbo.tbl_InvoiceHeader WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID;
            """, ct, ("@ID", id));
    private static Task<Dictionary<string, object?>> ContractAsync(int id, SqlConnection connection, SqlTransaction transaction, CancellationToken ct) =>
        OneAsync(connection, transaction, "SELECT * FROM dbo.tbl_Contracts WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID;", ct, ("@ID", id));
    private static Task<List<Dictionary<string, object?>>> InvoiceChildrenAsync(int id, SqlConnection connection, SqlTransaction transaction, CancellationToken ct) =>
        RowsAsync(connection, transaction, """
            SELECT ID,'line' Kind,(SELECT d.* FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) Details,
              CONVERT(bit,CASE WHEN NULLIF(LTRIM(RTRIM(d.QBLineID)),'') IS NOT NULL THEN 1 ELSE 0 END) HasAccountingReference
              FROM dbo.tbl_Invoice_Detail d WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT ID,'ticket',NULL,NULL FROM dbo.tbl_Invoices_Tickets WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT InvoiceID,'contract',NULL,NULL FROM dbo.tbl_Invoices_Contracts WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT TicketEntryID,'time',NULL,NULL FROM dbo.tbl_TicketEntries WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT InvoiceID,'monthly-support',NULL,NULL FROM dbo.tbl_Invoice_Monthly_ActualSupport_Detail WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT InvoiceID,'monthly-asset',NULL,NULL FROM dbo.tbl_Invoice_Monthly_Asset_Detail WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT InvoiceID,'monthly-labor',NULL,NULL FROM dbo.tbl_Invoice_Monthly_Labor_Detail WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT InvoiceID,'monthly-sip',NULL,NULL FROM dbo.tbl_Invoice_Monthly_SIP_Detail WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT InvoiceID,'recurring',NULL,NULL FROM dbo.tbl_Invoice_Recur_Detail WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            UNION ALL SELECT InvoiceID,'weekly',NULL,NULL FROM dbo.tbl_Invoice_Weekly_Detail WITH(UPDLOCK,HOLDLOCK) WHERE InvoiceID=@ID
            ORDER BY Kind,ID;
            """, ct, ("@ID", id));
    private static Task<List<Dictionary<string, object?>>> ContractChildrenAsync(int id, SqlConnection connection, SqlTransaction transaction, CancellationToken ct) =>
        RowsAsync(connection, transaction, """
            SELECT ID,'price' Kind,(SELECT p.* FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) Details
              FROM dbo.tbl_Contracts_PriceList p WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID
            UNION ALL SELECT Contract_ID,'pod',(SELECT p.* FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)
              FROM dbo.tbl_Contracts_Pod p WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID
            UNION ALL SELECT TicketID,'ticket',NULL FROM dbo.tbl_Tickets WITH(UPDLOCK,HOLDLOCK) WHERE ContractID=@ID
            UNION ALL SELECT TicketEntryID,'time',NULL FROM dbo.tbl_TicketEntries WITH(UPDLOCK,HOLDLOCK) WHERE ContractId=@ID
            UNION ALL SELECT InvoiceID,'invoice',NULL FROM dbo.tbl_InvoiceHeader WITH(UPDLOCK,HOLDLOCK) WHERE ContractID=@ID
            UNION ALL SELECT InvoiceID,'invoice-contract',NULL FROM dbo.tbl_Invoices_Contracts WITH(UPDLOCK,HOLDLOCK) WHERE ContractID=@ID
            UNION ALL SELECT ContractID,'pricing-update',NULL FROM Automated.tbl_PricingUpdates WITH(UPDLOCK,HOLDLOCK) WHERE ContractID=@ID
            UNION ALL SELECT ContractID,'recurring-ticket',NULL FROM dbo.tbl_MasterRecurringTickets WITH(UPDLOCK,HOLDLOCK) WHERE ContractID=@ID
            UNION ALL SELECT Contract_ID,'legacy-invoice',NULL FROM dbo.tbl_Invoices WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID
            UNION ALL SELECT Contract_ID,'monthly-count',NULL FROM dbo.tbl_Monthly_Counts WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID
            UNION ALL SELECT Contract_ID,'monthly-hours',NULL FROM dbo.tbl_MonthlyHours_Counts WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID
            UNION ALL SELECT ContractID,'billing-data',NULL FROM dbo.tbl_TicketEntries_BillData WITH(UPDLOCK,HOLDLOCK) WHERE ContractID=@ID
            ORDER BY Kind,ID;
            """, ct, ("@ID", id));
}
