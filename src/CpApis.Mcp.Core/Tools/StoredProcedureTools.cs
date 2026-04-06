using System.ComponentModel;
using System.Text.Json;
using CpApis.Mcp.Core.Models;
using CpApis.Mcp.Core.Options;
using CpApis.Mcp.Core.Services;
using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;
using CpApis.Shared.Services;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CpApis.Mcp.Core.Tools;

[McpServerToolType]
public sealed class StoredProcedureTools
{
    private readonly ManifestCatalogService _catalogService;
    private readonly StoredProcedureApiClient _apiClient;
    private readonly CpApisMcpOptions _options;

    public StoredProcedureTools(
        ManifestCatalogService catalogService,
        StoredProcedureApiClient apiClient,
        IOptions<CpApisMcpOptions> options)
    {
        _catalogService = catalogService;
        _apiClient = apiClient;
        _options = options.Value;
    }

    [McpServerTool(Name = "list_stored_procedure_modules")]
    [Description("List the available stored-procedure modules discovered from the Client Portal manifest.")]
    public StoredProcedureModuleResult ListModules(string? databaseAlias = null)
    {
        var modules = _catalogService.GetObjects(databaseAlias, "stored-procedure")
            .DistinctBy(item => item.Key)
            .Select(item => item.Module)
            .Where(module => string.IsNullOrWhiteSpace(module) is false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(module => module, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StoredProcedureModuleResult
        {
            Count = modules.Count,
            Modules = modules
        };
    }

    [McpServerTool(Name = "list_stored_procedures")]
    [Description("List stored procedures available through CP APIs. Filter by module, database alias, or a partial name. Use describe_stored_procedure before execution to inspect parameters.")]
    public StoredProcedureListResult ListStoredProcedures(
        string? module = null,
        string? databaseAlias = null,
        string? search = null,
        bool activeOnly = true,
        bool includeWriteProcedures = true,
        int limit = 100)
    {
        var normalizedLimit = Math.Clamp(limit <= 0 ? _options.DefaultListLimit : limit, 1, _options.MaxListLimit);
        IEnumerable<DatabaseObjectManifest> query = _catalogService.GetObjects(databaseAlias, "stored-procedure")
            .DistinctBy(item => item.Key);

        if (string.IsNullOrWhiteSpace(module) is false)
        {
            query = query.Where(item => string.Equals(item.Module, module, StringComparison.OrdinalIgnoreCase));
        }

        if (activeOnly)
        {
            query = query.Where(item => item.IsActiveRoot);
        }

        if (includeWriteProcedures is false)
        {
            query = query.Where(item => item.IsWriteOperation is false);
        }

        if (string.IsNullOrWhiteSpace(search) is false)
        {
            query = query.Where(item =>
                item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Schema.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Module.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var items = query
            .OrderBy(item => item.Module, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StoredProcedureListResult
        {
            TotalCount = items.Count,
            ReturnedCount = Math.Min(items.Count, normalizedLimit),
            Items = items
                .Take(normalizedLimit)
                .Select(ToSummary)
                .ToList()
        };
    }

    [McpServerTool(Name = "describe_stored_procedure")]
    [Description("Show the exact input parameters, example request body, source references, and dependency trails for one stored procedure.")]
    public StoredProcedureDetail DescribeStoredProcedure(string databaseAlias, string schema, string name)
    {
        var item = _catalogService.GetRequiredObject(databaseAlias, "stored-procedure", schema, name);
        var inputParameters = StoredProcedureMetadataBuilder.GetInputParameters(item);

        return new StoredProcedureDetail
        {
            Module = item.Module,
            DatabaseAlias = item.DatabaseAlias,
            DatabaseName = item.DatabaseName,
            Schema = item.Schema,
            Name = item.Name,
            IsActiveRoot = item.IsActiveRoot,
            IsWriteOperation = item.IsWriteOperation,
            ExecutePath = "/" + _apiClient.BuildExecutePath(item),
            BodyDescription = StoredProcedureMetadataBuilder.BuildRequestBodyDescription(item),
            RequestTemplate = StoredProcedureMetadataBuilder.BuildRequestTemplate(item),
            InputParameters = inputParameters.Select(parameter => new StoredProcedureParameterInfo
            {
                Name = parameter.Name.TrimStart('@'),
                SqlType = parameter.SqlType,
                MaxLength = parameter.MaxLength,
                Precision = parameter.Precision,
                Scale = parameter.Scale,
                IsNullable = parameter.IsNullable,
                HasDefaultValue = parameter.HasDefaultValue,
                ExampleValue = StoredProcedureMetadataBuilder.BuildExampleValue(parameter)
            }).ToList(),
            SourceReferences = item.SourceReferences
                .Select(reference => new StoredProcedureSourceReference
                {
                    FilePath = reference.FilePath,
                    LineNumber = reference.LineNumber,
                    MatchKind = reference.MatchKind
                })
                .ToList(),
            DependencyTrails = item.DependencyTrails
        };
    }

    [McpServerTool(Name = "execute_stored_procedure")]
    [Description("Execute one stored procedure through the CP APIs HTTP layer. For write procedures, set allowWrite=true explicitly after reviewing the parameters.")]
    public async Task<StoredProcedureExecutionResult> ExecuteStoredProcedureAsync(
        string databaseAlias,
        string schema,
        string name,
        Dictionary<string, JsonElement>? parameters = null,
        bool allowWrite = false)
    {
        var item = _catalogService.GetRequiredObject(databaseAlias, "stored-procedure", schema, name);

        if (_options.RequireWriteConfirmation && item.IsWriteOperation && allowWrite is false)
        {
            return new StoredProcedureExecutionResult
            {
                Executed = false,
                WriteConfirmationBypassed = false,
                Message = $"Stored procedure {databaseAlias}:{schema}.{name} is marked as a write operation. Call describe_stored_procedure first, then rerun with allowWrite=true if execution is intentional."
            };
        }

        var response = await _apiClient.ExecuteStoredProcedureAsync(item, parameters);
        return new StoredProcedureExecutionResult
        {
            Executed = true,
            WriteConfirmationBypassed = item.IsWriteOperation && allowWrite,
            Response = response,
            Message = $"Executed {databaseAlias}:{schema}.{name} through {_apiClient.BuildExecutePath(item)}"
        };
    }

    private StoredProcedureSummary ToSummary(DatabaseObjectManifest item)
    {
        return new StoredProcedureSummary
        {
            Module = item.Module,
            DatabaseAlias = item.DatabaseAlias,
            Schema = item.Schema,
            Name = item.Name,
            IsActiveRoot = item.IsActiveRoot,
            IsWriteOperation = item.IsWriteOperation,
            InputParameterCount = StoredProcedureMetadataBuilder.GetInputParameters(item).Count,
            ExecutePath = "/" + _apiClient.BuildExecutePath(item)
        };
    }
}
