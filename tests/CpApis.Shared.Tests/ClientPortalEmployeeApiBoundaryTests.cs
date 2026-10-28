using System.Net;
using System.Security.Claims;
using CpApis.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class ClientPortalEmployeeApiBoundaryTests
{
    private const string EmployeeClient = "95271648-21aa-436b-a088-7de56a6f0955";
    private const string ExistingMachineClient = "1cb85b95-8f51-4b69-a8a9-834c8d3ce0e1";

    [Theory]
    [InlineData(true, EmployeeClient, "azp", HttpStatusCode.Forbidden)]
    [InlineData(true, EmployeeClient, "appid", HttpStatusCode.Forbidden)]
    [InlineData(true, ExistingMachineClient, "azp", HttpStatusCode.OK)]
    [InlineData(false, EmployeeClient, "azp", HttpStatusCode.Forbidden)]
    public async Task EmployeeClientCannotBypassCuratedToolsThroughTheGenericApi(
        bool enabled, string clientId, string claimType, HttpStatusCode expected)
    {
        var reachedBackend = 0;
        using var host = new TestServer(new WebHostBuilder().ConfigureServices(services =>
            services.Configure<ClientPortalEmployeeApiOptions>(o => { o.Enabled = enabled; o.TrustedClientIds = [EmployeeClient]; }))
            .Configure(app =>
            {
                app.Use((context, next) =>
                {
                    context.User = new(new ClaimsIdentity([new Claim(claimType, clientId)], "owned-inprocess-fixture"));
                    return next();
                });
                app.UseMiddleware<ClientPortalEmployeeApiBoundary>();
                app.Run(async context => { reachedBackend++; await context.Response.WriteAsync("owned fixture: no database"); });
            }));
        using var client = host.CreateClient();
        using var response = await client.PostAsync("/api/objects/client-portal/stored-procedure/dbo/arbitrary", new StringContent("{}"));
        Assert.Equal(expected, response.StatusCode); Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, reachedBackend);
    }
}
