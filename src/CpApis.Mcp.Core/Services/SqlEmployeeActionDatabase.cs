using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace CpApis.Mcp.Core.Services;

/// <summary>Native changes, proposal state and audit commit or roll back together.</summary>
public sealed class SqlEmployeeActionDatabase(IConfiguration configuration) : IEmployeeActionDatabase
{
    public async Task<T> InTransactionAsync<T>(Func<SqlConnection, SqlTransaction, Task<T>> operation, CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("client-portal");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new EmployeeActionException(503, "Client Portal actions are unavailable.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var settings = connection.CreateCommand())
        {
            settings.CommandTimeout = 10;
            settings.CommandText = "SET LOCK_TIMEOUT 5000; SET DEADLOCK_PRIORITY LOW; SET XACT_ABORT ON;";
            await settings.ExecuteNonQueryAsync(ct);
        }
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var result = await operation(connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception failure)
        {
            // Cancellation cannot interrupt rollback and leave owned native changes committed.
            if (transaction.Connection is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("The action failed and rollback could not be confirmed.", failure, rollbackFailure);
                }
            }
            throw;
        }
    }
}
