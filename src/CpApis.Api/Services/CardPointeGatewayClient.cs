using System.Net.Http.Headers;
using System.Text;
using CpApis.Api.Options;
using Microsoft.Extensions.Options;

namespace CpApis.Api.Services;

/// <summary>
/// Thin typed client for the CardPointe (CardConnect) Gateway REST API. Read-only endpoints
/// only: inquire, settlestat, funding, inquireMerchant. Authorization, capture, refund,
/// void, profile writes, and batch open/close are intentionally excluded so this service
/// can never move money. Credentials travel as one HTTP Basic header straight to the
/// gateway and never appear in responses, errors, or logs. TLS certificate validation is
/// left fully on (the legacy ClientPortal client disabled it; that is not copied here).
/// </summary>
public sealed class CardPointeGatewayClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly CardPointeOptions _options;

    public CardPointeGatewayClient(HttpClient httpClient, IOptions<CardPointeOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.Timeout = Timeout;

        if (_options.IsConfigured)
        {
            _httpClient.BaseAddress = new Uri(
                $"https://{_options.Site.Trim().TrimEnd('/')}/cardconnect/rest/",
                UriKind.Absolute);
        }
    }

    /// <summary>Transaction inquiry by retrieval reference number (retref).</summary>
    public Task<string> InquireAsync(string retref, CancellationToken cancellationToken = default) =>
        GetAsync($"inquire/{Uri.EscapeDataString(retref)}?merchid={Uri.EscapeDataString(_options.MerchantId)}", cancellationToken);

    /// <summary>Settlement status report. Date is mandatory: a bare settlestat call retrieves and consumes unretrieved batches.</summary>
    public Task<string> SettleStatAsync(string date, CancellationToken cancellationToken = default) =>
        GetAsync($"settlestat?merchid={Uri.EscapeDataString(_options.MerchantId)}&date={Uri.EscapeDataString(date)}", cancellationToken);

    /// <summary>Funding (deposit) report. Date is mandatory for the same stateful-consumption reason as settlestat.</summary>
    public Task<string> FundingAsync(string date, CancellationToken cancellationToken = default) =>
        GetAsync($"funding?merchid={Uri.EscapeDataString(_options.MerchantId)}&date={Uri.EscapeDataString(date)}", cancellationToken);

    /// <summary>Merchant profile inquiry for the configured merchant.</summary>
    public Task<string> InquireMerchantAsync(CancellationToken cancellationToken = default) =>
        GetAsync($"inquireMerchant/{Uri.EscapeDataString(_options.MerchantId)}", cancellationToken);

    private async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        if (_options.IsConfigured is false)
        {
            throw new InvalidOperationException(
                "CardPointe gateway is not configured. Set CardPointe:Site, CardPointe:MerchantId, CardPointe:Username, and CardPointe:Password through environment variables or appsettings.Local.json.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_options.Username}:{_options.Password}")));
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode is false)
        {
            throw new InvalidOperationException(
                $"CardPointe gateway call failed. HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }
}
