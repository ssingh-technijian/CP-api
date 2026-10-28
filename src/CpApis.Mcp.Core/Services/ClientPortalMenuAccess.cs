using System.Data;
using Microsoft.Data.SqlClient;

namespace CpApis.Mcp.Core.Services;

public enum PortalMenu { TicketSearch, NewTicket, Timesheet, Contracts, Invoices, InvoiceReports, EodView }

/// <summary>Mirrors the production portal's active menu membership, using exact CSV tokens.</summary>
public static class ClientPortalMenuAccess
{
    public static string Route(PortalMenu menu) => menu switch
    {
        PortalMenu.TicketSearch => "/Ticket/Search",
        PortalMenu.NewTicket => "/DashBoard/NewTicketV2",
        PortalMenu.Timesheet => "/Ticket/Timesheet",
        PortalMenu.Contracts => "/Contract/ViewList",
        PortalMenu.Invoices => "/Invoices/AllInvoices",
        PortalMenu.InvoiceReports => "/Reports/InvoiceUI",
        PortalMenu.EodView => "/Technijian/EODView",
        _ => throw new ArgumentOutOfRangeException(nameof(menu))
    };

    public static bool Matches(string? roles, string? stages, string role) =>
        role is "T1" or "T2" or "T3"
        && Csv(roles).Contains(role, StringComparer.OrdinalIgnoreCase)
        && Csv(stages).Contains("production", StringComparer.OrdinalIgnoreCase);

    public static async Task RequireAsync(SqlConnection connection, SqlTransaction? transaction,
        PortalEmployee actor, PortalMenu menu, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10;
        command.CommandText = "SELECT Roles,Stage FROM dbo.tbl_Menu_WithStage WHERE IsActive=1 AND Url=@Url;";
        command.Parameters.Add("@Url", SqlDbType.VarChar, 100).Value = Route(menu);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (Matches(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), actor.Role)) return;
        throw new EmployeeAccessDeniedException("Your current native portal role does not permit this operation.");
    }

    private static string[] Csv(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
