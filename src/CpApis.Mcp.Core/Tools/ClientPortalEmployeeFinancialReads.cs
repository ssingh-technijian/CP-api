using System.ComponentModel;
using System.Data;
using System.Globalization;
using CpApis.Mcp.Core.Services;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace CpApis.Mcp.Core.Tools;

public sealed record EmployeeInvoice(int InvoiceId, int BillingDirectoryId, string InvoiceNumber, string Type,
    string? InvoiceDate, string Status, decimal? NativeTotal, decimal? NativePaid,
    bool HasAccountingReference, bool HasSignatureFields);
public sealed record EmployeeContract(int ContractId, int? ClientDirectoryId, string Name, string Status,
    string Type, string? Starts, string? Ends, bool HasSignatureFields, bool HasDocumentReference,int? LocationDirectoryId=null);
public sealed record EmployeeWorkHourGroup(bool EntryActive, bool EntryDeleted, bool TicketActive,
    bool Approved, bool LinkedToInvoice, bool Processed, bool BilledFromAutotask, long EntryCount,
    long MissingDurationCount, long InvalidDurationCount, decimal KnownWorkDurationHours,
    decimal StoredNormalHours, decimal StoredAfterHours, decimal StoredOnsiteHours, decimal DriveHours);

public sealed partial class ClientPortalEmployeeReadTools
{
    [McpServerTool(Name = "client_portal_list_invoices", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List active native invoices for one exact billingDirectoryId. Requires native invoice menu and Invoices.Get permission. Complete totalCount is separate from the bounded preview; continue using offset=nextOffset when hasMore. Offset0to5000; refine client scope for larger lists. Stored totals/payment/reference flags do not verify signed prices, settlement or external accounting.")]
    public Task<EmployeeToolResult<object>> ListInvoicesAsync(int billingDirectoryId,int limit=20,CancellationToken cancellationToken=default,int offset=0)
    {
        if(billingDirectoryId<=0||offset is <0 or >5000)return Task.FromResult(new EmployeeToolResult<object>(false,null,"Use a positive billing directory ID and offset0to5000."));
        return ReadAsync<object>(async(connection,_)=>
        {
            await RequireReportClientAsync(connection,billingDirectoryId,cancellationToken);
            var take=Math.Clamp(limit,1,50);
            var page=await FinancialPageAsync(connection,billingDirectoryId,take,offset,
                """
                SELECT InvoiceID,DirID,LEFT(CONVERT(nvarchar(100),ISNULL(InvoiceNo,N'')),100),
                  LEFT(ISNULL(InvoiceType,N''),50),InvoiceDate,LEFT(CONVERT(nvarchar(100),ISNULL(Status,N'')),100),
                  CONVERT(decimal(19,4),Total),CONVERT(decimal(19,4),Paid),
                  CONVERT(bit,CASE WHEN NULLIF(LTRIM(RTRIM(QBConfirm)),N'') IS NOT NULL THEN 1 ELSE 0 END),
                  CONVERT(bit,CASE WHEN ISNULL(SignedBy,0)>0 OR SignDate IS NOT NULL THEN 1 ELSE 0 END),COUNT(*) OVER()
                FROM dbo.tbl_InvoiceHeader WHERE DirID=@DirectoryID AND IsActive=1
                ORDER BY InvoiceDate DESC,InvoiceID DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
                ""","SELECT COUNT(*) FROM dbo.tbl_InvoiceHeader WHERE DirID=@DirectoryID AND IsActive=1",10,
                r=>new EmployeeInvoice(r.GetInt32(0),r.GetInt32(1),r.GetString(2),r.GetString(3),LocalDate(r,4),r.GetString(5),r.IsDBNull(6)?null:r.GetDecimal(6),r.IsDBNull(7)?null:r.GetDecimal(7),r.GetBoolean(8),r.GetBoolean(9)),cancellationToken);
            return BoundCollection(new{billingDirectoryId,totalCount=page.Total,offset,returnedCount=Math.Min(page.Rows.Count,take),hasMore=page.Total>offset+Math.Min(page.Rows.Count,take),
                invoices=page.Rows.Take(take).ToArray(),commercialCorrectnessVerified=false,
                basis="Native stored fields; complete client count is independent of the bounded preview. References/signature flags do not verify external accounting or signed terms."},offset);
        },cancellationToken,PortalMenu.Invoices,"Invoices.Get");
    }

    [McpServerTool(Name = "client_portal_list_contracts", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List native contracts for one exact clientDirectoryId. Requires native Contracts menu. Complete totalCount is separate from the bounded preview; continue using offset=nextOffset when hasMore. Offset0to5000. A parent client matches native Client_ID; a location also matches Client_LocationsID. Metadata and signature/document flags do not prove signed commercial terms, activation or signing delivery.")]
    public Task<EmployeeToolResult<object>> ListContractsAsync(int clientDirectoryId,int limit=20,CancellationToken cancellationToken=default,int offset=0)
    {
        if(clientDirectoryId<=0||offset is <0 or >5000)return Task.FromResult(new EmployeeToolResult<object>(false,null,"Use a positive client directory ID and offset0to5000."));
        return ReadAsync<object>(async(connection,_)=>
        {
            await RequireReportClientAsync(connection,clientDirectoryId,cancellationToken);
            var take=Math.Clamp(limit,1,50);
            var page=await FinancialPageAsync(connection,clientDirectoryId,take,offset,
                """
                SELECT Contract_ID,Client_ID,LEFT(ISNULL(Contract_Name,N''),200),LEFT(ISNULL(ContractStatusTxt,N''),100),
                  LEFT(ISNULL(ContractType,N''),100),StartDate,EndDate,
                  CONVERT(bit,CASE WHEN ISNULL(Signed_DirID,0)>0 OR DateSigned IS NOT NULL THEN 1 ELSE 0 END),
                  CONVERT(bit,CASE WHEN DSGUID IS NOT NULL AND DSGUID<>'00000000-0000-0000-0000-000000000000' THEN 1 ELSE 0 END),COUNT(*) OVER(),Client_LocationsID
                FROM dbo.tbl_Contracts WHERE (Client_ID=@DirectoryID OR Client_LocationsID=@DirectoryID)
                ORDER BY Contract_ID DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
                ""","SELECT COUNT(*) FROM dbo.tbl_Contracts WHERE Client_ID=@DirectoryID OR Client_LocationsID=@DirectoryID",9,
                r=>new EmployeeContract(r.GetInt32(0),r.IsDBNull(1)?null:r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetString(4),LocalDate(r,5),LocalDate(r,6),r.GetBoolean(7),r.GetBoolean(8),r.IsDBNull(10)?null:r.GetInt32(10)),cancellationToken);
            return BoundCollection(new{clientDirectoryId,totalCount=page.Total,offset,returnedCount=Math.Min(page.Rows.Count,take),hasMore=page.Total>offset+Math.Min(page.Rows.Count,take),
                contracts=page.Rows.Take(take).ToArray(),signedCommercialTermsVerified=false},offset);
        },cancellationToken,PortalMenu.Contracts);
    }

    private static async Task RequireReportClientAsync(SqlConnection connection,int id,CancellationToken ct)
    {
        await using var query=connection.CreateCommand();query.CommandTimeout=10;
        query.CommandText="""
            SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.tbl_Directory WHERE DirID=@ID AND DirectoryType IN('Client','Location'))
              OR EXISTS(SELECT 1 FROM dbo.tbl_InvoiceHeader WHERE DirID=@ID)
              OR EXISTS(SELECT 1 FROM dbo.tbl_Contracts WHERE Client_ID=@ID OR Client_LocationsID=@ID)
              OR EXISTS(SELECT 1 FROM dbo.tbl_Tickets WHERE ClientID=@ID) THEN 1 ELSE 0 END;
            """;
        query.Parameters.AddWithValue("@ID",id);
        if(Convert.ToInt32(await query.ExecuteScalarAsync(ct))!=1)throw new InvalidDataException("The native client identifier could not be verified. Resolve the client before interpreting a zero.");
    }

    private static object BoundCollection(object data,int offset)=>EmployeeReportResponseBounds.Bound(new(true,data,null),offset).Data!;
    private static async Task<(List<T> Rows,int Total)> FinancialPageAsync<T>(SqlConnection connection,int directoryId,int take,int offset,
        string sql,string countSql,int totalOrdinal,Func<SqlDataReader,T> project,CancellationToken ct)
    {
        await using var command=connection.CreateCommand();command.CommandTimeout=10;command.CommandText="SET LOCK_TIMEOUT 5000;"+sql;
        command.Parameters.AddWithValue("@DirectoryID",directoryId);command.Parameters.AddWithValue("@Take",take+1);command.Parameters.AddWithValue("@Skip",offset);
        var rows=new List<T>();var total=0;
        await using(var reader=await command.ExecuteReaderAsync(ct)){while(await reader.ReadAsync(ct)){total=reader.GetInt32(totalOrdinal);rows.Add(project(reader));}}
        if(rows.Count==0&&offset>0){command.CommandText=countSql;total=Convert.ToInt32(await command.ExecuteScalarAsync(ct));}
        return(rows,total);
    }

    [McpServerTool(Name = "client_portal_billing_hours", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read raw recorded work-hour groups for one exact native ticket clientDirectoryId and inclusive YYYY-MM-DD dates, at most31days. Requires the production invoice-report menu and TechBillable.List global permission. Separates inactive/deleted/approved/invoice-linked/processed/Autotask-billed groups, work duration, normal/after-hours/onsite buckets and drive hours. These are native source fields, not verified billable totals, prices or invoice completeness. Do not add onsite to normal/after-hours or double-count drive time.")]
    public Task<EmployeeToolResult<object>> BillingHoursAsync(int clientDirectoryId, string fromDate, string throughDate,
        CancellationToken cancellationToken = default)
    {
        if (clientDirectoryId <= 0 || !TryDateRange(fromDate, throughDate, out var from, out var until))
            return Task.FromResult(new EmployeeToolResult<object>(false, null, "Use a positive native client directory ID and inclusive YYYY-MM-DD range of at most31days."));
        return ReadAsync<object>(async (connection, _) =>
        {
            await RequireReportClientAsync(connection,clientDirectoryId,cancellationToken);
            await using var command = connection.CreateCommand(); command.CommandTimeout = 10;
            command.CommandText = """
                SET LOCK_TIMEOUT 5000;
                WITH WorkRows AS (
                  SELECT CONVERT(bit,ISNULL(e.IsActive,0)) EntryActive,CONVERT(bit,ISNULL(e.IsDeleted,0)) EntryDeleted,
                    CONVERT(bit,ISNULL(t.IsActive,0)) TicketActive,CONVERT(bit,ISNULL(e.IsApproved,0)) Approved,
                    CONVERT(bit,CASE WHEN ISNULL(e.InvoiceID,0)>0 OR ISNULL(e.InvDetID,0)>0 THEN 1 ELSE 0 END) LinkedToInvoice,
                    CONVERT(bit,ISNULL(e.IsProcessed,0)) Processed,CONVERT(bit,ISNULL(e.Is_Billed_From_AT,0)) BilledFromAutotask,
                    e.StartDateTime,e.EndDateTime,e.NH_HoursWorked,e.AH_HoursWorked,e.Onsite_HoursWorked,e.DriveTime
                  FROM dbo.tbl_TicketEntries e JOIN dbo.tbl_Tickets t ON t.TicketID=e.TicketID
                  WHERE t.ClientID=@ClientID AND e.StartDateTime>=@FromDate AND e.StartDateTime<@UntilDate)
                SELECT EntryActive,EntryDeleted,TicketActive,Approved,LinkedToInvoice,Processed,BilledFromAutotask,COUNT_BIG(*),
                  SUM(CONVERT(bigint,CASE WHEN EndDateTime IS NULL THEN 1 ELSE 0 END)),
                  SUM(CONVERT(bigint,CASE WHEN EndDateTime<StartDateTime THEN 1 ELSE 0 END)),
                  CONVERT(decimal(19,4),SUM(CASE WHEN EndDateTime>=StartDateTime THEN DATEDIFF_BIG(second,StartDateTime,EndDateTime)/3600.0 ELSE 0 END)),
                  CONVERT(decimal(19,4),SUM(ISNULL(NH_HoursWorked,0))),CONVERT(decimal(19,4),SUM(ISNULL(AH_HoursWorked,0))),
                  CONVERT(decimal(19,4),SUM(ISNULL(Onsite_HoursWorked,0))),CONVERT(decimal(19,4),SUM(ISNULL(DriveTime,0)))
                FROM WorkRows GROUP BY EntryActive,EntryDeleted,TicketActive,Approved,LinkedToInvoice,Processed,BilledFromAutotask
                ORDER BY EntryActive,EntryDeleted,TicketActive,Approved,LinkedToInvoice,Processed,BilledFromAutotask;
                """;
            command.Parameters.Add("@ClientID", SqlDbType.Int).Value = clientDirectoryId;
            command.Parameters.Add("@FromDate", SqlDbType.DateTime2).Value = from;
            command.Parameters.Add("@UntilDate", SqlDbType.DateTime2).Value = until;
            var groups = new List<EmployeeWorkHourGroup>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) groups.Add(new(reader.GetBoolean(0), reader.GetBoolean(1),
                reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6),
                reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetDecimal(10), reader.GetDecimal(11),
                reader.GetDecimal(12), reader.GetDecimal(13), reader.GetDecimal(14)));
            return new { clientDirectoryId, fromDate, throughDate, timeBasis = "Portal stored local start dates; no asserted UTC offset",
                groups, billingCorrectnessVerified = false,
                limitations = "Only entries with a start timestamp inside the range are included. Eligibility, signed prices, rounding, minimums, overlap, internal-work exclusions, recurring charges and accounting readback require separate verification. Stored buckets may overlap; never sum them as a verified total." };
        }, cancellationToken, PortalMenu.InvoiceReports, "TechBillable.List", allowUnqualifiedPermission: true);
    }

    private static string? LocalDate(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : reader.GetDateTime(ordinal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
