using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace CpApis.Mcp.Core.Services;

internal sealed record EmployeeActionStored(Guid ActionId, string Operation, string PayloadJson,
    byte[] PayloadHash, byte[] SnapshotHash, string ReviewJson, byte[] Version, string Status,
    DateTime ExpiresAtUtc, byte[]? NonceHash, DateTime? NonceExpiresAtUtc, string? ResultJson, DateTime ServerNowUtc);
internal sealed record EmployeeActionReviewContent(string Summary, IReadOnlyList<EmployeeActionField> Changes);

/// <summary>All lookups are actor-bound and locked for the lifetime of one short transaction.</summary>
internal static class EmployeeActionStore
{
    internal static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandTimeout = 10; command.CommandText = sql; return command;
    }

    private static void Actor(SqlCommand command, PortalEmployee actor)
    {
        command.Parameters.Add("@Tenant", SqlDbType.UniqueIdentifier).Value = Guid.Parse(ClientPortalEmployeeContext.MicrosoftTenant);
        command.Parameters.Add("@Actor", SqlDbType.UniqueIdentifier).Value = actor.ObjectId;
        command.Parameters.Add("@Directory", SqlDbType.Int).Value = actor.UserId;
    }

    internal static async Task<DateTime> NowAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT SYSUTCDATETIME();");
        return DateTime.SpecifyKind((DateTime)(await command.ExecuteScalarAsync(ct))!, DateTimeKind.Utc);
    }

    internal static async Task<EmployeeActionStored?> ReadAsync(SqlConnection connection, SqlTransaction transaction,
        PortalEmployee actor, Guid id, bool requestId, CancellationToken ct)
    {
        // The predicate is selected from two fixed statements; no caller SQL or identifier is accepted.
        var predicate = requestId ? "RequestId=@Id" : "ActionId=@Id";
        await using var command = Command(connection, transaction, $"""
            SELECT ActionId,Operation,PayloadJson,PayloadHash,SnapshotHash,ReviewJson,ActionVersion,Status,
                ExpiresAtUtc,ReviewNonceHash,ReviewNonceExpiresAtUtc,ResultJson,SYSUTCDATETIME()
            FROM mcp.EmployeeAction WITH(UPDLOCK,HOLDLOCK)
            WHERE MicrosoftTenantId=@Tenant AND MicrosoftObjectId=@Actor AND DirectoryUserId=@Directory AND {predicate};
            """);
        Actor(command, actor); command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = id;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var result = new EmployeeActionStored(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            (byte[])reader.GetValue(3), (byte[])reader.GetValue(4), reader.GetString(5), (byte[])reader.GetValue(6),
            reader.GetString(7), Utc(reader.GetDateTime(8)), reader.IsDBNull(9) ? null : (byte[])reader.GetValue(9),
            reader.IsDBNull(10) ? null : Utc(reader.GetDateTime(10)), reader.IsDBNull(11) ? null : reader.GetString(11),
            Utc(reader.GetDateTime(12)));
        if (await reader.ReadAsync(ct)) throw new EmployeeActionException(503, "The action store is inconsistent.");
        return result;
    }

    internal static async Task InsertAsync(SqlConnection connection, SqlTransaction transaction, PortalEmployee actor,
        Guid id, Guid requestId, string operation, string payload, byte[] payloadHash, EmployeeActionPlan plan,
        byte[] version, DateTime expires, CancellationToken ct)
    {
        var review = JsonSerializer.Serialize(new EmployeeActionReviewContent(plan.Summary, plan.Changes), EmployeeActionJson.Options);
        if (review.Length > 16_384) throw EmployeeActionJson.Invalid();
        await using var command = Command(connection, transaction, """
            INSERT mcp.EmployeeAction(ActionId,MicrosoftTenantId,MicrosoftObjectId,DirectoryUserId,RequestId,
              Operation,PayloadJson,PayloadHash,SnapshotHash,ReviewJson,ActionVersion,ExpiresAtUtc)
            VALUES(@Id,@Tenant,@Actor,@Directory,@Request,@Operation,@Payload,@PayloadHash,@Snapshot,@Review,@Version,@Expires);
            """);
        Actor(command, actor); command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = id;
        command.Parameters.Add("@Request", SqlDbType.UniqueIdentifier).Value = requestId;
        command.Parameters.Add("@Operation", SqlDbType.VarChar, 40).Value = operation;
        command.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payload;
        command.Parameters.Add("@PayloadHash", SqlDbType.Binary, 32).Value = payloadHash;
        command.Parameters.Add("@Snapshot", SqlDbType.Binary, 32).Value = plan.SnapshotHash;
        command.Parameters.Add("@Review", SqlDbType.NVarChar, -1).Value = review;
        command.Parameters.Add("@Version", SqlDbType.Binary, 32).Value = version;
        command.Parameters.Add("@Expires", SqlDbType.DateTime2).Value = expires;
        await command.ExecuteNonQueryAsync(ct);
        await AuditAsync(connection, transaction, actor, id, "proposed", ct);
    }

    internal static async Task SetReviewAsync(SqlConnection connection, SqlTransaction transaction, PortalEmployee actor,
        EmployeeActionStored row, byte[] nonceHash, DateTime expires, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            UPDATE mcp.EmployeeAction SET ReviewNonceHash=@Nonce,ReviewNonceExpiresAtUtc=@Expires
            WHERE ActionId=@Id AND MicrosoftTenantId=@Tenant AND MicrosoftObjectId=@Actor
              AND DirectoryUserId=@Directory AND Status='pending';
            """);
        Actor(command, actor); command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = row.ActionId;
        command.Parameters.Add("@Nonce", SqlDbType.Binary, 32).Value = nonceHash;
        command.Parameters.Add("@Expires", SqlDbType.DateTime2).Value = expires;
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw Conflict();
        await AuditAsync(connection, transaction, actor, row.ActionId, "reviewed", ct);
    }

    internal static async Task FinishAsync(SqlConnection connection, SqlTransaction transaction, PortalEmployee actor,
        EmployeeActionStored row, EmployeeActionResult? result, CancellationToken ct)
    {
        var json = result is null ? null : JsonSerializer.Serialize(result, EmployeeActionJson.Options);
        if (json?.Length > 8192) throw EmployeeActionJson.Invalid();
        await using var command = Command(connection, transaction, """
            UPDATE mcp.EmployeeAction SET Status=@Status,ResultJson=@Result,
              AppliedAtUtc=CASE WHEN @Status='applied' THEN SYSUTCDATETIME() ELSE NULL END,
              RejectedAtUtc=CASE WHEN @Status='rejected' THEN SYSUTCDATETIME() ELSE NULL END
            WHERE ActionId=@Id AND MicrosoftTenantId=@Tenant AND MicrosoftObjectId=@Actor
              AND DirectoryUserId=@Directory AND Status='pending';
            """);
        Actor(command, actor); command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = row.ActionId;
        command.Parameters.Add("@Status", SqlDbType.VarChar, 16).Value = result is null ? "rejected" : "applied";
        command.Parameters.Add("@Result", SqlDbType.NVarChar, -1).Value = (object?)json ?? DBNull.Value;
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw Conflict();
        await AuditAsync(connection, transaction, actor, row.ActionId, result is null ? "rejected" : "applied", ct);
    }

    private static async Task AuditAsync(SqlConnection connection, SqlTransaction transaction, PortalEmployee actor,
        Guid action, string phase, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            INSERT mcp.EmployeeActionAudit(ActionId,MicrosoftObjectId,DirectoryUserId,Event)
            VALUES(@Id,@Actor,@Directory,@Event);
            """);
        command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = action;
        command.Parameters.Add("@Actor", SqlDbType.UniqueIdentifier).Value = actor.ObjectId;
        command.Parameters.Add("@Directory", SqlDbType.Int).Value = actor.UserId;
        command.Parameters.Add("@Event", SqlDbType.VarChar, 20).Value = phase;
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static EmployeeActionException Conflict() => new(409, "This action changed. Open its review again.");
}
