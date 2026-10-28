using System.Globalization;
using System.Text.Json;
using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;

namespace CpApis.Mcp.Core.Services;

internal static class ClientPortalMcpSafety
{
    private static readonly string[] TicketInputs =
        ["TicketID", "Requestor", "AssignToUsername", "Title", "Description", "Priority", "Status",
         "Estimated_Hours", "RequestDate", "Internal_Notes", "Description_RTF"];

    internal static bool IsClientPortal(DatabaseObjectManifest item) =>
        item.DatabaseAlias.Equals("client-portal", StringComparison.OrdinalIgnoreCase)
        && item.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnsafeLegacyDelete(DatabaseObjectManifest item) => IsClientPortal(item)
        && (item.Name.Equals("sp_TicketEntry_Del", StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals("stp_TktEntry_Delete", StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals("stp_TktEntry_Delete_V2", StringComparison.OrdinalIgnoreCase));

    private static bool IsStaffDirectory(DatabaseObjectManifest item) => IsClientPortal(item)
        && item.Name.Equals("stp_Technijian_User_List", StringComparison.OrdinalIgnoreCase);

    internal static string? ValidateStaffLookup(DatabaseObjectManifest item, Dictionary<string, JsonElement>? parameters)
    {
        if (!IsStaffDirectory(item) || parameters is null || parameters.Count == 0) return null;
        if (parameters.Count != 1 || !parameters.Single().Key.TrimStart('@').Equals("GetInActive", StringComparison.OrdinalIgnoreCase))
            return "Only one GetInActive parameter is supported for the staff lookup.";
        var value = parameters.Single().Value;
        var activeOnly = value.ValueKind is JsonValueKind.Null or JsonValueKind.False
            || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == 0
            || value.ValueKind == JsonValueKind.String && (value.GetString() == "0" || string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase));
        return activeOnly ? null : "Inactive staff lookup is disabled in MCP because the legacy SQL broadens its client filter. Use GetInActive=false.";
    }

    internal static CommandExecutionResponse ProjectStaffDirectory(DatabaseObjectManifest item, CommandExecutionResponse response)
    {
        if (!IsStaffDirectory(item)) return response;
        string[] safeFields = ["UserID", "Email", "IsActive", "IsDelete"];
        var sets = new List<ResultSetPayload>();
        foreach (var set in response.ResultSets)
        {
            if (set.RowCount != set.Rows.Count) throw new InvalidOperationException("Staff lookup returned inconsistent row counts.");
            var rows = new List<Dictionary<string, object?>>();
            foreach (var row in set.Rows)
            {
                var projected = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in safeFields)
                {
                    var matches = row.Where(p => p.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count != 1) throw new InvalidOperationException("Staff lookup returned an unexpected schema.");
                    projected[field] = matches[0].Value;
                }
                rows.Add(projected);
            }
            sets.Add(new ResultSetPayload { Index = set.Index, RowCount = rows.Count, Rows = rows });
        }
        return new CommandExecutionResponse
        {
            DatabaseAlias = response.DatabaseAlias, DatabaseName = response.DatabaseName, Schema = response.Schema,
            Name = response.Name, Kind = response.Kind, DurationMs = response.DurationMs, ResultSets = sets
            // No output parameters are part of this procedure's reviewed contract.
        };
    }

    internal static (bool UsePatch, int TicketId, Dictionary<string, JsonElement>? Body, string? Error)
        PrepareTicketPatch(DatabaseObjectManifest item, Dictionary<string, JsonElement>? parameters)
    {
        if (!IsClientPortal(item) || !item.Name.Equals("sp_Ticket_Upd", StringComparison.OrdinalIgnoreCase)) return (false, 0, null, null);
        var normalized = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parameters ?? [])
        {
            var key = pair.Key.TrimStart('@');
            if (!normalized.TryAdd(key, pair.Value)) return (false, 0, null, "Ambiguous duplicate ticket parameter names.");
        }
        if (!normalized.TryGetValue("TicketID", out var value) || !PositiveId(value, out var ticketId))
            return (false, 0, null, "A positive TicketID is required.");
        if (TicketInputs.All(normalized.ContainsKey)) return (false, ticketId, null, null);
        if (normalized.Keys.Any(key => !TicketInputs.Contains(key, StringComparer.OrdinalIgnoreCase)))
            return (false, 0, null, "Unsupported partial ticket parameter.");
        normalized.Remove("TicketID");
        if (normalized.Count == 0) return (false, ticketId, null, "Provide at least one ticket field to update.");
        if (normalized.Remove("Requestor", out var requestor))
        {
            if (!PositiveId(requestor, out var requestorId)) return (false, 0, null, "Partial updates require Requestor as a positive directory ID.");
            normalized["Requestor_DirID"] = JsonSerializer.SerializeToElement(requestorId);
        }
        if (normalized.TryGetValue("AssignToUsername", out var assigned)
            && assigned.ValueKind is not JsonValueKind.Null
            && (assigned.ValueKind != JsonValueKind.String || int.TryParse(assigned.GetString(), out _)))
            return (false, 0, null, "Use an active username, not a numeric assignee ID, for a partial update.");
        foreach (var key in new[] { "Priority", "Status" })
            if (normalized.TryGetValue(key, out var lookup) && lookup.ValueKind == JsonValueKind.Number)
                normalized[key] = JsonSerializer.SerializeToElement(lookup.GetRawText());
        return (true, ticketId, normalized, null);
    }

    private static bool PositiveId(JsonElement value, out int id)
    {
        id = 0;
        return (value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out id)
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id)) && id > 0;
    }
}
