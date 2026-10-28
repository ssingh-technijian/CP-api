using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace CpApis.Mcp.Core.Services;

public sealed class EmployeeActionException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record EmployeeActionField(string Field, string? Current, string? Proposed);
public sealed record EmployeeActionPlan(byte[] SnapshotHash, string Summary, IReadOnlyList<EmployeeActionField> Changes);
public sealed record EmployeeActionResult(string Operation, int? RecordId, string Message);
public sealed record EmployeeActionProposal(Guid ActionId, string Status, string Summary, string ReviewUrl, DateTime ExpiresAtUtc);
public sealed record EmployeeActionStatus(Guid ActionId, string Operation, string Status, EmployeeActionResult? Result);
public sealed record EmployeeActionReview(Guid ActionId, string Operation, string Status, string Summary,
    IReadOnlyList<EmployeeActionField> Changes, string Version, string? ReviewToken, DateTime ExpiresAtUtc,
    EmployeeActionResult? Result);
public sealed record EmployeeActionDecision(string Version, string ReviewToken);

/// <summary>Only reviewed native operations implement this boundary. Approval is never an MCP tool.</summary>
public interface IEmployeeActionExecutor
{
    Task<EmployeeActionPlan> PrepareAsync(string operation, JsonElement payload, SqlConnection connection,
        SqlTransaction transaction, PortalEmployee actor, CancellationToken ct);
    Task<EmployeeActionResult> ApplyAsync(string operation, JsonElement payload, SqlConnection connection,
        SqlTransaction transaction, PortalEmployee actor, CancellationToken ct);
}

public interface IEmployeeActionDatabase
{
    Task<T> InTransactionAsync<T>(Func<SqlConnection, SqlTransaction, Task<T>> operation, CancellationToken ct);
}
