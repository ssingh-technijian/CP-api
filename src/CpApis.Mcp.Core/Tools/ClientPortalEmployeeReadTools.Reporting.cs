using System.ComponentModel;
using System.Data;
using System.Globalization;
using CpApis.Mcp.Core.Services;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace CpApis.Mcp.Core.Tools;

public sealed partial class ClientPortalEmployeeReadTools
{
    [McpServerTool(Name="client_portal_find_employees",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Resolve native employee names to directory IDs, for example Rahul or Rahul Sharma. Uses the Portal EOD employee directory and your Deny-first EOD.List scope; Self cannot search coworkers. Returns names, IDs and offices only. Ask the user to choose when multiple employees match; never ask for an ID before trying this lookup. This selects report subjects, never the signed-in actor.")]
    public Task<EmployeeToolResult<object>> FindEmployeesAsync(string search,CancellationToken cancellationToken=default)=>ReportAsync(async(c,a)=>
    {
        if(string.IsNullOrWhiteSpace(search))throw new EmployeeActionException(400,"Search by2to80characters of employee name.");
        var matches=await EmployeeConversationReports.FindAsync(c,a,search,null,cancellationToken);
        return new {returnedCount=Math.Min(matches.Count,20),hasMore=matches.Count>20,
            matchStatus=matches.Count==0?"not_found":matches.Count==1?"unique":"ambiguous",
            employees=matches.Take(20).Select(s=>s.Public()).ToArray(),basis="Native EOD employee directory, active Technijian profiles and current caller scope"};
    },cancellationToken);

    [McpServerTool(Name="client_portal_ticket_activity",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Answer tickets created or closed/completed by me in a date period, including follow-up 'and how many created last2days?'. Defaults to the signed-in employee and preceding48hours, with exact dates returned. Period: today,yesterday,last_2_days,last_7_days,this_week,last_week,this_month,last_month,custom. Custom fromDate/throughDate are inclusive YYYY-MM-DD, max31days. Created means native CreatedBy; newlyCreatedCurrentOpenTicketCount separately filters creation dates of current native My Open Tickets. Closed includes native Completed and Closed transitions from audit, not current status/last-modified guesses. Counts are distinct tickets; event counts expose reopen/reclose. Another employee requires native ticket administrator and EOD scope. clientDirectoryId optionally limits an exact client. Missing attribution is disclosed, never treated as proof of no closures. Continue with offset=nextOffset; counts already cover the full query, so never add page totals. Current open count is as of now, not historical open-at-period-end.")]
    public Task<EmployeeToolResult<object>> TicketActivityAsync(string period="last_2_days",string? fromDate=null,string? throughDate=null,
        int? directoryId=null,int? clientDirectoryId=null,int limit=20,int offset=0,CancellationToken cancellationToken=default)=>ReportAsync(async(c,a)=>
    {
        if(clientDirectoryId is <=0)throw new EmployeeActionException(400,"Use a positive native client directory ID.");
        ValidateOffset(offset);
        await ClientPortalMenuAccess.RequireAsync(c,null,a,PortalMenu.TicketSearch,cancellationToken);
        if(clientDirectoryId is not null)
        {
            var client=await EmployeeConversationReports.RowsAsync(c,"SELECT DirID FROM dbo.tbl_Directory WHERE DirID=@Client AND DirectoryType IN('Client','Location');",cancellationToken,("@Client",clientDirectoryId));
            if(client.Count!=1)throw new EmployeeActionException(404,"That native client/location ID was not found. Resolve the client name before interpreting a count.");
        }
        var subject=await EmployeeConversationReports.SubjectAsync(c,a,null,directoryId,true,cancellationToken);
        var clock=await EmployeeConversationReports.ClockAsync(c,cancellationToken);
        var window=EmployeeReportPeriods.Resolve(period,fromDate,throughDate,clock.Now,EmployeeReportPeriods.EmployeeZone(subject.LocationTopFilter));
        var take=Math.Clamp(limit,1,50);
        await using var q=c.CreateCommand();q.CommandTimeout=10;q.CommandText="SET LOCK_TIMEOUT 5000;"+EmployeeConversationReports.ActivitySql;
        q.Parameters.Add("@Username",SqlDbType.NVarChar,50).Value=subject.Username;
        q.Parameters.AddWithValue("@Employee",subject.DirectoryId);q.Parameters.AddWithValue("@Client",(object?)clientDirectoryId??DBNull.Value);
        q.Parameters.Add("@From",SqlDbType.DateTime2).Value=window.NativeFrom(clock.NativeZone);
        q.Parameters.Add("@Until",SqlDbType.DateTime2).Value=window.NativeUntil(clock.NativeZone);
        q.Parameters.AddWithValue("@Take",take+1);
        q.Parameters.AddWithValue("@Skip",offset);
        await using var r=await q.ExecuteReaderAsync(cancellationToken);
        var counts=new Dictionary<string,(int Events,int Tickets)>(StringComparer.Ordinal);
        while(await r.ReadAsync(cancellationToken))counts[r.GetString(0)]=(r.GetInt32(1),r.GetInt32(2));
        var entries=new List<object>();if(!await r.NextResultAsync(cancellationToken))throw new InvalidDataException("Activity details missing.");
        while(await r.ReadAsync(cancellationToken))
        {
            if(entries.Count>50)throw new InvalidDataException("Activity response exceeded its bound.");
            var date=r.GetDateTime(3);
            entries.Add(new{kind=r.GetString(0),ticketId=r.GetInt32(1),title=r.IsDBNull(2)?"":r.GetString(2),
                eventNativeLocal=date.ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture),
                eventUtc=UtcOrUnknown(date,clock.NativeZone),afterStatus=r.IsDBNull(6)?null:r.GetString(6)});
        }
        int Tickets(string kind)=>counts.TryGetValue(kind,out var v)?v.Tickets:0;
        int Events(string kind)=>counts.TryGetValue(kind,out var v)?v.Events:0;
        return new{employee=subject.Public(),window=EmployeeConversationReports.Window(window),clientDirectoryId,
            createdTicketCount=Tickets("created"),createdByEmployeeTicketCount=Tickets("created"),
            currentOpenTicketCount=Tickets("current_open"),newlyCreatedCurrentOpenTicketCount=Tickets("new_open"),
            currentOpenAsOfUtc=clock.Now,
            createdCountBasis="createdByEmployeeTicketCount is CreatedBy this employee. newlyCreatedCurrentOpenTicketCount is creation-date-filtered membership in native My Open Tickets, regardless of creator. Distinguish both when a follow-up is ambiguous.",
            closedTicketCount=Tickets("closed"),closureEventCount=Events("closed"),
            reopenedTicketCount=Tickets("reopened"),unattributedOwnedClosureCount=Events("unattributed"),
            unresolvedOwnedAfterStateCount=Events("unresolved"),attributionCompleteForOwnedTransitions=Events("unattributed")==0&&Events("unresolved")==0,
            returnedCount=Math.Min(entries.Count,take),hasMore=entries.Count>take,events=entries.Take(take).ToArray(),
            nativeTimestampZone=clock.NativeZone.Id,attributionBasis="After-state native ModifiedBy, not the modifier in the prior audit snapshot. Counts describe recorded attributable transitions; absent attribution is not proof nobody acted.",
            closedBasis="Completed(1272) and Closed(1275) are terminal. Count distinct tickets in the period even if later reopened; repeated closure events are separate.",
            totalCountsAreCompleteForQuery=true};
    },cancellationToken,offset);

    [McpServerTool(Name="client_portal_get_eod",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Retrieve a native End of Day work-log report by employee name, for example yesterday EOD of Rahul. Omit employee/directoryId for yourself. Resolves the name with your native EOD.List scope and asks on ambiguity; do not ask the user for time-entry IDs. Period defaults yesterday in the employee's native work region; supports today,yesterday,last_2_days,last_7_days,this_week,last_week,this_month,last_month,custom (inclusive YYYY-MM-DD max31days). Returns exact dates, work notes, client/ticket IDs, complete totals, bounded entries. Uses native overlap allocation and keeps drive hours separate. Continue with offset=nextOffset when hasMore; full totals repeat on each page and must never be added. If notesTruncated and full notes are requested, retrieve the supplied ticketEntryId with the exact time-entry tool. This is not payroll, verified commercial billing, or proof an EOD email was sent/approved.")]
    public Task<EmployeeToolResult<object>> EodAsync(string? employee=null,int? directoryId=null,string period="yesterday",
        string? fromDate=null,string? throughDate=null,int limit=50,int offset=0,CancellationToken cancellationToken=default)=>ReportAsync(async(c,a)=>
    {
        ValidateOffset(offset);
        var subject=await EmployeeConversationReports.SubjectAsync(c,a,employee,directoryId,false,cancellationToken);
        var clock=await EmployeeConversationReports.ClockAsync(c,cancellationToken);
        var zone=EmployeeReportPeriods.EmployeeZone(subject.LocationTopFilter);
        var window=EmployeeReportPeriods.Resolve(period,fromDate,throughDate,clock.Now,zone);
        var take=Math.Clamp(limit,1,50);
        var rows=await EmployeeConversationReports.RowsAsync(c,EmployeeConversationReports.EodSql,cancellationToken,
            ("@Employee",subject.DirectoryId),("@From",window.NativeFrom(zone)),("@Until",window.NativeUntil(zone)),("@Take",take),("@Skip",offset));
        if(rows.Select(row=>Convert.ToInt32(row["TicketEntryID"])).Distinct().Count()!=rows.Count)
            throw new InvalidDataException("Native EOD returned duplicate entry IDs.");
        var totals=rows;
        if(rows.Count==0&&offset>0)totals=await EmployeeConversationReports.RowsAsync(c,EmployeeConversationReports.EodSql,cancellationToken,
            ("@Employee",subject.DirectoryId),("@From",window.NativeFrom(zone)),("@Until",window.NativeUntil(zone)),("@Take",1),("@Skip",0));
        var total=totals.Count==0?0:Convert.ToInt32(totals[0]["TotalCount"]);
        var entries=rows.Select(row=>new{ticketEntryId=row["TicketEntryID"],ticketId=row["TicketID"],title=row["Title"],clientCode=row["ClientCode"],
            workDate=((DateTime)row["WorkDate"]!).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            startLocal=((DateTime)row["StartDateTime"]!).ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture),
            endLocal=((DateTime)row["EndDateTime"]!).ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture),notes=row["Notes"],notesTruncated=row["NotesTruncated"],
            allocatedWorkHours=row["AllocatedWorkHours"],driveHours=row["DriveHours"]}).ToArray();
        return new{employee=subject.Public(),window=EmployeeConversationReports.Window(window),totalEntryCount=total,returnedCount=rows.Count,hasMore=total>offset+rows.Count,
            totalAllocatedWorkHours=totals.Count==0?0m:Convert.ToDecimal(totals[0]["TotalAllocatedWorkHours"]),
            totalDriveHours=totals.Count==0?0m:Convert.ToDecimal(totals[0]["TotalDriveHours"]),entries,
            basis="Native EOD work-log view and native per-day overlap allocation; cross-midnight work belongs to its native start day. Stored local timestamps have no asserted UTC offset. Entry totals cover the full query, not only the preview. Drive hours are separate.",
            eodEmailDeliveryVerified=false,payrollOrCommercialBillingVerified=false};
    },cancellationToken,offset);

    [McpServerTool(Name="client_portal_get_ticket_history",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Inspect creation time and native audit status/title transitions of an exact ticket you can read. Use for when/by-whom closure or reopening questions and before describing ticket history. Shows prior and after snapshots separately; after-state ModifiedBy is the recorded actor. Bounded preview includes full snapshot count. Continue using offset=nextOffset. Do not infer closure time from current status or last-modified time alone.")]
    public Task<EmployeeToolResult<object>> TicketHistoryAsync(int ticketId,int limit=25,int offset=0,CancellationToken cancellationToken=default)=>ReportAsync(async(c,a)=>
    {
        ValidateOffset(offset);
        if(ticketId<=0)throw new EmployeeActionException(400,"Use a positive ticket ID.");
        var current=await EmployeeTicketActions.ReadAsync(ticketId,c,null!,a,cancellationToken);
        var clock=await EmployeeConversationReports.ClockAsync(c,cancellationToken);
        var created=await EmployeeConversationReports.RowsAsync(c,"SELECT CreateDate,CreatedBy FROM dbo.tbl_Tickets WHERE TicketID=@Ticket;",cancellationToken,("@Ticket",ticketId));
        var take=Math.Clamp(limit,1,50);
        var rows=await EmployeeConversationReports.RowsAsync(c,EmployeeConversationReports.HistorySql,cancellationToken,("@Ticket",ticketId),("@Take",take),("@Skip",offset));
        var snapshotCount=rows.Count>0?Convert.ToInt32(rows[0]["TotalCount"]):0;
        if(rows.Count==0&&offset>0)
        {
            var count=await EmployeeConversationReports.RowsAsync(c,"SELECT COUNT(*) TotalCount FROM dbo.tbl_Tickets_Audit WHERE TicketID=@Ticket AND [Date] IS NOT NULL;",cancellationToken,("@Ticket",ticketId));
            snapshotCount=Convert.ToInt32(count[0]["TotalCount"]);
        }
        var events=rows.Select(row=>new{auditId=row["SequenceID"],eventNativeLocal=((DateTime)row["SnapshotAt"]!).ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture),
            eventUtc=UtcOrUnknown((DateTime)row["SnapshotAt"]!,clock.NativeZone),beforeStatus=row["BeforeLabel"],afterStatus=row["AfterLabel"],
            becameClosed=!EmployeeReportPeriods.IsClosed(EmployeeConversationReports.Text(row,"BeforeStatus"),EmployeeConversationReports.Text(row,"BeforeLabel"))
                &&EmployeeReportPeriods.IsClosed(EmployeeConversationReports.Text(row,"AfterStatus"),EmployeeConversationReports.Text(row,"AfterLabel")),
            reopened=EmployeeReportPeriods.IsClosed(EmployeeConversationReports.Text(row,"BeforeStatus"),EmployeeConversationReports.Text(row,"BeforeLabel"))
                &&!EmployeeReportPeriods.IsClosed(EmployeeConversationReports.Text(row,"AfterStatus"),EmployeeConversationReports.Text(row,"AfterLabel")),
            beforeTitle=row["BeforeTitle"],afterTitle=row["AfterTitle"],recordedAfterModifier=row["RecordedAfterModifier"]}).ToArray();
        var createdDate=created.Count==1?created[0]["CreateDate"] as DateTime?:null;
        return new{ticketId,title=current["Title"],currentStatus=current["StatusTxt"],createdNativeLocal=createdDate?.ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture),
            createdUtc=createdDate is null?null:UtcOrUnknown(createdDate.Value,clock.NativeZone),createdBy=created.Count==1?created[0]["CreatedBy"]:null,
            totalSnapshotCount=snapshotCount,returnedCount=rows.Count,
            hasMore=snapshotCount>offset+rows.Count,events,nativeTimestampZone=clock.NativeZone.Id,
            basis="Native audit rows store the BEFORE-update state. The next snapshot/current row supplies AFTER-state and recorded modifier. A null UTC timestamp is not asserted across an ambiguous native daylight-saving hour."};
    },cancellationToken,offset);

    private async Task<EmployeeToolResult<object>> ReportAsync(Func<SqlConnection,PortalEmployee,Task<object>> query,CancellationToken ct,int offset=0)
    {
        try{return EmployeeReportResponseBounds.Bound(await ReadAsync(query,ct),offset);}
        catch(EmployeeReportSelectionException e){return EmployeeReportResponseBounds.Bound(new(false,e.Candidates,e.Message));}
        catch(EmployeeActionException e){return new(false,null,e.Message);}
        catch(InvalidDataException){return new(false,null,"Native report data could not be returned safely. Narrow the period or preview limit; no verified result is available.");}
        catch(Exception e)when(e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentOutOfRangeException)
        {return new(false,null,"The native date/time basis is unavailable or invalid. No verified report is available.");}
    }

    private static void ValidateOffset(int offset)
    {
        if(offset is <0 or >5000)throw new EmployeeActionException(400,"Report offset must be between0and5000. Continue using nextOffset from the previous page.");
    }

    private static DateTime? UtcOrUnknown(DateTime nativeLocal,TimeZoneInfo zone)=>zone.IsAmbiguousTime(nativeLocal)||zone.IsInvalidTime(nativeLocal)
        ?null:TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nativeLocal,DateTimeKind.Unspecified),zone);
}
