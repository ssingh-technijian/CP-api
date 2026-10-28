using CpApis.Mcp.Core.Options;
using Microsoft.Extensions.Options;

namespace CpApis.Mcp.Core.Services;

/// <summary>
/// Proxies the read-only CardPointe endpoints of the CP APIs REST host. The gateway
/// credentials and merchant id live only on the API host; this client carries none.
/// </summary>
public sealed class CardPointeApiClient
{
    private readonly HttpClient _httpClient;
    private readonly CpApisMcpOptions _options;
    private readonly IApiAuthorizationHeaderAccessor _authorizationHeaderAccessor;

    public CardPointeApiClient(
        HttpClient httpClient,
        IOptions<CpApisMcpOptions> options,
        IApiAuthorizationHeaderAccessor authorizationHeaderAccessor)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _authorizationHeaderAccessor = authorizationHeaderAccessor;
        _httpClient.BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public Task<string> InquireAsync(string retref, CancellationToken cancellationToken = default) =>
        GetAsync($"api/cardpointe/inquire/{Uri.EscapeDataString(retref)}", cancellationToken);

    public Task<string> SettlementStatusAsync(string date, CancellationToken cancellationToken = default) =>
        GetAsync($"api/cardpointe/settlestat?date={Uri.EscapeDataString(date)}", cancellationToken);

    public Task<string> FundingAsync(string date, CancellationToken cancellationToken = default) =>
        GetAsync($"api/cardpointe/funding?date={Uri.EscapeDataString(date)}", cancellationToken);

    public Task<string> MerchantInquiryAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/cardpointe/merchant", cancellationToken);

    private async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var authorization = _authorizationHeaderAccessor.GetAuthorizationHeader();
        if (string.IsNullOrWhiteSpace(authorization) is false)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode is false)
        {
            throw new InvalidOperationException(
                $"CardPointe API call failed for {path}. HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }
}
