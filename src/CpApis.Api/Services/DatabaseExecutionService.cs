using System.Data;
using System.Diagnostics;
using System.Text;
using CpApis.Shared.Inventory;
using CpApis.Shared.Options;
using CpApis.Shared.Runtime;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CpApis.Api.Services;

public sealed class DatabaseExecutionService
{
    private readonly IConfiguration _configuration;
    private readonly RuntimeCatalogOptions _options;

    public DatabaseExecutionService(
        IConfiguration configuration,
        IOptions<RuntimeCatalogOptions> options)
    {
        _configuration = configuration;
        _options = options.Value;
    }

    public async Task<CommandExecutionResponse> ExecuteStoredProcedureAsync(
        DatabaseObjectManifest item,
        StoredProcedureExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(GetConnectionString(item.DatabaseAlias));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(
            $"{SqlSupport.QuoteIdentifier(item.Schema)}.{SqlSupport.QuoteIdentifier(item.Name)}",
            connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = _options.CommandTimeoutSeconds
        };

        foreach (var parameter in item.Parameters.Where(parameter => parameter.ParameterId > 0 && string.IsNullOrWhiteSpace(parameter.Name) is false))
        {
            var sqlParameter = BuildSqlParameter(parameter, request.Parameters);
            command.Parameters.Add(sqlParameter);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var resultSets = await SqlSupport.ReadResultSetsAsync(reader, cancellationToken);
        await reader.CloseAsync();

        var outputs = command.Parameters
            .Cast<SqlParameter>()
            .Where(parameter => parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput or ParameterDirection.ReturnValue)
            .ToDictionary(
                parameter => parameter.ParameterName.TrimStart('@'),
                parameter => SqlSupport.ConvertScalarFromReader(parameter.Value),
                SqlSupport.NameComparer);

        return new CommandExecutionResponse
        {
            DatabaseAlias = item.DatabaseAlias,
            DatabaseName = ResolveDatabaseName(connection, item),
            Schema = item.Schema,
            Name = item.Name,
            Kind = item.Kind,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ResultSets = resultSets,
            OutputParameters = outputs
        };
    }

    public async Task<QueryExecutionResponse> QueryViewAsync(
        DatabaseObjectManifest item,
        ObjectQueryRequest request,
        CancellationToken cancellationToken)
    {
        var (commandText, parameters) = BuildViewQuery(item, request);
        return await ExecuteQueryAsync(item, commandText, parameters, cancellationToken);
    }

    public async Task<object> ExecuteFunctionAsync(
        DatabaseObjectManifest item,
        FunctionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return item.Kind switch
        {
            "scalar-function" => await ExecuteScalarFunctionAsync(item, request.Parameters, cancellationToken),
            "table-function" or "inline-table-function" => await ExecuteTabularFunctionAsync(item, request, cancellationToken),
            _ => throw new ArgumentException($"Unsupported function kind '{item.Kind}'.")
        };
    }

    private async Task<QueryExecutionResponse> ExecuteQueryAsync(
        DatabaseObjectManifest item,
        string commandText,
        List<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(GetConnectionString(item.DatabaseAlias));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(commandText, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = _options.CommandTimeoutSeconds
        };

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var resultSets = await SqlSupport.ReadResultSetsAsync(reader, cancellationToken);
        var rows = resultSets.FirstOrDefault()?.Rows ?? [];

        return new QueryExecutionResponse
        {
            DatabaseAlias = item.DatabaseAlias,
            DatabaseName = ResolveDatabaseName(connection, item),
            Schema = item.Schema,
            Name = item.Name,
            Kind = item.Kind,
            DurationMs = stopwatch.ElapsedMilliseconds,
            RowCount = rows.Count,
            Rows = rows
        };
    }

    private async Task<ScalarFunctionResponse> ExecuteScalarFunctionAsync(
        DatabaseObjectManifest item,
        Dictionary<string, System.Text.Json.JsonElement> requestParameters,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(GetConnectionString(item.DatabaseAlias));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(BuildScalarFunctionSql(item), connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = _options.CommandTimeoutSeconds
        };

        foreach (var parameter in BuildFunctionParameters(item, requestParameters))
        {
            command.Parameters.Add(parameter);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return new ScalarFunctionResponse
        {
            DatabaseAlias = item.DatabaseAlias,
            DatabaseName = ResolveDatabaseName(connection, item),
            Schema = item.Schema,
            Name = item.Name,
            Kind = item.Kind,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Value = SqlSupport.ConvertScalarFromReader(value)
        };
    }

    private async Task<QueryExecutionResponse> ExecuteTabularFunctionAsync(
        DatabaseObjectManifest item,
        FunctionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var functionParameters = BuildFunctionParameters(item, request.Parameters);
        var (commandText, queryParameters) = BuildTableFunctionQuery(item, request.Query ?? new ObjectQueryRequest(), functionParameters);
        return await ExecuteQueryAsync(item, commandText, queryParameters, cancellationToken);
    }

    private string BuildScalarFunctionSql(DatabaseObjectManifest item)
    {
        var placeholders = item.Parameters
            .Where(parameter => parameter.ParameterId > 0)
            .OrderBy(parameter => parameter.ParameterId)
            .Select(parameter => parameter.Name)
            .ToArray();

        return $"SELECT {SqlSupport.QuoteIdentifier(item.Schema)}.{SqlSupport.QuoteIdentifier(item.Name)}({string.Join(", ", placeholders)}) AS Value;";
    }

    private List<SqlParameter> BuildFunctionParameters(
        DatabaseObjectManifest item,
        Dictionary<string, System.Text.Json.JsonElement> requestParameters)
    {
        var parameters = new List<SqlParameter>();
        foreach (var parameter in item.Parameters.Where(parameter => parameter.ParameterId > 0))
        {
            parameters.Add(BuildSqlParameter(parameter, requestParameters));
        }

        return parameters;
    }

    private (string CommandText, List<SqlParameter> Parameters) BuildTableFunctionQuery(
        DatabaseObjectManifest item,
        ObjectQueryRequest request,
        List<SqlParameter> functionParameters)
    {
        var top = Math.Clamp(request.Top <= 0 ? _options.DefaultTop : request.Top, 1, _options.MaxTop);
        var alias = "src";
        var columns = item.Columns.ToDictionary(column => column.Name, SqlSupport.NameComparer);

        var builder = new StringBuilder();
        builder.Append("SELECT TOP (@top) * FROM ");
        builder.Append(SqlSupport.QuoteIdentifier(item.Schema));
        builder.Append('.');
        builder.Append(SqlSupport.QuoteIdentifier(item.Name));
        builder.Append('(');
        builder.Append(string.Join(", ", item.Parameters.Where(parameter => parameter.ParameterId > 0).Select(parameter => parameter.Name)));
        builder.Append(") AS ");
        builder.Append(alias);

        var parameters = new List<SqlParameter>(functionParameters)
        {
            new("@top", SqlDbType.Int) { Value = top }
        };

        AppendFiltersAndSorts(builder, parameters, request, columns, alias);
        return (builder.ToString(), parameters);
    }

    private (string CommandText, List<SqlParameter> Parameters) BuildViewQuery(
        DatabaseObjectManifest item,
        ObjectQueryRequest request)
    {
        var top = Math.Clamp(request.Top <= 0 ? _options.DefaultTop : request.Top, 1, _options.MaxTop);
        var alias = "src";
        var columns = item.Columns.ToDictionary(column => column.Name, SqlSupport.NameComparer);

        var builder = new StringBuilder();
        builder.Append("SELECT TOP (@top) * FROM ");
        builder.Append(SqlSupport.QuoteIdentifier(item.Schema));
        builder.Append('.');
        builder.Append(SqlSupport.QuoteIdentifier(item.Name));
        builder.Append(" AS ");
        builder.Append(alias);

        var parameters = new List<SqlParameter>
        {
            new("@top", SqlDbType.Int) { Value = top }
        };

        AppendFiltersAndSorts(builder, parameters, request, columns, alias);
        return (builder.ToString(), parameters);
    }

    private void AppendFiltersAndSorts(
        StringBuilder builder,
        List<SqlParameter> parameters,
        ObjectQueryRequest request,
        IReadOnlyDictionary<string, ColumnManifest> columns,
        string alias)
    {
        if (request.Filters.Count > 0)
        {
            builder.Append(" WHERE ");
            var filterClauses = new List<string>();
            for (var i = 0; i < request.Filters.Count; i++)
            {
                var filter = request.Filters[i];
                if (columns.TryGetValue(filter.Column, out var column) is false)
                {
                    throw new ArgumentException($"Unknown filter column '{filter.Column}'.");
                }

                var qualifiedColumn = $"{alias}.{SqlSupport.QuoteIdentifier(column.Name)}";
                var parameterName = $"@f{i}";
                switch (filter.Operator.ToLowerInvariant())
                {
                    case "eq":
                        filterClauses.Add($"{qualifiedColumn} = {parameterName}");
                        parameters.Add(new SqlParameter(parameterName, SqlSupport.ResolveSqlDbType(column.SqlType))
                        {
                            Value = filter.Value.HasValue ? SqlSupport.ConvertJsonValue(filter.Value.Value, ToParameter(column)) ?? DBNull.Value : DBNull.Value
                        });
                        break;
                    case "neq":
                        filterClauses.Add($"{qualifiedColumn} <> {parameterName}");
                        parameters.Add(new SqlParameter(parameterName, SqlSupport.ResolveSqlDbType(column.SqlType))
                        {
                            Value = filter.Value.HasValue ? SqlSupport.ConvertJsonValue(filter.Value.Value, ToParameter(column)) ?? DBNull.Value : DBNull.Value
                        });
                        break;
                    case "gt":
                    case "gte":
                    case "lt":
                    case "lte":
                        var sqlOperator = filter.Operator.ToLowerInvariant() switch
                        {
                            "gt" => ">",
                            "gte" => ">=",
                            "lt" => "<",
                            _ => "<="
                        };
                        filterClauses.Add($"{qualifiedColumn} {sqlOperator} {parameterName}");
                        parameters.Add(new SqlParameter(parameterName, SqlSupport.ResolveSqlDbType(column.SqlType))
                        {
                            Value = filter.Value.HasValue ? SqlSupport.ConvertJsonValue(filter.Value.Value, ToParameter(column)) ?? DBNull.Value : DBNull.Value
                        });
                        break;
                    case "like":
                        filterClauses.Add($"{qualifiedColumn} LIKE {parameterName}");
                        parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar)
                        {
                            Value = filter.Value?.ToString() ?? string.Empty
                        });
                        break;
                    case "contains":
                        filterClauses.Add($"{qualifiedColumn} LIKE {parameterName}");
                        parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar)
                        {
                            Value = $"%{filter.Value?.ToString() ?? string.Empty}%"
                        });
                        break;
                    case "startswith":
                        filterClauses.Add($"{qualifiedColumn} LIKE {parameterName}");
                        parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar)
                        {
                            Value = $"{filter.Value?.ToString() ?? string.Empty}%"
                        });
                        break;
                    case "in":
                        if (filter.Values.Count == 0)
                        {
                            throw new ArgumentException($"The 'in' operator for '{filter.Column}' requires a non-empty values collection.");
                        }

                        var names = new List<string>();
                        for (var valueIndex = 0; valueIndex < filter.Values.Count; valueIndex++)
                        {
                            var listParameterName = $"{parameterName}_{valueIndex}";
                            names.Add(listParameterName);
                            parameters.Add(new SqlParameter(listParameterName, SqlSupport.ResolveSqlDbType(column.SqlType))
                            {
                                Value = SqlSupport.ConvertJsonValue(filter.Values[valueIndex], ToParameter(column)) ?? DBNull.Value
                            });
                        }

                        filterClauses.Add($"{qualifiedColumn} IN ({string.Join(", ", names)})");
                        break;
                    case "isnull":
                        filterClauses.Add($"{qualifiedColumn} IS NULL");
                        break;
                    case "isnotnull":
                        filterClauses.Add($"{qualifiedColumn} IS NOT NULL");
                        break;
                    default:
                        throw new ArgumentException($"Unsupported filter operator '{filter.Operator}'.");
                }
            }

            builder.Append(string.Join(" AND ", filterClauses));
        }

        if (request.Sorts.Count > 0)
        {
            builder.Append(" ORDER BY ");
            builder.Append(string.Join(", ", request.Sorts.Select(sort =>
            {
                if (columns.ContainsKey(sort.Column) is false)
                {
                    throw new ArgumentException($"Unknown sort column '{sort.Column}'.");
                }

                return $"{alias}.{SqlSupport.QuoteIdentifier(sort.Column)} {(sort.Descending ? "DESC" : "ASC")}";
            })));
        }
    }

    private SqlParameter BuildSqlParameter(
        ParameterManifest parameter,
        Dictionary<string, System.Text.Json.JsonElement> requestParameters)
    {
        var sqlDbType = SqlSupport.ResolveSqlDbType(parameter.SqlType);
        var sqlParameter = new SqlParameter(parameter.Name, sqlDbType);

        if (parameter.MaxLength == -1)
        {
            sqlParameter.Size = -1;
        }
        else if (parameter.MaxLength > 0 && parameter.MaxLength != short.MaxValue)
        {
            sqlParameter.Size = parameter.MaxLength;
        }

        if (parameter.Precision > 0)
        {
            sqlParameter.Precision = parameter.Precision;
        }

        if (parameter.Scale > 0)
        {
            sqlParameter.Scale = parameter.Scale;
        }

        if (parameter.IsOutput)
        {
            sqlParameter.Direction = requestParameters.ContainsKey(parameter.Name.TrimStart('@'))
                ? ParameterDirection.InputOutput
                : ParameterDirection.Output;
        }
        else
        {
            sqlParameter.Direction = ParameterDirection.Input;
        }

        if (sqlParameter.Size == 0
            && sqlParameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput
            && RequiresExplicitSize(sqlDbType))
        {
            sqlParameter.Size = GetDefaultOutputSize(sqlDbType);
        }

        if (requestParameters.TryGetValue(parameter.Name.TrimStart('@'), out var value))
        {
            sqlParameter.Value = SqlSupport.ConvertJsonValue(value, parameter) ?? DBNull.Value;
        }
        else if (sqlParameter.Direction == ParameterDirection.Input)
        {
            sqlParameter.Value = DBNull.Value;
        }

        return sqlParameter;
    }

    private static bool RequiresExplicitSize(SqlDbType sqlDbType) => sqlDbType switch
    {
        SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.VarBinary or SqlDbType.Binary
            or SqlDbType.Char or SqlDbType.NChar or SqlDbType.Text or SqlDbType.NText
            or SqlDbType.Image or SqlDbType.Xml => true,
        _ => false
    };

    private static int GetDefaultOutputSize(SqlDbType sqlDbType) => sqlDbType switch
    {
        SqlDbType.Xml or SqlDbType.Text or SqlDbType.NText or SqlDbType.Image => -1,
        SqlDbType.NVarChar or SqlDbType.VarChar => 4000,
        SqlDbType.NChar or SqlDbType.Char => 4000,
        SqlDbType.VarBinary or SqlDbType.Binary => 8000,
        _ => 0
    };

    private string GetConnectionString(string databaseAlias)
    {
        var connectionString = _configuration[$"ConnectionStrings:{databaseAlias}"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string for database alias '{databaseAlias}' is not configured.");
        }

        return connectionString;
    }

    private static string ResolveDatabaseName(SqlConnection connection, DatabaseObjectManifest item) =>
        string.IsNullOrWhiteSpace(connection.Database) ? item.DatabaseName : connection.Database;

    private static ParameterManifest ToParameter(ColumnManifest column) =>
        new()
        {
            Name = column.Name,
            SqlType = column.SqlType,
            MaxLength = column.MaxLength,
            Precision = column.Precision,
            Scale = column.Scale,
            IsNullable = column.IsNullable
        };
}
