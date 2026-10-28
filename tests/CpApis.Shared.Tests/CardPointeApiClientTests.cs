using System.Net;
using System.Net.Http.Headers;
using CpApis.Mcp.Core.Options;
using CpApis.Mcp.Core.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class CardPointeApiClientTests
{
    [Fact]
    public async Task ForwardsCallerAuthorizationToProtectedApi()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new CardPointeApiClient(
            httpClient,
            Microsoft.Extensions.Options.Options.Create(new CpApisMcpOptions { ApiBaseUrl = "http://127.0.0.1:5180" }),
            new FixedAuthorizationHeaderAccessor("Bearer caller-token"));

        var body = await client.MerchantInquiryAsync();

        Assert.Equal("{}", body);
        Assert.Equal(HttpMethod.Get, handler.Request?.Method);
        Assert.Equal("Bearer caller-token", handler.Request?.Headers.Authorization?.ToString());
        Assert.EndsWith("api/cardpointe/merchant", handler.Request?.RequestUri?.ToString(), StringComparison.Ordinal);
    }

    private sealed class FixedAuthorizationHeaderAccessor(string value) : IApiAuthorizationHeaderAccessor
    {
        public string? GetAuthorizationHeader() => value;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
