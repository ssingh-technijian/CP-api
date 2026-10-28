using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CpApis.Mcp.Core.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;

namespace CpApis.Mcp.Core.Tools;

public sealed record EmployeeToolResult<T>(bool Success, T? Data, string? Error);
public sealed record EmployeeTicket(int TicketId, string Title, string Client, string Status);
public sealed record EmployeeTicketPage(int TotalCount, int ReturnedCount, IReadOnlyList<EmployeeTicket> Tickets)
{
    public int Offset {get;init;}
    public bool HasMore {get;init;}
    public int? NextOffset {get;init;}
    public bool OutputBudgetApplied {get;init;}
}
public sealed record EmployeeTimeEntry(int TicketEntryId, int TicketId, string Title, string? StartLocal,
    string? EndLocal, decimal? RecordedDurationHours, decimal DriveHours, bool Approved, bool LinkedToInvoice);

/// <summary>Curated employee reads. No arbitrary SQL/procedure, actor, role, or tenant arguments.</summary>
[McpServerToolType]
public sealed partial class ClientPortalEmployeeReadTools(ClientPortalEmployeeContext employees, IConfiguration configuration)
{
    [McpServerTool(Name = "client_portal_whoami", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Show your verified Microsoft-to-Client-Portal employee identity and current native role. Uses your sign-in; no email or actor ID input is accepted.")]
    public Task<EmployeeToolResult<object>> WhoAmIAsync(CancellationToken cancellationToken) =>
        ReadAsync<object>((_, actor) => Task.FromResult<object>(new
        {
            actor.UserId, actor.Username, actor.Role,
            identitySource = "Microsoft delegated sign-in + active native Client Portal directory",
            authority = "Native role, menu and action permissions are checked separately for each operation."
        }), cancellationToken);

    [McpServerTool(Name = "client_portal_list_tickets", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Get your own current Client Portal My Open Tickets, including the complete verified total count and a bounded title list. Uses the native dashboard procedure and your authenticated directory ID. Continue with offset=NextOffset when HasMore; TotalCount is complete and must not be summed across pages. Offset0to5000. Never supply another user's email or ID.")]
    public Task<EmployeeToolResult<EmployeeTicketPage>> MyOpenTicketsAsync(int limit = 10, CancellationToken cancellationToken = default, int offset=0) =>
        ReadAsync(async (connection, actor) =>
        {
            await using var command = connection.CreateCommand(); command.CommandTimeout = 10;
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "dbo.stp_xml_Ticket_DSB_MyOpnTkt_List_Get_V2";
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = actor.UserId;
            var output = command.Parameters.Add("@XML_OUT", SqlDbType.Xml); output.Direction = ParameterDirection.Output;
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (output.Value is not string xml) throw new InvalidDataException("Missing native ticket output.");
            return ParseTicketPage(xml, limit,offset);
        }, cancellationToken, PortalMenu.TicketSearch);

    [McpServerTool(Name = "client_portal_list_time_entries", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read your own active time entries between fromDate and throughDate (inclusive YYYY-MM-DD in the portal's stored local dates, at most31days). Returns recorded work duration and drive hours separately. Continue with offset=nextOffset when hasMore. totalCount covers the full date range and must not be summed across pages. Offset0to5000. These are work logs, not verified contract billing quantities or invoice amounts.")]
    public async Task<EmployeeToolResult<object>> MyTimeEntriesAsync(string fromDate, string throughDate,
        int limit = 25, CancellationToken cancellationToken = default,int offset=0)
    {
        if (!TryDateRange(fromDate, throughDate, out var from, out var until)||offset is <0 or >5000)
            return new(false, null, "Use an inclusive YYYY-MM-DD date range of at most31days and offset0to5000.");
        return await ReadAsync<object>(async (connection, actor) =>
        {
            var take = Math.Clamp(limit, 1, 50);
            await using var command = connection.CreateCommand(); command.CommandTimeout = 10;
            command.CommandText = """
                SET LOCK_TIMEOUT 5000;
                SELECT e.TicketEntryID,e.TicketID,LEFT(ISNULL(t.Title,N''),200),e.StartDateTime,e.EndDateTime,
                  CONVERT(decimal(18,4),CASE WHEN e.StartDateTime IS NOT NULL AND e.EndDateTime>=e.StartDateTime
                    THEN DATEDIFF_BIG(second,e.StartDateTime,e.EndDateTime)/3600.0 ELSE NULL END),
                  CONVERT(decimal(18,4),ISNULL(e.DriveTime,0)),CONVERT(bit,ISNULL(e.IsApproved,0)),
                  CONVERT(bit,CASE WHEN ISNULL(e.InvoiceID,0)>0 OR ISNULL(e.InvDetID,0)>0 THEN 1 ELSE 0 END),COUNT(*) OVER()
                FROM dbo.tbl_TicketEntries e JOIN dbo.tbl_Tickets t ON t.TicketID=e.TicketID
                WHERE e.CreatedBy=@Username AND e.IsActive=1 AND ISNULL(e.IsDeleted,0)=0
                  AND e.StartDateTime>=@FromDate AND e.StartDateTime<@UntilDate
                ORDER BY e.StartDateTime DESC,e.TicketEntryID DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
                """;
            command.Parameters.Add("@Take", SqlDbType.Int).Value = take + 1;
            command.Parameters.Add("@Skip", SqlDbType.Int).Value = offset;
            command.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = actor.Username;
            command.Parameters.Add("@FromDate", SqlDbType.DateTime2).Value = from;
            command.Parameters.Add("@UntilDate", SqlDbType.DateTime2).Value = until;
            var rows = new List<EmployeeTimeEntry>();
            var total=0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {total=reader.GetInt32(9);rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.GetDecimal(6), reader.GetBoolean(7), reader.GetBoolean(8)));}
            await reader.DisposeAsync();
            if(rows.Count==0&&offset>0)
            {
                command.CommandText="SELECT COUNT(*) FROM dbo.tbl_TicketEntries e JOIN dbo.tbl_Tickets t ON t.TicketID=e.TicketID WHERE e.CreatedBy=@Username AND e.IsActive=1 AND ISNULL(e.IsDeleted,0)=0 AND e.StartDateTime>=@FromDate AND e.StartDateTime<@UntilDate";
                total=Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            }
            return BoundCollection(new { fromDate, throughDate,totalCount=total,offset, returnedCount = Math.Min(rows.Count, take), hasMore = total>offset+Math.Min(rows.Count,take),
                timeBasis = "Client Portal stored local dates; timestamps have no asserted UTC offset",
                durationBasis = "Missing or reversed native timestamps yield an unknown (null) duration. Overlapping stored hour buckets are never added as a substitute.",
                entries = rows.Take(take).ToArray() },offset);
        }, cancellationToken, PortalMenu.Timesheet);
    }

    public static EmployeeTicketPage ParseTicketPage(string xml, int limit,int offset=0)
    {
        if(offset is <0 or >5000)throw new InvalidDataException("Use offset0to5000.");
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 4_000_000) throw new InvalidDataException("Invalid native ticket output.");
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "Root" || root.HasAttributes || root.Elements().Any(e => e.Name != "TicketViewModel")
            || root.Nodes().Any(n => n is XText t && !string.IsNullOrWhiteSpace(t.Value)))
            throw new InvalidDataException("Unexpected native ticket schema.");
        var tickets = new List<EmployeeTicket>(); var seen = new HashSet<int>();
        foreach (var row in root.Elements())
        {
            if (row.Elements("ID").Count() != 1 || row.Element("ID")!.HasElements || row.Element("ID")!.HasAttributes
                || !int.TryParse(row.Element("ID")!.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) || id <= 0 || !seen.Add(id)) throw new InvalidDataException("Invalid native ticket identifiers.");
            static string Field(XElement row, string name, int max)
            {
                var value = row.Element(name)?.Value ?? ""; return value.Length <= max ? value : value[..max];
            }
            tickets.Add(new(id, Field(row, "Title", 200), Field(row, "Client", 100), Field(row, "Status", 50)));
        }
        var page=tickets.Skip(offset).Take(Math.Clamp(limit,1,20)).ToList();var reduced=false;
        while(true)
        {
            var result=new EmployeeTicketPage(tickets.Count,page.Count,page.ToArray())
            {Offset=offset,HasMore=tickets.Count>offset+page.Count,NextOffset=tickets.Count>offset+page.Count?offset+page.Count:null,OutputBudgetApplied=reduced};
            if(JsonSerializer.Serialize(new EmployeeToolResult<EmployeeTicketPage>(true,result,null),new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}).Length<=EmployeeReportResponseBounds.MaxCharacters)return result;
            if(page.Count<=1)throw new InvalidDataException("Native ticket row exceeded its safe bound.");
            page.RemoveAt(page.Count-1);reduced=true;
        }
    }

    public static bool TryDateRange(string fromDate, string throughDate, out DateTime from, out DateTime until)
    {
        from = until = default;
        if (!DateOnly.TryParseExact(fromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(throughDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || end < start || end.DayNumber - start.DayNumber >= 31 || end == DateOnly.MaxValue) return false;
        from = start.ToDateTime(TimeOnly.MinValue); until = end.AddDays(1).ToDateTime(TimeOnly.MinValue); return true;
    }

    private async Task<EmployeeToolResult<T>> ReadAsync<T>(Func<SqlConnection, PortalEmployee, Task<T>> query,
        CancellationToken ct, PortalMenu? requiredMenu = null, string? requiredGlobalScope = null,
        bool allowUnqualifiedPermission = false)
    {
        try
        {
            employees.GetIdentity(); // Reject disabled/invalid sign-ins before opening the database.
            var connectionString = configuration.GetConnectionString("client-portal");
            if (string.IsNullOrWhiteSpace(connectionString)) return new(false, default, "Employee Client Portal access is not configured.");
            await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(ct);
            var actor = await employees.ResolveAsync(connection, null, ct);
            if (requiredMenu is not null) await ClientPortalMenuAccess.RequireAsync(connection, null, actor, requiredMenu.Value, ct);
            if (requiredGlobalScope is not null && !ClientPortalPermissionPolicy.Allows(
                await ClientPortalEmployeeContext.ReadPermissionsAsync(connection, null, actor.UserId, requiredGlobalScope, ct),
                isOwner: false, allowUnqualified: allowUnqualifiedPermission))
                throw new EmployeeAccessDeniedException("The native portal scope does not allow this lookup.");
            return new(true, await query(connection, actor), null);
        }
        catch (EmployeeAccessDeniedException) { return new(false, default, "Your sign-in or current native portal role does not permit this operation."); }
        catch (Exception ex) when (ex is SqlException or InvalidDataException or XmlException)
        { return new(false, default, "Client Portal lookup failed or returned an invalid response. No verified result is available."); }
    }
}
