namespace CpApis.Shared.Inventory;

public sealed class DatabaseInventoryManifest
{
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public string SourceRoot { get; init; } = string.Empty;
    public List<DatabaseTargetManifest> Databases { get; init; } = [];
    public List<DatabaseObjectManifest> Objects { get; init; } = [];
}

public sealed class DatabaseTargetManifest
{
    public string Alias { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Server { get; init; } = string.Empty;
    public string ConnectionName { get; init; } = string.Empty;
    public string ConnectionSourcePath { get; init; } = string.Empty;
    public bool IsDerivedTarget { get; init; }
}

public sealed class DatabaseObjectManifest
{
    public string Key { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string DatabaseAlias { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string TypeCode { get; init; } = string.Empty;
    public string TypeDescription { get; init; } = string.Empty;
    public bool IsActiveRoot { get; init; }
    public bool IsIncluded { get; init; }
    public bool IsWriteOperation { get; init; }
    public string? DefinitionHash { get; init; }
    public List<ParameterManifest> Parameters { get; init; } = [];
    public List<ColumnManifest> Columns { get; init; } = [];
    public List<SourceReferenceManifest> SourceReferences { get; init; } = [];
    public List<ObjectDependencyManifest> Dependencies { get; init; } = [];
    public List<string> DependencyTrails { get; init; } = [];
}

public sealed class ParameterManifest
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

public sealed class ColumnManifest
{
    public string Name { get; init; } = string.Empty;
    public string SqlType { get; init; } = string.Empty;
    public short MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
}

public sealed class SourceReferenceManifest
{
    public string FilePath { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string MatchKind { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;
}

public sealed class ObjectDependencyManifest
{
    public string ReferencedKey { get; init; } = string.Empty;
    public string ReferencedSchema { get; init; } = string.Empty;
    public string ReferencedName { get; init; } = string.Empty;
    public string ReferencedKind { get; init; } = string.Empty;
    public string DetectionKind { get; init; } = string.Empty;
}
