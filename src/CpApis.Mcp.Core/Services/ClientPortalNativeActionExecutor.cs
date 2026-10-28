using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace CpApis.Mcp.Core.Services;

/// <summary>Closed operation dispatch. No raw procedure names, actor IDs or SQL are accepted.</summary>
public sealed class ClientPortalNativeActionExecutor : IEmployeeActionExecutor
{
    public Task<EmployeeActionPlan> PrepareAsync(string operation, JsonElement payload, SqlConnection connection,
        SqlTransaction transaction, PortalEmployee actor, CancellationToken ct) => operation switch
    {
        "ticket.create" or "ticket.edit" or "ticket.delete" => EmployeeTicketActions.PrepareAsync(operation, payload, connection, transaction, actor, ct),
        "time.create" or "time.edit" or "time.delete" => EmployeeTimeEntryActions.PrepareAsync(operation, payload, connection, transaction, actor, ct),
        "invoice.create" or "invoice.edit" or "invoice.delete" or "contract.create" or "contract.edit" or "contract.delete"
            => EmployeeFinanceDraftActions.PrepareAsync(operation, payload, connection, transaction, actor, ct),
        _ => throw new EmployeeActionException(400, "This operation is not an implemented employee action.")
    };

    public Task<EmployeeActionResult> ApplyAsync(string operation, JsonElement payload, SqlConnection connection,
        SqlTransaction transaction, PortalEmployee actor, CancellationToken ct) => operation switch
    {
        "ticket.create" or "ticket.edit" or "ticket.delete" => EmployeeTicketActions.ApplyAsync(operation, payload, connection, transaction, actor, ct),
        "time.create" or "time.edit" or "time.delete" => EmployeeTimeEntryActions.ApplyAsync(operation, payload, connection, transaction, actor, ct),
        "invoice.create" or "invoice.edit" or "invoice.delete" or "contract.create" or "contract.edit" or "contract.delete"
            => EmployeeFinanceDraftActions.ApplyAsync(operation, payload, connection, transaction, actor, ct),
        _ => throw new EmployeeActionException(400, "This operation is not an implemented employee action.")
    };
}
