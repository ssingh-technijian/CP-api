using CpApis.Shared.Runtime;

namespace CpApis.Mcp.Core.Models;

public sealed class StoredProcedureModuleResult
{
    public int Count { get; init; }
    public IReadOnlyList<string> Modules { get; init; } = [];
}

public sealed class StoredProcedureListResult
{
    public int TotalCount { get; init; }
    public int ReturnedCount { get; init; }
    public IReadOnlyList<StoredProcedureSummary> Items { get; init; } = [];
}

public sealed class StoredProcedureSummary
{
    public string Module { get; init; } = string.Empty;
    public string DatabaseAlias { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsActiveRoot { get; init; }
    public bool IsWriteOperation { get; init; }
    public int InputParameterCount { get; init; }
    public string ExecutePath { get; init; } = string.Empty;
}

public sealed class StoredProcedureDetail
{
    public string Module { get; init; } = string.Empty;
    public string DatabaseAlias { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsActiveRoot { get; init; }
    public bool IsWriteOperation { get; init; }
    public string ExecutePath { get; init; } = string.Empty;
    public string BodyDescription { get; init; } = string.Empty;
    public object RequestTemplate { get; init; } = new();
    public IReadOnlyList<StoredProcedureParameterInfo> InputParameters { get; init; } = [];
    public IReadOnlyList<StoredProcedureSourceReference> SourceReferences { get; init; } = [];
    public IReadOnlyList<string> DependencyTrails { get; init; } = [];
}

public sealed class StoredProcedureParameterInfo
{
    public string Name { get; init; } = string.Empty;
    public string SqlType { get; init; } = string.Empty;
    public short MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public bool IsNullable { get; init; }
    public bool HasDefaultValue { get; init; }
    public object ExampleValue { get; init; } = string.Empty;
}

public sealed class StoredProcedureSourceReference
{
    public string FilePath { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string MatchKind { get; init; } = string.Empty;
}

public sealed class StoredProcedureExecutionResult
{
    public bool Executed { get; init; }
    public bool WriteConfirmationBypassed { get; init; }
    public CommandExecutionResponse? Response { get; init; }
    public string Message { get; init; } = string.Empty;
}
