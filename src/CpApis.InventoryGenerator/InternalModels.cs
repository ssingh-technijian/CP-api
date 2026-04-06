using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;

namespace CpApis.InventoryGenerator;

internal sealed class DatabaseCatalog
{
    public DatabaseCatalog(DatabaseTargetManifest target)
    {
        Target = target;
    }

    public DatabaseTargetManifest Target { get; }
    public Dictionary<string, DatabaseObjectBuilder> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, DatabaseObjectBuilder> ObjectsById { get; } = [];
    public Dictionary<string, List<DatabaseObjectBuilder>> ObjectsByNormalizedName { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class DatabaseObjectBuilder
{
    public string DatabaseAlias { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TypeCode { get; init; } = string.Empty;
    public string TypeDescription { get; init; } = string.Empty;
    public string? Definition { get; init; }
    public string Kind => SqlSupport.ToKind(TypeCode);
    public string Key => SqlSupport.NormalizeCompositeKey(DatabaseAlias, Schema, Name, Kind);
    public string NormalizedName => SqlSupport.NormalizeObjectName($"{Schema}.{Name}");
    public List<ParameterBuilder> Parameters { get; } = [];
    public List<ColumnBuilder> Columns { get; } = [];
    public List<DependencyBuilder> Dependencies { get; } = [];
}

internal sealed class ParameterBuilder
{
    public int ParameterId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SqlType { get; init; } = string.Empty;
    public short MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public bool IsOutput { get; init; }
    public bool IsNullable { get; init; }
    public bool HasDefaultValue { get; init; }
}

internal sealed class ColumnBuilder
{
    public string Name { get; init; } = string.Empty;
    public string SqlType { get; init; } = string.Empty;
    public short MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
}

internal sealed class DependencyBuilder
{
    public string ReferencedKey { get; init; } = string.Empty;
    public string ReferencedSchema { get; init; } = string.Empty;
    public string ReferencedName { get; init; } = string.Empty;
    public string ReferencedKind { get; init; } = string.Empty;
    public string DetectionKind { get; init; } = string.Empty;
}

internal sealed record SourceReference(string ObjectKey, string FilePath, int LineNumber, string MatchKind, string Snippet);

internal static class DictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> values, TKey key, Func<TKey, TValue> factory) where TKey : notnull
    {
        if (values.TryGetValue(key, out var value))
        {
            return value;
        }

        value = factory(key);
        values[key] = value;
        return value;
    }
}
