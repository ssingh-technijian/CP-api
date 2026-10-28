using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace CpApis.Mcp.Core.Services;

internal sealed record EmployeeReportSubject(int DirectoryId, string DisplayName, string Office, string Username, string LocationTopFilter)
{
    internal object Public() => new { directoryId = DirectoryId, displayName = DisplayName, office = Office };
}
internal sealed class EmployeeReportSelectionException(string message, object candidates) : Exception(message)
{
    internal object Candidates { get; } = candidates;
}

/// <summary>Fixed, bounded native read queries. Subject IDs never replace the authenticated actor.</summary>
internal static class EmployeeConversationReports
{
    internal static async Task<(DateTimeOffset Now, TimeZoneInfo NativeZone)> ClockAsync(SqlConnection c, CancellationToken ct)
    {
        await using var q = c.CreateCommand(); q.CommandTimeout = 10;
        q.CommandText = "SELECT SYSUTCDATETIME(),CURRENT_TIMEZONE_ID();";
        await using var r = await q.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) throw new InvalidDataException("Native clock unavailable.");
        return (new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc)), TimeZoneInfo.FindSystemTimeZoneById(r.GetString(1)));
    }

    internal static async Task<IReadOnlyList<EmployeeReportSubject>> FindAsync(SqlConnection c, PortalEmployee actor,
        string? search, int? directoryId, CancellationToken ct)
    {
        await ClientPortalMenuAccess.RequireAsync(c, null, actor, PortalMenu.EodView, ct);
        var permissions = await ClientPortalEmployeeContext.ReadPermissionsAsync(c, null, actor.UserId, "EOD.List", ct);
        if (!ClientPortalPermissionPolicy.Allows(permissions, true)) throw new EmployeeAccessDeniedException("Native EOD access denied.");
        var all = ClientPortalPermissionPolicy.Allows(permissions, false);
        if (directoryId is <= 0) throw new EmployeeActionException(400, "Use a positive native employee directory ID.");
        if (search is not null && (search.Trim().Length is < 2 or > 80))
            throw new EmployeeActionException(400, "Search by2to80characters of employee name. Do not guess an ID.");
        var pattern = search is null ? null : "%" + search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        var rows = await RowsAsync(c, """
            SELECT TOP(21) v.UserID DirectoryId,LEFT(v.UserName,200) DisplayName,
              CASE WHEN COUNT(DISTINCT v.Office)>1 THEN 'Multiple offices' ELSE MAX(LEFT(v.Office,100)) END Office,
              d.Username,d.LocationTopFilter
            FROM dbo.vw_PODUser_Tech_Workshift_Get v JOIN dbo.tbl_Directory d ON d.DirID=v.UserID
            WHERE d.IsActive=1 AND d.DirectoryType='User' AND d.ClientCode='Technijian' AND d.Role IN('T1','T2','T3')
              AND (@All=1 OR v.UserID=@Actor) AND (@ID IS NULL OR v.UserID=@ID)
              AND (@Search IS NULL OR v.UserName LIKE @Search)
              AND NOT EXISTS(SELECT 1 FROM dbo.tbl_Directory other WHERE other.IsActive=1 AND other.DirID<>d.DirID AND other.Username=d.Username)
            GROUP BY v.UserID,v.UserName,d.Username,d.LocationTopFilter ORDER BY v.UserName,v.UserID;
            """, ct, ("@All", all), ("@Actor", actor.UserId), ("@ID", directoryId), ("@Search", pattern));
        return rows.Select(r => new EmployeeReportSubject(Convert.ToInt32(r["DirectoryId"]), Text(r,"DisplayName") ?? "",
            Text(r,"Office") ?? "", Text(r,"Username") ?? "", Text(r,"LocationTopFilter") ?? "")).ToArray();
    }

    internal static async Task<EmployeeReportSubject> SubjectAsync(SqlConnection c, PortalEmployee actor,
        string? name, int? id, bool ticketReport, CancellationToken ct)
    {
        if (ticketReport && name is null && (id is null || id == actor.UserId))
            return new(actor.UserId, actor.Username, "", actor.Username, actor.LocationTopFilter);
        if (ticketReport && !actor.IsPortalAdministrator)
            throw new EmployeeAccessDeniedException("Another employee's ticket activity requires native ticket administrator access.");
        var matches = await FindAsync(c, actor, name, id ?? (name is null ? actor.UserId : null), ct);
        if (matches.Count != 1)
            throw new EmployeeReportSelectionException(matches.Count == 0
                ? "No matching employee is visible in your native EOD directory. Check the name; do not infer a missing employee ID."
                : "More than one employee matches. Ask the user to choose a full name or native directory ID.",
                new { matchStatus = matches.Count == 0 ? "not_found" : "ambiguous", hasMore = matches.Count > 20,
                    employees = matches.Take(20).Select(s => s.Public()).ToArray() });
        return matches[0];
    }

    internal static async Task<List<Dictionary<string,object?>>> RowsAsync(SqlConnection c, string sql, CancellationToken ct,
        params (string Name,object? Value)[] args)
    {
        await using var q=c.CreateCommand();q.CommandTimeout=10;q.CommandText="SET LOCK_TIMEOUT 5000;"+sql;
        foreach(var (name,value) in args)
            if(value is DateTime date)q.Parameters.Add(name,SqlDbType.DateTime2).Value=date;
            else q.Parameters.AddWithValue(name,value??DBNull.Value);
        await using var r=await q.ExecuteReaderAsync(ct);var rows=new List<Dictionary<string,object?>>();
        while(await r.ReadAsync(ct))
        {
            if(rows.Count>=1000)throw new InvalidDataException("Native read exceeded its bound.");
            var row=new Dictionary<string,object?>(StringComparer.Ordinal);
            for(var i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    internal static string? Text(IReadOnlyDictionary<string,object?> row,string name)=>row[name] is null?null:Convert.ToString(row[name],CultureInfo.InvariantCulture);
    internal static object Window(EmployeeReportWindow window)
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId);
        var localFrom=TimeZoneInfo.ConvertTime(window.FromUtc,zone);
        var localUntil=TimeZoneInfo.ConvertTime(window.UntilUtc,zone);
        return new {period=window.Period,fromUtc=window.FromUtc,untilUtcExclusive=window.UntilUtc,
            fromLocal=localFrom,untilLocalExclusive=localUntil,
            fromDate=localFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            throughDate=(window.UntilUtc<=window.FromUtc?localFrom:TimeZoneInfo.ConvertTime(window.UntilUtc.AddTicks(-1),zone)).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            timeZone=window.TimeZoneId,last2DaysBasis="last_2_days is the preceding48hours; explicit dates are inclusive employee-local calendar dates"};
    }

    internal const string ActivitySql = """
        DECLARE @Events TABLE(Kind varchar(12),TicketID int,Title nvarchar(200),EventAt datetime,RecordedBy nvarchar(50),AfterStatus nvarchar(50),AfterLabel nvarchar(100));
        DECLARE @Open TABLE(TicketID int PRIMARY KEY);
        INSERT @Open SELECT DISTINCT TicketID FROM dbo.fn_dt_Tkt_Cal_Tkt_MyOpnTkt_List_Get(@Employee);
        INSERT @Events SELECT 'created',TicketID,LEFT(Title,200),CreateDate,CreatedBy,Status,StatusTxt
        FROM dbo.tbl_Tickets WHERE CreatedBy=@Username AND CreateDate>=@From AND CreateDate<@Until
          AND (@Client IS NULL OR ClientID=@Client);
        INSERT @Events SELECT 'new_open',t.TicketID,LEFT(t.Title,200),t.CreateDate,t.CreatedBy,t.Status,t.StatusTxt
        FROM dbo.tbl_Tickets t JOIN @Open o ON o.TicketID=t.TicketID
        WHERE t.CreateDate>=@From AND t.CreateDate<@Until AND (@Client IS NULL OR t.ClientID=@Client);
        SELECT DISTINCT TicketID INTO #McpActivityTickets FROM dbo.tbl_Tickets_Audit WHERE [Date]>=@From AND [Date]<@Until;
        CREATE UNIQUE CLUSTERED INDEX IX_McpActivityTickets ON #McpActivityTickets(TicketID);
        ;WITH Snapshots AS(
          SELECT CONVERT(bigint,a.Audit_ID) SequenceID,a.TicketID,a.[Date] EventAt,a.Status,a.StatusTxt,a.CreatedBy,a.AssignTo_DirID,a.ModifiedBy,LEFT(a.Title,200) Title,a.ClientID
          FROM dbo.tbl_Tickets_Audit a JOIN #McpActivityTickets k ON k.TicketID=a.TicketID
          UNION ALL
          SELECT CONVERT(bigint,9223372036854775807),t.TicketID,NULL,t.Status,t.StatusTxt,t.CreatedBy,t.AssignTo_DirID,t.ModifiedBy,LEFT(t.Title,200),t.ClientID
          FROM dbo.tbl_Tickets t JOIN #McpActivityTickets k ON k.TicketID=t.TicketID
        ), Sequenced AS(
          SELECT *,LEAD(SequenceID) OVER(PARTITION BY TicketID ORDER BY SequenceID) NextId,
            LEAD(Status) OVER(PARTITION BY TicketID ORDER BY SequenceID) AfterStatus,
            LEAD(StatusTxt) OVER(PARTITION BY TicketID ORDER BY SequenceID) AfterLabel,
            LEAD(ModifiedBy) OVER(PARTITION BY TicketID ORDER BY SequenceID) RecordedBy,
            LEAD(Title) OVER(PARTITION BY TicketID ORDER BY SequenceID) AfterTitle,
            LEAD(ClientID) OVER(PARTITION BY TicketID ORDER BY SequenceID) AfterClient
          FROM Snapshots
        ), Transitions AS(
          SELECT TicketID,EventAt,Status BeforeStatus,StatusTxt BeforeLabel,CreatedBy TicketCreator,AssignTo_DirID AssignedId,
            AfterStatus,AfterLabel,RecordedBy,COALESCE(AfterTitle,Title) Title,COALESCE(AfterClient,ClientID) ClientID,
            CASE WHEN NextId IS NULL THEN 0 ELSE 1 END HasAfter
          FROM Sequenced WHERE EventAt>=@From AND EventAt<@Until
        ), Classified AS(
          SELECT *,CASE WHEN TRY_CONVERT(int,BeforeStatus) IS NOT NULL THEN CASE WHEN TRY_CONVERT(int,BeforeStatus) IN(1272,1275) THEN 1 ELSE 0 END
              WHEN LTRIM(RTRIM(BeforeLabel)) IN('Completed','Closed') THEN 1 ELSE 0 END WasClosed,
            CASE WHEN TRY_CONVERT(int,AfterStatus) IS NOT NULL THEN CASE WHEN TRY_CONVERT(int,AfterStatus) IN(1272,1275) THEN 1 ELSE 0 END
              WHEN LTRIM(RTRIM(AfterLabel)) IN('Completed','Closed') THEN 1 ELSE 0 END IsClosed
          FROM Transitions WHERE @Client IS NULL OR ClientID=@Client
        )
        INSERT @Events SELECT CASE WHEN HasAfter=0 THEN 'unresolved' WHEN NULLIF(LTRIM(RTRIM(RecordedBy)),'') IS NULL THEN 'unattributed'
          WHEN WasClosed=0 AND IsClosed=1 THEN 'closed' ELSE 'reopened' END,
          TicketID,LEFT(Title,200),EventAt,RecordedBy,AfterStatus,AfterLabel
        FROM Classified WHERE (HasAfter=1 AND WasClosed<>IsClosed AND (RecordedBy=@Username OR
          (NULLIF(LTRIM(RTRIM(RecordedBy)),'') IS NULL AND WasClosed=0 AND IsClosed=1 AND (TicketCreator=@Username OR AssignedId=@Employee))))
          OR (HasAfter=0 AND (TicketCreator=@Username OR AssignedId=@Employee))
        OPTION(RECOMPILE);
        SELECT Kind,COUNT(*) EventCount,COUNT(DISTINCT TicketID) TicketCount FROM @Events GROUP BY Kind
        UNION ALL SELECT 'current_open',COUNT(*),COUNT(*) FROM @Open o JOIN dbo.tbl_Tickets t ON t.TicketID=o.TicketID WHERE @Client IS NULL OR t.ClientID=@Client;
        SELECT Kind,TicketID,Title,EventAt,RecordedBy,AfterStatus,AfterLabel FROM @Events ORDER BY EventAt DESC,TicketID DESC,Kind
          OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
        DROP TABLE #McpActivityTickets;
        """;

    internal const string EodSql = """
        DROP TABLE IF EXISTS #McpEodEntries;
        SELECT * INTO #McpEodEntries FROM(
          SELECT DISTINCT l.TicketEntryID,l.TicketID,LEFT(t.Title,200) Title,l.ClientCode,l.StartDate,
            LEFT(CONVERT(nvarchar(max),l.Notes),1000) Notes,
            CONVERT(bit,CASE WHEN LEN(CONVERT(nvarchar(max),l.Notes))>1000 THEN 1 ELSE 0 END) NotesTruncated,
            te.StartDateTime,te.EndDateTime,CONVERT(decimal(18,4),ISNULL(te.DriveTime,0)) DriveHours,
            CONVERT(date,l.StartDate) WorkDate
          FROM dbo.vw_TktEntry_EOD_List l JOIN dbo.vw_TktEntry te ON te.TicketEntryID=l.TicketEntryID
            JOIN dbo.tbl_Tickets t ON t.TicketID=l.TicketID
          WHERE l.EmpID=@Employee AND l.StartDate>=@From AND l.StartDate<@Until AND te.StartDateTime<te.EndDateTime
        ) filtered;
        CREATE UNIQUE CLUSTERED INDEX IX_McpEodEntries ON #McpEodEntries(TicketEntryID);
        ;WITH Points AS(
          SELECT WorkDate,StartDateTime Point FROM #McpEodEntries UNION SELECT WorkDate,EndDateTime FROM #McpEodEntries
        ), Segments AS(
          SELECT WorkDate,Point SegmentStart,LEAD(Point) OVER(PARTITION BY WorkDate ORDER BY Point) SegmentEnd FROM Points
        ), Allocations AS(
          SELECT e.TicketEntryID,SUM(DATEDIFF(minute,s.SegmentStart,s.SegmentEnd)) MinutesWorked
          FROM Segments s CROSS APPLY(SELECT TOP(1) e.TicketEntryID FROM #McpEodEntries e
            WHERE e.WorkDate=s.WorkDate AND e.StartDateTime<=s.SegmentStart AND e.EndDateTime>=s.SegmentEnd
            ORDER BY e.StartDateTime,e.EndDateTime,e.TicketEntryID) e
          WHERE s.SegmentEnd>s.SegmentStart GROUP BY e.TicketEntryID
        ), Allocated AS(
          SELECT e.*,CAST(ISNULL(a.MinutesWorked,0)/60.0 AS decimal(10,2)) AllocatedWorkHours
          FROM #McpEodEntries e LEFT JOIN Allocations a ON a.TicketEntryID=e.TicketEntryID
        )
        SELECT TicketEntryID,TicketID,Title,ClientCode,WorkDate,StartDateTime,EndDateTime,Notes,NotesTruncated,
          AllocatedWorkHours,DriveHours,COUNT(*) OVER() TotalCount,
          SUM(AllocatedWorkHours) OVER() TotalAllocatedWorkHours,SUM(DriveHours) OVER() TotalDriveHours
        FROM Allocated ORDER BY StartDateTime,TicketEntryID OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
        DROP TABLE #McpEodEntries;
        """;

    internal const string HistorySql = """
        ;WITH Snapshots AS(
          SELECT CONVERT(bigint,Audit_ID) SequenceID,TicketID,Status,StatusTxt,Title,Priority,ModifiedBy,[Date] SnapshotAt
          FROM dbo.tbl_Tickets_Audit WHERE TicketID=@Ticket
          UNION ALL SELECT CONVERT(bigint,9223372036854775807),TicketID,Status,StatusTxt,Title,Priority,ModifiedBy,NULL
          FROM dbo.tbl_Tickets WHERE TicketID=@Ticket
        ), History AS(
          SELECT *,LEAD(Status) OVER(ORDER BY SequenceID) AfterStatus,LEAD(StatusTxt) OVER(ORDER BY SequenceID) AfterLabel,
            LEAD(ModifiedBy) OVER(ORDER BY SequenceID) RecordedAfterModifier,LEAD(Title) OVER(ORDER BY SequenceID) AfterTitle
          FROM Snapshots
        )
        SELECT SequenceID,SnapshotAt,Status BeforeStatus,StatusTxt BeforeLabel,LEFT(Title,200) BeforeTitle,
          AfterStatus,AfterLabel,RecordedAfterModifier,LEFT(AfterTitle,200) AfterTitle,COUNT(*) OVER() TotalCount
        FROM History WHERE SnapshotAt IS NOT NULL ORDER BY SequenceID DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
        """;
}
