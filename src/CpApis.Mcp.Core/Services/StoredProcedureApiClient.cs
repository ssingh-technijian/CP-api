using System.Net.Http.Json;
using System.Text.Json;
using CpApis.Mcp.Core.Options;
using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;
using Microsoft.Extensions.Options;

namespace CpApis.Mcp.Core.Services;

public sealed class StoredProcedureApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly CpApisMcpOptions _options;

    public StoredProcedureApiClient(
        HttpClient httpClient,
        IOptions<CpApisMcpOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public string BuildExecutePath(DatabaseObjectManifest item)
    {
        var moduleSegment = StoredProcedureMetadataBuilder.NormalizeModuleSegment(item.Module);
        return $"api/modules/{moduleSegment}/stored-procedures/{Uri.EscapeDataString(item.DatabaseAlias)}/{Uri.EscapeDataString(item.Schema)}/{Uri.EscapeDataString(item.Name)}/execute";
    }

    public async Task<CommandExecutionResponse> ExecuteStoredProcedureAsync(
        DatabaseObjectManifest item,
        Dictionary<string, JsonElement>? parameters,
        CancellationToken cancellationToken = default)
    {
        var request = new StoredProcedureExecutionRequest
        {
            Parameters = parameters is null
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(parameters, StringComparer.OrdinalIgnoreCase)
        };

        using var response = await _httpClient.PostAsJsonAsync(BuildExecutePath(item), request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode is false)
        {
            throw new InvalidOperationException(
                $"API call failed for {item.DatabaseAlias}:{item.Schema}.{item.Name}. HTTP {(int)response.StatusCode}: {body}");
        }

        var payload = JsonSerializer.Deserialize<CommandExecutionResponse>(body, JsonOptions);
        return payload ?? throw new InvalidOperationException($"API call for {item.DatabaseAlias}:{item.Schema}.{item.Name} returned an empty response body.");
    }
}
