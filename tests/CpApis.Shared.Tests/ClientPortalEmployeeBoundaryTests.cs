using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Http.Options;
using CpApis.Mcp.Http.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace CpApis.Shared.Tests;

// In-process SDK transport/authorization tests. Fixture authentication exists only in this
// TestServer; no test authentication scheme, bypass endpoint or test identity is deployed.
public sealed class ClientPortalEmployeeBoundaryTests
{
    private static readonly string ClientId = "42873c3e-a7bc-49a2-808e-8fa895d3c01f";
    private const string EmployeeResource = "a5e2c432-af80-4954-b5c3-c84638803eed";
    private static readonly string First = "d680f7d6-4d7e-42a3-9b24-2dd705105c5e";
    private static readonly string Second = "01f70eef-cb04-4f3d-8179-74c28d4a85bd";

    [Fact]
    public async Task EmployeeListsOnlyCuratedToolsAndCannotInvokeRawProcedures()
    {
        using var host = Host(); using var client = host.CreateClient();
        var session = await Initialize(client, First);
        var list = await Rpc(client, First, session, "tools/list", new { });
        Assert.Equal(new[] { "client_portal_whoami" }, list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
        var denied = await Rpc(client, First, session, "tools/call", new { name = "execute_stored_procedure", arguments = new { allowWrite = false } });
        Assert.True(denied.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, host.Services.GetRequiredService<Calls>().MachineCalls);
        var own = await Rpc(client, First, session, "tools/call", new { name = "client_portal_whoami", arguments = new { } });
        Assert.Contains(First, own.GetRawText());
        Assert.Equal(1, host.Services.GetRequiredService<Calls>().EmployeeCalls);
        await Close(client, First, session);
    }

    [Fact]
    public async Task MachineIntegrationKeepsItsToolsAndCannotActAsAnEmployee()
    {
        using var host = Host(); using var client = host.CreateClient();
        var session = await Initialize(client, First, machine: true);
        var list = await Rpc(client, First, session, "tools/list", new { }, machine: true);
        Assert.Equal(new[] { "execute_stored_procedure" }, list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
        var denied = await Rpc(client, First, session, "tools/call", new { name = "client_portal_whoami", arguments = new { } }, machine: true);
        Assert.True(denied.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, host.Services.GetRequiredService<Calls>().EmployeeCalls);
        var machine = await Rpc(client, First, session, "tools/call", new { name = "execute_stored_procedure", arguments = new { } }, machine: true);
        Assert.False(machine.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Equal(1, host.Services.GetRequiredService<Calls>().MachineCalls);
        await Close(client, First, session, machine: true);
    }

    [Fact]
    public async Task DifferentEmployeeAndUnknownSessionAreRejectedBeforeToolExecution()
    {
        using var host = Host(); using var client = host.CreateClient();
        var session = await Initialize(client, First);
        using var foreign = await Send(client, Second, session, "tools/call", new { name = "client_portal_whoami", arguments = new { } });
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var machineHijack = await Send(client, First, session, "tools/call", new { name = "client_portal_whoami", arguments = new { } }, machine: true);
        Assert.Equal(HttpStatusCode.Forbidden, machineHijack.StatusCode);
        using var unknown = await Send(client, First, "unknown-session", "tools/list", new { });
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(0, host.Services.GetRequiredService<Calls>().EmployeeCalls);
        await Close(client, First, session);
        using var reused = await Send(client, First, session, "tools/list", new { });
        Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
    }

    [Fact]
    public async Task DisabledEmployeeModeDoesNotFallBackToMachineTools()
    {
        using var host = Host(enabled: false); using var client = host.CreateClient();
        using var response = await Send(client, First, null, "initialize", Initialization());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Services.GetRequiredService<Calls>().EmployeeCalls);
    }

    [Fact]
    public async Task ApplicationTokenForTheEmployeeAudienceCannotUseLegacyMachineTools()
    {
        using var host = Host(); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-Employee-Resource", "true");
        using var response = await Send(client, First, null, "initialize", Initialization(), machine: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Services.GetRequiredService<Calls>().MachineCalls);
    }

    private static TestServer Host(bool enabled = true) => new(new WebHostBuilder()
        .ConfigureServices(services =>
        {
            services.AddRouting(); services.AddHttpContextAccessor(); services.AddSingleton<Calls>();
            services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
            services.AddAuthorization();
            services.Configure<ClientPortalEmployeeOptions>(o => { o.Enabled = enabled; o.TrustedClientIds = [ClientId]; o.ResourceClientId = EmployeeResource; });
            services.AddSingleton<IOptions<CpApisMcpHttpOptions>>(Microsoft.Extensions.Options.Options.Create(new CpApisMcpHttpOptions { Path = "/mcp", Stateless = false }));
            services.AddMcpServer().WithHttpTransport(o => o.Stateless = false).WithTools<ProbeTools>()
                .WithRequestFilters(f => { f.AddListToolsFilter(ClientPortalEmployeeBoundary.FilterList); f.AddCallToolFilter(ClientPortalEmployeeBoundary.FilterCall); });
        })
        .Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseMiddleware<ClientPortalEmployeeBoundary>();
            app.UseEndpoints(e => e.MapMcp("/mcp").RequireAuthorization());
        }));

    private static object Initialization() => new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-inprocess-test", version = "1" } };

    private static async Task<string> Initialize(HttpClient client, string actor, bool machine = false)
    {
        using var response = await Send(client, actor, null, "initialize", Initialization(), machine);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = response.Headers.GetValues("Mcp-Session-Id").Single();
        using var ready = await Send(client, actor, session, "notifications/initialized", new { }, machine, notification: true);
        Assert.True(ready.IsSuccessStatusCode); return session;
    }

    private static async Task<JsonElement> Rpc(HttpClient client, string actor, string session, string method, object parameters, bool machine = false)
    {
        using var response = await Send(client, actor, session, method, parameters, machine);
        Assert.True(response.IsSuccessStatusCode, "In-process MCP request failed.");
        var text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Join("\n", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].TrimStart()));
        using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string actor, string? session,
        string method, object parameters, bool machine = false, bool notification = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Add("X-Fixture-Actor", actor); if (machine) request.Headers.Add("X-Fixture-Machine", "true");
        request.Headers.Add("Accept", "application/json, text/event-stream");
        if (session is not null) { request.Headers.Add("Mcp-Session-Id", session); request.Headers.Add("MCP-Protocol-Version", "2025-06-18"); }
        object payload = notification ? new { jsonrpc = "2.0", method, @params = parameters }
            : new { jsonrpc = "2.0", id = "owned-test", method, @params = parameters };
        request.Content = JsonContent.Create(payload); return await client.SendAsync(request);
    }

    private static async Task Close(HttpClient client, string actor, string session, bool machine = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/mcp"); request.Headers.Add("X-Fixture-Actor", actor);
        if (machine) request.Headers.Add("X-Fixture-Machine", "true");
        request.Headers.Add("Mcp-Session-Id", session); request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        using var response = await client.SendAsync(request); Assert.True(response.IsSuccessStatusCode);
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>
            {
                new("iss", $"https://login.microsoftonline.com/{ClientPortalEmployeeContext.MicrosoftTenant}/v2.0"),
                new("tid", ClientPortalEmployeeContext.MicrosoftTenant),
                new("aud", "api://" + (Request.Headers.ContainsKey("X-Fixture-Machine") && !Request.Headers.ContainsKey("X-Fixture-Employee-Resource") ? ClientPortalEmployeeContext.ResourceClient : EmployeeResource)),
                new("oid", Request.Headers["X-Fixture-Actor"].ToString()),
                new("azp", Request.Headers.ContainsKey("X-Fixture-Machine") ? ClientPortalEmployeeContext.ResourceClient : ClientId),
                new("preferred_username", "mcpqa@technijian.com")
            };
            if (!Request.Headers.ContainsKey("X-Fixture-Machine")) { claims.Add(new("scp", ClientPortalEmployeeContext.EmployeeScope)); claims.Add(new("acct", "0")); }
            return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class Calls { public int MachineCalls, EmployeeCalls; }
    [McpServerToolType]
    private sealed class ProbeTools(Calls calls)
    {
        [McpServerTool(Name = "execute_stored_procedure")]
        public string Machine() { calls.MachineCalls++; return "owned in-process machine fixture; no database"; }
        [McpServerTool(Name = "client_portal_whoami")]
        public string Employee(RequestContext<CallToolRequestParams> request) { calls.EmployeeCalls++; return request.User!.FindFirst("oid")!.Value; }
    }
}
