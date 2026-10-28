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
    private readonly IApiAuthorizationHeaderAccessor _authorizationHeaderAccessor;

    public StoredProcedureApiClient(
        HttpClient httpClient,
        IOptions<CpApisMcpOptions> options,
        IApiAuthorizationHeaderAccessor authorizationHeaderAccessor)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _authorizationHeaderAccessor = authorizationHeaderAccessor;
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

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildExecutePath(item))
        {
            Content = JsonContent.Create(request)
        };
        var authorization = _authorizationHeaderAccessor.GetAuthorizationHeader();
        if (string.IsNullOrWhiteSpace(authorization) is false)
        {
            httpRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode is false)
        {
            throw new InvalidOperationException(
                $"API call failed for {item.DatabaseAlias}:{item.Schema}.{item.Name}. HTTP {(int)response.StatusCode}: {body}");
        }

        var payload = JsonSerializer.Deserialize<CommandExecutionResponse>(body, JsonOptions);
        return payload ?? throw new InvalidOperationException($"API call for {item.DatabaseAlias}:{item.Schema}.{item.Name} returned an empty response body.");
    }

    public async Task<CommandExecutionResponse> PatchTicketAsync(DatabaseObjectManifest item, int ticketId,
        Dictionary<string, JsonElement> body, CancellationToken cancellationToken = default)
    {
        if (ticketId <= 0) throw new ArgumentOutOfRangeException(nameof(ticketId));
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/tickets/{ticketId}")
        {
            Content = JsonContent.Create(body)
        };
        var authorization = _authorizationHeaderAccessor.GetAuthorizationHeader();
        if (!string.IsNullOrWhiteSpace(authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ticket update API returned HTTP {(int)response.StatusCode}.");
        var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(await response.Content.ReadAsStringAsync(cancellationToken), JsonOptions)
            ?? throw new InvalidOperationException("Ticket update API returned an empty response.");
        return new CommandExecutionResponse
        {
            DatabaseAlias = item.DatabaseAlias, DatabaseName = item.DatabaseName, Schema = item.Schema,
            Name = item.Name, Kind = item.Kind,
            ResultSets = [new ResultSetPayload { Index = 0, RowCount = 1, Rows = [row] }]
        };
    }
}
