using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using static CpApis.Mcp.Core.Services.EmployeeNativeSql;

namespace CpApis.Mcp.Core.Services;

/// <summary>Native ticket operations with actor ownership, fresh menus and reviewed changes.</summary>
internal static class EmployeeTicketActions
{
    internal static async Task<EmployeeActionPlan> PrepareAsync(string operation, JsonElement payload,
        SqlConnection connection, SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        if (operation == "ticket.create")
        {
            var create = await CreateAsync(payload, connection, transaction, actor, ct);
            return new(Snapshot(create), "Create a Client Portal ticket assigned to you.",
                create.Where(p => p.Key is "ClientID" or "Client" or "Title" or "Description" or "Priority" or "Priority label" or "RoleType" or "Work role" or "ContractID")
                    .Select(p => new EmployeeActionField(p.Key, null, Display(p.Value)))
                    .Append(new("Email notifications", null, "Off")).ToArray());
        }
        Payload(payload, operation == "ticket.delete" ? ["ticketId"] :
            ["ticketId", "title", "description", "priorityId", "statusId", "estimatedHours"]);
        var current = await ReadAsync(Id(payload, "ticketId"), connection, transaction, actor, ct);
        await RequireScopeAsync(connection, transaction, actor, operation == "ticket.delete" ? "Tkt.Delete" : "Tkt.Edit",
            IsOwner(current, actor), false, ct);
        var links = await LinksAsync(current.Number("TicketID"), connection, transaction, ct);
        if (operation == "ticket.delete")
        {
            if (links.Any(l => l.Text("Kind") != "assignment") || current.Number("Project_Schedule_ID") != 0
                || current.Number("task_id_autotask") != 0 || current.Number("ContractPriceline") != 0
                || current.Text("RecurringType") is not (null or "" or "0") || current.Flag("IsScheduled"))
                throw Locked("This ticket has work, financial, project, scheduled or external links. Delete it through the native Portal after reviewing those links.");
            return new(Snapshot(current, links), $"Deactivate ticket {current.Number("TicketID")}: {current.Text("Title")}",
                [new("Ticket", current.Text("Title"), current.Text("Title")), new("Active", "true", "false")]);
        }
        var proposed = await MergeAsync(current, payload, connection, transaction, ct);
        var wasComplete=IsComplete(current.Text("Status"),current.Text("StatusTxt"));
        var willComplete=IsComplete(proposed.Text("Status"),proposed.Text("StatusTxt"));
        if (current.Number("Project_Schedule_ID") != 0 && (willComplete || wasComplete)
            || willComplete && !wasComplete && links.Any(l => l.Text("Kind") == "invoice"))
            throw Locked("Completing this project or invoice-linked ticket can trigger billing. Use the native Portal for this transition.");
        var changes = Changes(current, proposed);
        return new(Snapshot(current, links), $"Edit ticket {current.Number("TicketID")}: {current.Text("Title")}", changes);
    }

    internal static async Task<EmployeeActionResult> ApplyAsync(string operation, JsonElement payload,
        SqlConnection connection, SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        // Called only after the action service re-runs Prepare under the same serializable transaction.
        if (operation == "ticket.create")
        {
            var create = await CreateAsync(payload, connection, transaction, actor, ct);
            var xml = new XDocument(new XElement("Root", new XElement("Ticket", create
                .Where(p=>p.Key is not("Client" or "Work role" or "Priority label")).Select(p => new XElement(p.Key, p.Value)))));
            await using var command = Command(connection, transaction, "mcp.stp_EmployeeTicket_Create");
            command.CommandType = CommandType.StoredProcedure;
            command.Parameters.Add("@XML_IN", SqlDbType.Xml).Value = xml.ToString(SaveOptions.DisableFormatting);
            var output = command.Parameters.Add("@XML_OUT", SqlDbType.Xml); output.Direction = ParameterDirection.Output;
            await command.ExecuteNonQueryAsync(ct);
            if (output.Value is not string text || text.Length > 2000) throw Locked("The native ticket create returned an invalid result.");
            var ids = XDocument.Parse(text).Descendants("TicketID").Select(x => int.TryParse(x.Value, out var n) ? n : 0).ToArray();
            if (ids.Length != 1 || ids[0] <= 0) throw Locked("The native Portal did not create exactly one ticket.");
            var saved = await ReadAsync(ids[0], connection, transaction, actor, ct);
            var notification = await OneAsync(connection, transaction, "SELECT SendEmail FROM dbo.tbl_Tickets WITH(UPDLOCK,HOLDLOCK) WHERE TicketID=@ID;", ct, ("@ID", ids[0]));
            var differingFields = new[] { "Title", "Description", "Priority", "Status", "RoleType", "LocationTopFilter", "Requestor_DirID" }
                .Where(key => Display(saved[key]) != Display(create[key])).ToArray();
            if (differingFields.Length != 0 || saved.Text("CreatedBy") != actor.Username
                || saved.Number("AssignTo_DirID") != actor.UserId || saved.Number("ClientID") != create.Number("ClientID")
                || saved.Number("ContractID") != create.Number("ContractID") || notification.Flag("SendEmail")) throw Locked("Native ticket readback differs from the reviewed creation: " + string.Join(", ", differingFields));
            return new(operation, ids[0], "Ticket created with email notifications off and independently read back in the action transaction.");
        }
        var id = Id(payload, "ticketId");
        var before = await ReadAsync(id, connection, transaction, actor, ct);
        if (operation == "ticket.delete")
        {
            await ExecuteAsync(connection, transaction, "EXEC dbo.stp_Tkt_Delete @TicketID,@UserID;", ct,
                ("@TicketID", id), ("@UserID", actor.UserId));
            var saved = await OneAsync(connection, transaction, "SELECT IsActive FROM dbo.tbl_Tickets WITH(UPDLOCK,HOLDLOCK) WHERE TicketID=@ID;", ct, ("@ID", id));
            if (saved.Flag("IsActive")) throw Locked("Native ticket deletion did not deactivate the ticket.");
            return new(operation, id, "Ticket deactivated using the native Portal delete procedure.");
        }
        var after = await MergeAsync(before, payload, connection, transaction, ct);
        await using (var command = Command(connection, transaction, "mcp.stp_EmployeeTicket_Edit"))
        {
            command.CommandType = CommandType.StoredProcedure;
            void Add(string name, SqlDbType type, object? value, int size = 0)
            { var p = size == 0 ? command.Parameters.Add(name, type) : command.Parameters.Add(name, type, size); p.Value = value ?? DBNull.Value; }
            Add("@TicketID", SqlDbType.Int, id); Add("@ModifiedBy", SqlDbType.NVarChar, actor.Username, 50);
            Add("@Title", SqlDbType.NVarChar, after["Title"], 200); Add("@Description", SqlDbType.NVarChar, after["Description"], 8000);
            Add("@Priority", SqlDbType.NVarChar, after["Priority"], 50); Add("@Status", SqlDbType.NVarChar, after["Status"], 50);
            var estimated = command.Parameters.Add("@EstimatedHours", SqlDbType.Decimal); estimated.Precision = 18; estimated.Scale = 2; estimated.Value = after["Estimated_Hours"] ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
        }
        var actual = await ReadAsync(id, connection, transaction, actor, ct);
        var modified = await OneAsync(connection, transaction, "SELECT ModifiedBy FROM dbo.tbl_Tickets WITH(UPDLOCK,HOLDLOCK) WHERE TicketID=@ID;", ct, ("@ID", id));
        if (modified.Text("ModifiedBy") != actor.Username) throw Locked("Native ticket attribution differs from the authenticated employee; the edit was rolled back.");
        foreach (var key in new[] { "Title", "Description", "Priority", "Status", "Estimated_Hours", "Requestor_DirID", "AssignTo_DirID", "RequestDate", "Internal_Notes", "Description_RTF" })
            if (Display(actual[key]) != Display(after[key])) throw Locked("Native ticket readback differs from the reviewed edit; the action was rolled back.");
        return new(operation, id, "Ticket edited; omitted fields were preserved and native readback verified.");
    }

    internal static async Task<Dictionary<string, object?>> ReadAsync(int id, SqlConnection connection,
        SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        await ClientPortalMenuAccess.RequireAsync(connection, transaction, actor, PortalMenu.TicketSearch, ct);
        var row = await OneAsync(connection, transaction, """
            SELECT TicketID,Requestor_DirID,AssignTo_DirID,ClientID,ContractID,Title,Description,Priority,Status,
              Estimated_Hours,RequestDate,Internal_Notes,Description_RTF,RoleType,WorkType,CreatedBy,LastModifiedTime,
              Project_Schedule_ID,task_id_autotask,ContractPriceline,RecurringType,IsScheduled,LocationTopFilter,ClientName,ClientCode,StatusTxt
            FROM dbo.tbl_Tickets WITH(UPDLOCK,HOLDLOCK) WHERE TicketID=@ID AND IsActive=1;
            """, ct, ("@ID", id));
        if (!IsOwner(row, actor) && !actor.IsPortalAdministrator)
        {
            var assigned = await RowsAsync(connection, transaction, """
                SELECT ID FROM dbo.tbl_Tickets_Assigned WITH(UPDLOCK,HOLDLOCK)
                WHERE TicketID=@ID AND AssignedID=@Actor AND IsActive=1
                UNION ALL
                SELECT TicketEntryID FROM dbo.tbl_TicketEntries WITH(UPDLOCK,HOLDLOCK)
                WHERE TicketID=@ID AND AssignToUserID=@Actor AND IsActive=1 AND ISNULL(IsDeleted,0)=0
                  AND WorkType='Assign' AND StartDateTime=EndDateTime;
                """, ct, ("@ID", id), ("@Actor", actor.UserId));
            if (assigned.Count == 0) throw new EmployeeAccessDeniedException("You must own or be assigned to this ticket.");
        }
        return row;
    }

    private static bool IsOwner(Dictionary<string, object?> row, PortalEmployee actor) =>
        row.Number("AssignTo_DirID") == actor.UserId || string.Equals(row.Text("CreatedBy"), actor.Username, StringComparison.OrdinalIgnoreCase);

    // Mirror both terms of the native trigger's completion predicate, including legacy text statuses.
    private static bool IsComplete(string? value,string? label)=>int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var id)&&id==1272
        ||string.Equals(label?.Trim(),"Completed",StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, object?>> CreateAsync(JsonElement payload, SqlConnection connection,
        SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        Payload(payload, "clientDirectoryId", "title", "description", "roleTypeId", "priorityId");
        await ClientPortalMenuAccess.RequireAsync(connection, transaction, actor, PortalMenu.NewTicket, ct);
        await RequireScopeAsync(connection, transaction, actor, "Tkt.Create", true, false, ct);
        var client = await OneAsync(connection, transaction, """
            SELECT DirID,LocationTopFilter,LEFT(COALESCE(NULLIF(Location_Name,''),Client_Name,''),200) DisplayName,
              LEFT(COALESCE(NULLIF(LocationCode,''),ClientCode,''),100) DisplayCode FROM dbo.tbl_Directory WITH(UPDLOCK,HOLDLOCK)
            WHERE DirID=@ID AND IsActive=1 AND DirectoryType IN('Client','Location');
            """, ct, ("@ID", Id(payload, "clientDirectoryId")));
        var role = await LookupAsync(connection, transaction, "RoleType", Id(payload, "roleTypeId"), null, ct);
        var priority = await LookupAsync(connection, transaction, "Priority", payload.TryGetProperty("priorityId", out _) ? Id(payload, "priorityId") : null, "Watch", ct);
        var status = await LookupAsync(connection, transaction, "Status", null, "New", ct);
        var contract = await OneAsync(connection, transaction, "SELECT dbo.fn_int_GetActiveContractForLocation(@ID) ContractID;", ct, ("@ID", client.Number("DirID")));
        if (client.Text("LocationTopFilter") is not { Length: > 0 and <= 100 }) throw Locked("The client has no valid native location filter.");
        return new(StringComparer.Ordinal)
        {
            ["ClientID"] = client.Number("DirID"), ["Requestor_DirID"] = client.Number("DirID"), ["AssignTo_DirID"] = actor.UserId,
            ["ContractID"] = contract["ContractID"] ?? 0, ["Title"] = RequiredText(payload, "title", 200),
            ["Description"] = RequiredText(payload, "description", 8000), ["RoleType"] = role["ID"], ["Priority"] = priority["ID"],
            ["Status"] = status["ID"], ["LocationTopFilter"] = client["LocationTopFilter"], ["CreatedBy"] = actor.Username,
            ["ParentID"] = 0, ["AssetID"] = 0, ["RequestType"] = "Client Portal"
            , ["Client"] = $"{client.Text("DisplayCode")}: {client.Text("DisplayName")}",
            ["Work role"] = role.Text("Description"), ["Priority label"] = priority.Text("Description")
        };
    }

    private static async Task<Dictionary<string, object?>> MergeAsync(Dictionary<string, object?> current,
        JsonElement payload, SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        var proposed = new Dictionary<string, object?>(current, StringComparer.Ordinal);
        proposed["Title"] = PatchText(payload, "title", current["Title"], 200, false);
        proposed["Description"] = PatchText(payload, "description", current["Description"], 8000);
        if (payload.TryGetProperty("priorityId", out _)) proposed["Priority"] = (await LookupAsync(connection, transaction, "Priority", Id(payload, "priorityId"), null, ct)).Text("ID");
        if (payload.TryGetProperty("statusId", out _)) proposed["Status"] = (await LookupAsync(connection, transaction, "Status", Id(payload, "statusId"), null, ct)).Text("ID");
        if (payload.TryGetProperty("estimatedHours", out _)) proposed["Estimated_Hours"] = Decimal(payload, "estimatedHours", 0, 99999999);
        if (Display(current["Title"])?.Length > 200 || Display(current["Description"])?.Length > 8000)
            throw Locked("This ticket has content larger than the native edit contract. Edit it in the Portal.");
        return proposed;
    }

    private static IReadOnlyList<EmployeeActionField> Changes(Dictionary<string, object?> current, Dictionary<string, object?> proposed)
    {
        var changes = proposed.Where(p => Display(current[p.Key]) != Display(p.Value))
            .Select(p => new EmployeeActionField(p.Key, Display(current[p.Key]), Display(p.Value))).ToArray();
        if (changes.Length == 0) throw new EmployeeActionException(400, "The proposal does not change any supported field.");
        return changes;
    }

    internal static Task<Dictionary<string, object?>> LookupAsync(SqlConnection connection, SqlTransaction transaction,
        string type, int? id, string? description, CancellationToken ct) => OneAsync(connection, transaction, """
            SELECT ID,Description FROM dbo.vw_LookupV_Ticket
            WHERE Type=@Type AND ((@ID IS NOT NULL AND ID=CONVERT(nvarchar(50),@ID))
              OR (@ID IS NULL AND Description=@Description));
            """, ct, ("@Type", type), ("@ID", id), ("@Description", description));

    private static Task<List<Dictionary<string, object?>>> LinksAsync(int id, SqlConnection connection, SqlTransaction transaction, CancellationToken ct) =>
        RowsAsync(connection, transaction, """
            SELECT TicketEntryID ID,CASE WHEN WorkType='Assign' AND StartDateTime=EndDateTime AND ISNULL(IsApproved,0)=0
              AND ISNULL(InvoiceID,0)=0 AND ISNULL(InvDetID,0)=0 AND ISNULL(IsProcessed,0)=0 AND ISNULL(Is_Billed_From_AT,0)=0
              THEN 'assignment' ELSE 'work' END Kind,
              LastModifiedTime,IsApproved,InvoiceID,InvDetID FROM dbo.tbl_TicketEntries WITH(UPDLOCK,HOLDLOCK)
              WHERE TicketID=@ID AND ((IsActive=1 AND ISNULL(IsDeleted,0)=0) OR ISNULL(IsApproved,0)=1 OR ISNULL(InvoiceID,0)>0
                OR ISNULL(InvDetID,0)>0 OR ISNULL(IsProcessed,0)=1 OR ISNULL(Is_Billed_From_AT,0)=1 OR ISNULL(task_id_autotask,0)>0)
            UNION ALL SELECT ID,'invoice',CreatedDate,NULL,InvoiceID,NULL FROM dbo.tbl_Invoices_Tickets WITH(UPDLOCK,HOLDLOCK) WHERE TicketID=@ID
            UNION ALL SELECT TicketID,'child',LastModifiedTime,NULL,NULL,NULL FROM dbo.tbl_Tickets WITH(UPDLOCK,HOLDLOCK) WHERE ParentID=@ID AND IsActive=1
            ORDER BY Kind,ID;
            """, ct, ("@ID", id));
}
