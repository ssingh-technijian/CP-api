using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using static CpApis.Mcp.Core.Services.EmployeeNativeSql;

namespace CpApis.Mcp.Core.Services;

/// <summary>Uses the current native calendar save, preserving assignments and financial fields.</summary>
internal static class EmployeeTimeEntryActions
{
    private sealed record Prepared(Dictionary<string, object?> Ticket, Dictionary<string, object?>? Current,
        Dictionary<string, object?> Proposed, int OwnerId, List<Dictionary<string, object?>> Assignments,
        byte[] Hash, IReadOnlyList<EmployeeActionField> Changes);

    internal static async Task<EmployeeActionPlan> PrepareAsync(string operation, JsonElement payload,
        SqlConnection connection, SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        var plan = await BuildAsync(operation, payload, connection, transaction, actor, ct);
        var title=plan.Ticket.Text("Title")??"";if(title.Length>200)title=title[..200];
        return new(plan.Hash, $"{(operation == "time.create" ? "Create" : operation == "time.delete" ? "Deactivate" : "Edit")} time on {plan.Ticket.Text("ClientCode")}: ticket {plan.Ticket.Number("TicketID")} — {title}.", plan.Changes);
    }

    internal static async Task<EmployeeActionResult> ApplyAsync(string operation, JsonElement payload,
        SqlConnection connection, SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        var plan = await BuildAsync(operation, payload, connection, transaction, actor, ct);
        var id = plan.Current?.Number("TicketEntryID") ?? 0;
        if (operation == "time.delete")
        {
            await using var delete = Command(connection, transaction, "dbo.stp_TktEntry_Delete_V2"); delete.CommandType = CommandType.StoredProcedure;
            delete.Parameters.Add("@TicketEntryID", SqlDbType.Int).Value = id;
            delete.Parameters.Add("@UserID", SqlDbType.Int).Value = actor.UserId;
            var result = delete.Parameters.Add("@Result", SqlDbType.Int); result.Direction = ParameterDirection.Output;
            await delete.ExecuteNonQueryAsync(ct);
            var actual = await ReadEntryAsync(id, connection, transaction, ct, activeOnly: false);
            if (actual.Flag("IsActive") || Convert.ToInt32(result.Value, CultureInfo.InvariantCulture) != 1)
                throw Locked("Native time deletion did not deactivate the entry. The action was rolled back.");
            return new(operation, id, "Time entry deactivated and native state read back.");
        }
        var proposed = plan.Proposed;
        var root = new XElement("CalTimeEntrySaveV2",
            new XElement("TicketID", plan.Ticket.Number("TicketID")), new XElement("TicketEntryID", id),
            new XElement("CreatedUserID", actor.UserId), new XElement("CreatedBy", actor.Username),
            new XElement("StartTime", Display(proposed["StartDateTime"])), new XElement("EndTime", Display(proposed["EndDateTime"])),
            new XElement("Notes", proposed["Notes"]),
            new XElement("TimeZoneOffset", proposed["TimeZoneOffset"]),
            new XElement("AssignedUsers", plan.Assignments.Select(a => new XElement("UserID", a.Number("AssignToUserID")))));
        if (proposed["DriveTime"] is not null) root.Add(new XElement("DriveTime", proposed["DriveTime"]));
        foreach (var field in new[] { "WorkType", "RoleType", "InternalNotes", "AssignToUserID", "FollowUpDate", "Category" })
            if (proposed[field] is not null) root.Add(new XElement(field, Display(proposed[field])));
        if (proposed["EntryAssetID"] is not null) root.Add(new XElement("AssetID", proposed["EntryAssetID"]));
        if (proposed["EntryAssetTxt"] is not null) root.Add(new XElement("AssetName", proposed["EntryAssetTxt"]));
        // Omit Status: logging/editing work never completes/reopens a ticket or schedules customer notifications.
        var xml = await XmlAsync(connection, transaction, "dbo.stp_xml_TktEntry_Cal_TktEntry_Save_V4",
            "@XML_In", new XDocument(root), "@XML_Out", ct);
        var responses = xml.Root?.Elements("DatabaseResponse").ToArray() ?? [];
        if (responses.Length != 1 || responses[0].Element("Code")?.Value != "0")
            throw Locked("The native Portal rejected this time entry. Check duplicate notes, contract role and work/break rules in the Portal.");
        var ids = responses[0].Descendants("TicketEntryID").Select(e => int.TryParse(e.Value, out var number) ? number : 0).Distinct().ToArray();
        if (ids.Length != 1 || ids[0] <= 0 || id != 0 && ids[0] != id) throw Locked("The native Portal returned an ambiguous time-entry result.");
        id = ids[0];
        var actualEntry = await ReadEntryAsync(id, connection, transaction, ct);
        foreach (var field in new[] { "StartDateTime", "EndDateTime", "DriveTime", "Notes", "WorkType", "RoleType", "InternalNotes", "AssignToUserID", "FollowUpDate", "TimeZoneOffset", "EntryAssetID", "EntryAssetTxt", "Category" })
            if (Display(actualEntry[field]) != Display(proposed[field])) throw Locked("Native time-entry readback differs from the reviewed action; all changes were rolled back.");
        if (plan.Current is not null && actualEntry.Text("CreatedBy") != plan.Current.Text("CreatedBy"))
            throw Locked("Native time editing changed the original work owner; the action was rolled back.");
        var assignmentsAfter = await AssignmentsAsync(plan.Ticket.Number("TicketID"), connection, transaction, ct);
        if (!EmployeeActionJson.Same(Snapshot(plan.Assignments), Snapshot(assignmentsAfter)))
            throw Locked("The native save changed ticket assignments; the action was rolled back.");
        return new(operation, id, "Time entry saved and read back. Existing assignments and omitted fields were preserved. Billing quantities/prices are not certified by this work-log action.");
    }

    private static async Task<Prepared> BuildAsync(string operation, JsonElement payload, SqlConnection connection,
        SqlTransaction transaction, PortalEmployee actor, CancellationToken ct)
    {
        var create = operation == "time.create"; var delete = operation == "time.delete";
        Payload(payload, delete ? ["ticketEntryId"] : create
            ? ["ticketId", "startLocal", "endLocal", "notes", "driveHours", "workTypeId"]
            : ["ticketEntryId", "startLocal", "endLocal", "notes", "driveHours"]);
        await ClientPortalMenuAccess.RequireAsync(connection, transaction, actor, PortalMenu.Timesheet, ct);
        Dictionary<string, object?>? current = create ? null : await ReadEntryAsync(Id(payload, "ticketEntryId"), connection, transaction, ct);
        var ownerId = actor.UserId;
        if (current is not null)
        {
            var owner = await OneAsync(connection, transaction, "SELECT DirID FROM dbo.tbl_Directory WITH(UPDLOCK,HOLDLOCK) WHERE Username=@Username AND IsActive=1 AND DirectoryType='User';", ct, ("@Username", current["CreatedBy"]));
            ownerId = owner.Number("DirID");
            if (ownerId != actor.UserId && !actor.IsPortalAdministrator) throw new EmployeeAccessDeniedException("Only native administrators can propose changes to another employee's work.");
        }
        await RequireScopeAsync(connection, transaction, actor, delete ? "TktEntry.Delete" : "TktEntry.Edit", ownerId == actor.UserId, ownerId != actor.UserId, ct);
        await RequireScopeAsync(connection, transaction, actor, "EODTktEntry.Edit", ownerId == actor.UserId, false, ct);
        var ticketId = current?.Number("TicketID") ?? Id(payload, "ticketId");
        var ticket = await EmployeeTicketActions.ReadAsync(ticketId, connection, transaction, actor, ct);
        if (current is not null && (current.Number("ContractId") != ticket.Number("ContractID") || current.Text("RoleType") != ticket.Text("RoleType")))
            throw Locked("The entry's native contract or work role differs from the ticket. Use the native Portal to review this billing classification before editing.");
        if (current is not null && (current.Flag("IsApproved") || current.Number("InvoiceID") != 0 || current.Number("InvDetID") != 0
            || current.Flag("IsProcessed") || current.Flag("Is_Billed_From_AT") || current.Number("task_id_autotask") != 0
            || current["time_subitem_id"] is not null && current.Text("time_subitem_id") is not ("" or "0")
            || current.Text("ExchangeID") is not (null or "" or "0") || current.Number("MeetingTimePlaceID") != 0 || current.Flag("IsScheduled")
            || current.Text("WorkType") == "Assign"))
            throw Locked("Approved, invoiced, processed, externally linked, scheduled or assignment entries cannot be changed through this action.");
        var proposed = current is null ? new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Notes"] = RequiredText(payload, "notes", 8000), ["StartDateTime"] = LocalTime(payload, "startLocal"),
            ["EndDateTime"] = LocalTime(payload, "endLocal"), ["DriveTime"] = Decimal(payload, "driveHours", 0, 24),
            ["RoleType"] = ticket["RoleType"], ["WorkType"] = (await EmployeeTicketActions.LookupAsync(connection, transaction, "WorkType", Id(payload, "workTypeId"), null, ct)).Text("ID"),
            ["InternalNotes"] = null, ["AssignToUserID"] = actor.UserId, ["FollowUpDate"] = null,
            ["EntryAssetID"] = null, ["EntryAssetTxt"] = null, ["Category"] = null,
            // Native portal uses JavaScript-style local-to-UTC minutes: IST -330, Pacific 420/480.
            ["TimeZoneOffset"] = 0 // Derived below from native timezone functions for the actual work date.
        } : new Dictionary<string, object?>(current, StringComparer.Ordinal);
        if (!delete && current is not null)
        {
            proposed["Notes"] = PatchText(payload, "notes", current["Notes"], 8000, false);
            proposed["StartDateTime"] = LocalTime(payload, "startLocal", current.Date("StartDateTime"));
            proposed["EndDateTime"] = LocalTime(payload, "endLocal", current.Date("EndDateTime"));
            if (payload.TryGetProperty("driveHours", out _)) proposed["DriveTime"] = Decimal(payload, "driveHours", 0, 24);
        }
        var start = proposed.Date("StartDateTime"); var end = proposed.Date("EndDateTime");
        if (start is null || end is null || end <= start || (end.Value - start.Value).TotalHours > 24)
            throw new EmployeeActionException(400, "Use valid stored-local start/end times with a positive duration of at most24hours.");
        if(create||!delete&&(payload.TryGetProperty("startLocal",out _)||payload.TryGetProperty("endLocal",out _)))
        {
            var india=create?ClientPortalEmployeeTimeZonePolicy.Resolve(actor.LocationTopFilter)==PortalWorkRegion.India
                :Math.Abs(current!.Number("TimeZoneOffset"))==330;
            if(!create&&(current!["TimeZoneOffset"] is null||Math.Abs(current.Number("TimeZoneOffset")) is not(330 or 420 or 480)))
                throw Locked("The native entry timezone is missing or unsupported. Resolve it in the Portal.");
            var zone=await OneAsync(connection,transaction,"""
                SELECT DATEDIFF(minute,@Start,CASE WHEN @India=1 THEN dbo.fn_date_ConvertISTToUTC(@Start)
                  ELSE dbo.fn_date_ConvertPSTToUTC(@Start) END) OffsetMinutes;
                """,ct,("@Start",start.Value),("@India",india));
            proposed["TimeZoneOffset"]=zone["OffsetMinutes"];
        }
        if (proposed["TimeZoneOffset"] is null || Math.Abs(proposed.Number("TimeZoneOffset")) is not (330 or 420 or 480))
            throw Locked("This entry has an unsupported or missing native timezone. Use the Portal to resolve it.");
        await RequireOpenMonthAsync(connection, transaction, ownerId, start.Value, end.Value, ct);
        if (current is not null) await RequireOpenMonthAsync(connection, transaction, ownerId,
            current.Date("StartDateTime") ?? start.Value, current.Date("EndDateTime") ?? end.Value, ct);
        if (!delete)
        {
            var clock = await OneAsync(connection, transaction, """
                SELECT CASE WHEN @India=1 THEN dbo.fn_date_ConvertISTToUTC(@End) ELSE dbo.fn_date_ConvertPSTToUTC(@End) END EndUtc,
                  GETUTCDATE() NowUtc;
                """, ct, ("@India", Math.Abs(proposed.Number("TimeZoneOffset")) == 330), ("@End", end.Value));
            if (clock.Date("EndUtc") is null || clock.Date("EndUtc") > clock.Date("NowUtc")?.AddMinutes(5)
                || create && clock.Date("EndUtc") < clock.Date("NowUtc")?.AddHours(-10))
                throw Locked("Work logs cannot end in the future. New entries must also satisfy the native10hour backdate window.");
            var overlap = await RowsAsync(connection, transaction, """
                SELECT TicketEntryID FROM dbo.tbl_TicketEntries WITH(UPDLOCK,HOLDLOCK)
                WHERE CreatedBy=@Owner AND TicketEntryID<>@ID AND IsActive=1 AND ISNULL(IsDeleted,0)=0
                  AND EndDateTime>StartDateTime AND StartDateTime<@End AND EndDateTime>@Start;
                """, ct, ("@Owner", current?.Text("CreatedBy") ?? actor.Username), ("@ID", current?.Number("TicketEntryID") ?? 0),
                ("@Start", start), ("@End", end));
            if (overlap.Count > 0) throw Locked("This work period overlaps another active work log. Resolve the overlap in the native Portal before recording or editing this entry.");
        }
        var assignments = await AssignmentsAsync(ticketId, connection, transaction, ct);
        var dependencies = await RowsAsync(connection, transaction, """
            SELECT TicketEntryID,StartDateTime,EndDateTime,StartTimeUTC,EndTimeUTC,LastModifiedTime,IsActive,IsDeleted
            FROM dbo.tbl_TicketEntries WITH(UPDLOCK,HOLDLOCK) WHERE CreatedBy=@Owner
              AND StartDateTime>=DATEADD(hour,-8,@Start) AND StartDateTime<=DATEADD(hour,8,@End)
            ORDER BY TicketEntryID;
            """, ct, ("@Owner", current?.Text("CreatedBy") ?? actor.Username), ("@Start", start), ("@End", end));
        var contract = await RowsAsync(connection, transaction, "SELECT Contract_ID,Update_timestamp,ContractStatusTxt FROM dbo.tbl_Contracts WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID;", ct, ("@ID", ticket["ContractID"]));
        var prices = await RowsAsync(connection, transaction, "SELECT ID,IsActive,Abbr,NH_Price,AH_Price FROM dbo.tbl_Contracts_PriceList WITH(UPDLOCK,HOLDLOCK) WHERE Contract_ID=@ID ORDER BY ID;", ct, ("@ID", ticket["ContractID"]));
        var changes = delete ? new EmployeeActionField[] { new("TicketEntryID", Display(current!["TicketEntryID"]), Display(current["TicketEntryID"])), new("Active", "true", "false") }
            : new[] { "StartDateTime", "EndDateTime", "Notes", "DriveTime", "WorkType", "TimeZoneOffset" }
                .Where(key => current is null || Display(current[key]) != Display(proposed[key]))
                .Select(key => new EmployeeActionField(key, current is null ? null : Display(current[key]), Display(proposed[key]))).ToArray();
        if (changes.Length == 0) throw new EmployeeActionException(400, "The proposal does not change a supported field.");
        return new(ticket, current, proposed, ownerId, assignments, Snapshot(ticket, current, assignments, dependencies, contract, prices), changes);
    }

    private static Task<Dictionary<string, object?>> ReadEntryAsync(int id, SqlConnection connection, SqlTransaction transaction,
        CancellationToken ct, bool activeOnly = true) => OneAsync(connection, transaction, activeOnly ? EntrySql + " AND IsActive=1 AND ISNULL(IsDeleted,0)=0;" : EntrySql + ";", ct, ("@ID", id));
    private const string EntrySql = """
        SELECT TicketEntryID,TicketID,StartDateTime,EndDateTime,DriveTime,Notes,CreatedBy,WorkType,RoleType,InternalNotes,
          ModifiedBy,LastModifiedTime,InvoiceID,IsApproved,AssignToUserID,FollowUpDate,IsActive,Is_Billed_From_AT,
          task_id_autotask,time_subitem_id,TimeZoneOffset,IsProcessed,InvDetID,EntryAssetTxt,EntryAssetID,ExchangeID,
          MeetingTimePlaceID,IsScheduled,Category,IsDeleted,ContractId,StartTimeUTC,EndTimeUTC
        FROM dbo.tbl_TicketEntries WITH(UPDLOCK,HOLDLOCK) WHERE TicketEntryID=@ID
        """;
    private static Task<List<Dictionary<string, object?>>> AssignmentsAsync(int id, SqlConnection connection, SqlTransaction transaction, CancellationToken ct) =>
        RowsAsync(connection, transaction, """
            SELECT TicketEntryID,AssignToUserID FROM dbo.tbl_TicketEntries WITH(UPDLOCK,HOLDLOCK)
            WHERE TicketID=@ID AND WorkType='Assign' AND StartDateTime=EndDateTime AND IsActive=1 AND ISNULL(IsDeleted,0)=0
            ORDER BY TicketEntryID;
            """, ct, ("@ID", id));

    private static async Task RequireOpenMonthAsync(SqlConnection connection, SqlTransaction transaction, int ownerId,
        DateTime start, DateTime end, CancellationToken ct)
    {
        // Check both months for an entry crossing a month boundary. EOD time-off rows alone are not closure evidence.
        foreach (var month in new[] { new DateTime(start.Year, start.Month, 1), new DateTime(end.Year, end.Month, 1) }.Distinct())
        {
            var locks = await RowsAsync(connection, transaction, """
                SELECT ID FROM Payroll.tbl_India_Payroll WITH(UPDLOCK,HOLDLOCK)
                  WHERE UserID=@Owner AND MonthYear=@Month AND IsActive=1 AND (ApprovedDate IS NOT NULL OR ISNULL(ApprovedBy,0)>0)
                UNION ALL SELECT ID FROM dbo.tbl_NotificationProcessed WITH(UPDLOCK,HOLDLOCK)
                  WHERE NotificationFor IN('IndiaPayrollApproval','EOMAdminApproval')
                    AND NotificationID LIKE CONCAT('%;',FORMAT(@Month,'yyyy/M/d','en-US'));
                """, ct, ("@Owner", ownerId), ("@Month", month));
            if (locks.Count > 0) throw Locked("This work month is closed or approved for payroll/EOD processing. Use the native correction workflow.");
        }
    }
}
