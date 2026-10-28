using CpApis.Shared.Inventory;

namespace CpApis.Shared.Runtime;

/// <summary>
/// Conservative confirmation hints for old inventories as well as new ones.
/// A false result is not proof that SQL is read-only or that a caller is authorized.
/// </summary>
public static class StoredProcedureWriteSafety
{
    private static readonly string[] ExistingWriteFragments =
        ["save", "update", "delete", "add", "insert", "approve", "assign", "mark"];
    private static readonly string[] AdditionalWritePrefixes =
        ["create", "generate", "change", "remove", "reset", "activate", "deactivate",
         "recalc", "rebuild", "sync", "send", "cancel", "close", "void", "process", "import", "restore"];

    public static bool IsWriteOperation(DatabaseObjectManifest item) =>
        item.IsWriteOperation || IsWriteName(item.Name);

    public static bool IsWriteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var lowered = name.ToLowerInvariant();
        if (ExistingWriteFragments.Any(fragment => lowered.Contains(fragment, StringComparison.Ordinal))) return true;
        foreach (var token in lowered.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (AdditionalWritePrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal))) return true;
            foreach (var shortVerb in new[] { "upd", "del", "set" })
            {
                if (token == shortVerb) return true;
                if (!token.StartsWith(shortVerb, StringComparison.Ordinal)) continue;
                var suffix = token.AsSpan(shortVerb.Length);
                if (suffix.Length > 0 && suffix[0] == 'v') suffix = suffix[1..];
                if (suffix.Length > 0 && suffix.ToArray().All(char.IsAsciiDigit)) return true;
            }
        }
        return false;
    }
}
