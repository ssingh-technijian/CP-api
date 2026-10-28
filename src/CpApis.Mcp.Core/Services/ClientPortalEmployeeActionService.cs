using System.Text.Json;

namespace CpApis.Mcp.Core.Services;

/// <summary>
/// Models may propose actions or read their own status. Only the separate authenticated
/// review endpoints issue a review token and call ApproveAsync. Native writes and action
/// state share one transaction; each approval resolves current native identity and authority.
/// </summary>
public sealed class ClientPortalEmployeeActionService(ClientPortalEmployeeContext employees,
    IEmployeeActionDatabase database, IEmployeeActionExecutor executor)
{
    public Task<EmployeeActionProposal> ProposeAsync(string operation, Guid requestId, JsonElement payload, CancellationToken ct)
    {
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(operation) || operation.Length > 40) throw EmployeeActionJson.Invalid();
        // The caller may dispose its JSON document as soon as this asynchronous method returns.
        // Own the payload before the first database await, rather than capturing borrowed JSON.
        payload = payload.Clone();
        var json = EmployeeActionJson.Canonicalize(payload); var payloadHash = EmployeeActionJson.Hash(json);
        return database.InTransactionAsync(async (connection, transaction) =>
        {
            var actor = await employees.ResolveAsync(connection, transaction, ct);
            var previous = await EmployeeActionStore.ReadAsync(connection, transaction, actor, requestId, true, ct);
            if (previous is not null)
            {
                if (previous.Operation != operation || !EmployeeActionJson.Same(previous.PayloadHash, payloadHash))
                    throw new EmployeeActionException(409, "This request ID already belongs to different action details. Use a new request ID.");
                // A retry after native deletion must still return its original applied result,
                // without attempting to read or mutate the now-deleted native record again.
                if (previous.Status != "pending" || previous.ExpiresAtUtc <= previous.ServerNowUtc)
                    return Proposal(previous, EffectiveStatus(previous), "This request already has an action.");
            }
            var plan = await executor.PrepareAsync(operation, payload, connection, transaction, actor, ct);
            ValidatePlan(plan);
            if (previous is not null)
                return Proposal(previous, EmployeeActionJson.Same(previous.SnapshotHash, plan.SnapshotHash) ? "pending" : "stale",
                    "Review the existing action before making any change.");
            var now = await EmployeeActionStore.NowAsync(connection, transaction, ct);
            // SQL datetime2(3) is the time authority. Avoid hash differences from submillisecond truncation.
            var expires = new DateTime(now.AddMinutes(15).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            var id = Guid.NewGuid(); var version = EmployeeActionJson.Version(id, payloadHash, plan.SnapshotHash, expires);
            await EmployeeActionStore.InsertAsync(connection, transaction, actor, id, requestId, operation,
                json, payloadHash, plan, version, expires, ct);
            return new EmployeeActionProposal(id, "pending", plan.Summary, ReviewUrl(id), expires);
        }, ct);
    }

    /// <summary>No native snapshot, review nonce, raw SQL or approval capability is returned to a model.</summary>
    public Task<EmployeeActionStatus> GetStatusAsync(Guid actionId, CancellationToken ct) =>
        database.InTransactionAsync(async (connection, transaction) =>
        {
            var actor = await employees.ResolveAsync(connection, transaction, ct);
            var row = await EmployeeActionStore.ReadAsync(connection, transaction, actor, actionId, false, ct) ?? throw Missing();
            return Status(row);
        }, ct);

    /// <summary>Called only by the fixed authenticated human review HTTP endpoint.</summary>
    public Task<EmployeeActionReview> GetReviewAsync(Guid actionId, CancellationToken ct) =>
        database.InTransactionAsync(async (connection, transaction) =>
        {
            var actor = await employees.ResolveAsync(connection, transaction, ct);
            var row = await EmployeeActionStore.ReadAsync(connection, transaction, actor, actionId, false, ct) ?? throw Missing();
            var status = EffectiveStatus(row);
            if (status != "pending")
                return new EmployeeActionReview(row.ActionId, row.Operation, status, "This action is no longer pending.",
                    [], Convert.ToHexString(row.Version), null, row.ExpiresAtUtc, ReadResult(row));
            using var payload = JsonDocument.Parse(row.PayloadJson);
            // Permissions, roles, menu and record access are fresh even when the proposal is old.
            var fresh = await executor.PrepareAsync(row.Operation, payload.RootElement, connection, transaction, actor, ct);
            ValidatePlan(fresh);
            if (!EmployeeActionJson.Same(fresh.SnapshotHash, row.SnapshotHash)) status = "stale";
            var review = JsonSerializer.Deserialize<EmployeeActionReviewContent>(row.ReviewJson, EmployeeActionJson.Options)
                ?? throw new EmployeeActionException(503, "The action review is unavailable.");
            var token = EmployeeActionJson.NewToken();
            var nonceExpiry = row.ServerNowUtc.AddMinutes(5);
            if (nonceExpiry > row.ExpiresAtUtc) nonceExpiry = row.ExpiresAtUtc;
            await EmployeeActionStore.SetReviewAsync(connection, transaction, actor, row, EmployeeActionJson.Hash(token), nonceExpiry, ct);
            return new EmployeeActionReview(row.ActionId, row.Operation, status, review.Summary, review.Changes,
                Convert.ToHexString(row.Version), token, row.ExpiresAtUtc, null);
        }, ct);

    public Task<EmployeeActionStatus> ApproveAsync(Guid actionId, EmployeeActionDecision decision, CancellationToken ct)
    {
        var version = EmployeeActionJson.ReadVersion(decision.Version); EmployeeActionJson.RequireToken(decision.ReviewToken);
        return database.InTransactionAsync(async (connection, transaction) =>
        {
            var actor = await employees.ResolveAsync(connection, transaction, ct);
            var row = await EmployeeActionStore.ReadAsync(connection, transaction, actor, actionId, false, ct) ?? throw Missing();
            RequireDecision(row, decision, version);
            if (row.Status == "applied") return Status(row); // An exact replay never invokes native writes twice.
            RequirePending(row);
            using var payload = JsonDocument.Parse(row.PayloadJson);
            var current = await executor.PrepareAsync(row.Operation, payload.RootElement, connection, transaction, actor, ct);
            ValidatePlan(current);
            if (!EmployeeActionJson.Same(current.SnapshotHash, row.SnapshotHash))
                throw new EmployeeActionException(409, "The Portal record changed after this action was proposed. Create a new proposal.");
            var result = await executor.ApplyAsync(row.Operation, payload.RootElement, connection, transaction, actor, ct);
            if (result.Operation != row.Operation || result.Message.Length > 1000) throw new EmployeeActionException(503, "The native action result is invalid.");
            await EmployeeActionStore.FinishAsync(connection, transaction, actor, row, result, ct);
            return new EmployeeActionStatus(row.ActionId, row.Operation, "applied", result);
        }, ct);
    }

    public Task<EmployeeActionStatus> RejectAsync(Guid actionId, EmployeeActionDecision decision, CancellationToken ct)
    {
        var version = EmployeeActionJson.ReadVersion(decision.Version); EmployeeActionJson.RequireToken(decision.ReviewToken);
        return database.InTransactionAsync(async (connection, transaction) =>
        {
            var actor = await employees.ResolveAsync(connection, transaction, ct);
            var row = await EmployeeActionStore.ReadAsync(connection, transaction, actor, actionId, false, ct) ?? throw Missing();
            RequireDecision(row, decision, version);
            if (row.Status == "rejected") return Status(row);
            RequirePending(row);
            await EmployeeActionStore.FinishAsync(connection, transaction, actor, row, null, ct);
            return new EmployeeActionStatus(row.ActionId, row.Operation, "rejected", null);
        }, ct);
    }

    private static void RequireDecision(EmployeeActionStored row, EmployeeActionDecision decision, byte[] version)
    {
        if (!EmployeeActionJson.Same(version, row.Version)) throw EmployeeActionStore.Conflict();
        if (!EmployeeActionJson.Same(EmployeeActionJson.Hash(decision.ReviewToken), row.NonceHash))
            throw new EmployeeActionException(403, "Open the authenticated action review before submitting this decision.");
        // An exact replay of a completed decision is idempotent even after its nonce expires.
        if (row.Status == "pending" && (row.NonceExpiresAtUtc is null || row.NonceExpiresAtUtc <= row.ServerNowUtc))
            throw new EmployeeActionException(410, "This review expired. Open it again before deciding.");
    }

    private static void RequirePending(EmployeeActionStored row)
    {
        if (row.Status != "pending") throw new EmployeeActionException(409, "This action has already been decided.");
        if (row.ExpiresAtUtc <= row.ServerNowUtc) throw new EmployeeActionException(410, "This action expired. Create a new proposal.");
    }

    private static void ValidatePlan(EmployeeActionPlan plan)
    {
        if (plan.SnapshotHash.Length != 32 || string.IsNullOrWhiteSpace(plan.Summary) || plan.Summary.Length > 1000
            || plan.Changes.Count is < 1 or > 32 || plan.Changes.Any(c => string.IsNullOrWhiteSpace(c.Field)
                || c.Field.Length > 100 || c.Current?.Length > 8000 || c.Proposed?.Length > 8000))
            throw new EmployeeActionException(503, "The native action could not produce a bounded review.");
    }

    private static string EffectiveStatus(EmployeeActionStored row) => row.Status == "pending" && row.ExpiresAtUtc <= row.ServerNowUtc ? "expired" : row.Status;
    private static EmployeeActionResult? ReadResult(EmployeeActionStored row) => row.ResultJson is null ? null
        : JsonSerializer.Deserialize<EmployeeActionResult>(row.ResultJson, EmployeeActionJson.Options);
    private static EmployeeActionStatus Status(EmployeeActionStored row) => new(row.ActionId, row.Operation, EffectiveStatus(row), ReadResult(row));
    private static string ReviewUrl(Guid id) => $"https://rtm-chat.technijian.ai/client-portal/actions/{id:D}";
    private static EmployeeActionProposal Proposal(EmployeeActionStored row, string status, string summary) => new(row.ActionId, status, summary, ReviewUrl(row.ActionId), row.ExpiresAtUtc);
    private static EmployeeActionException Missing() => new(404, "This action was not found for your active Portal account.");
}
