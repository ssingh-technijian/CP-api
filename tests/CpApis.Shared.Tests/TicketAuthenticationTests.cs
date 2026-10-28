using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CpApis.Api.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class TicketAuthenticationTests
{
    [Theory]
    [InlineData("/api/tickets")]
    [InlineData("/api/tickets/create")]
    public async Task AnonymousCreate_CannotReachValidationOrTheDatabase(string path)
    {
        using var server = Server();
        using var client = server.CreateClient();
        using var response = await client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/tickets")]
    [InlineData("/api/tickets/create")]
    public async Task AuthenticatedCreate_RetainsItsExistingValidationContract(string path)
    {
        using var server = Server();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer fixture-auth");
        using var response = await client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static TestServer Server() => new(new WebHostBuilder()
        .ConfigureServices(services =>
        {
            services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(TicketsController).Assembly);
        })
        .Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers().RequireAuthorization());
        }));

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.Authorization == "Bearer fixture-auth"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "qa@mcpqa.invalid")], "fixture")), "fixture"))
                : AuthenticateResult.NoResult());
    }
}
