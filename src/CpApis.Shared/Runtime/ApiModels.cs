using System.Text.Json;

namespace CpApis.Shared.Runtime;

public sealed class ObjectQueryRequest
{
    public int Top { get; init; } = 100;
    public List<FilterClause> Filters { get; init; } = [];
    public List<SortClause> Sorts { get; init; } = [];
}

public sealed class FunctionExecutionRequest
{
    public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public ObjectQueryRequest? Query { get; init; }
}

public sealed class StoredProcedureExecutionRequest
{
    public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FilterClause
{
    public string Column { get; init; } = string.Empty;
    public string Operator { get; init; } = "eq";
    public JsonElement? Value { get; init; }
    public List<JsonElement> Values { get; init; } = [];
}

public sealed class SortClause
{
    public string Column { get; init; } = string.Empty;
    public bool Descending { get; init; }
}

public sealed class CommandExecutionResponse
{
    public string DatabaseAlias { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public List<ResultSetPayload> ResultSets { get; init; } = [];
    public Dictionary<string, object?> OutputParameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class QueryExecutionResponse
{
    public string DatabaseAlias { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public int RowCount { get; init; }
    public List<Dictionary<string, object?>> Rows { get; init; } = [];
}

public sealed class ScalarFunctionResponse
{
    public string DatabaseAlias { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public object? Value { get; init; }
}

public sealed class ResultSetPayload
{
    public int Index { get; init; }
    public int RowCount { get; init; }
    public List<Dictionary<string, object?>> Rows { get; init; } = [];
}
