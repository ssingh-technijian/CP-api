using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using CpApis.Mcp.Http.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class ClientPortalEmployeeActionBoundaryTests
{
    [Theory]
    [InlineData("client_portal_approve_action")]
    [InlineData("client_portal_reject_action")]
    [InlineData("client_portal_get_review")]
    [InlineData("client_portal_execute_sql")]
    [InlineData("client_portal_pay_invoice")]
    public void ModelCannotDiscoverOrInvokeApprovalAndPaymentNames(string name)=>Assert.False(ClientPortalEmployeeBoundary.IsEmployeeTool(name));

    [Fact]
    public void RegisteredEmployeeMethodsHaveNoActorOrCredentialParameters()
    {
        var methods=new[]{typeof(ClientPortalEmployeeReadTools),typeof(ClientPortalEmployeeActionTools),typeof(ClientPortalEmployeeDetailTools)}
            .SelectMany(t=>t.GetMethods()).Where(m=>m.IsDefined(typeof(McpServerToolAttribute))).ToArray();
        Assert.Equal(29,methods.Length);
        Assert.DoesNotContain(methods,m=>m.Name.Contains("Approve",StringComparison.OrdinalIgnoreCase)||m.Name.Contains("Review",StringComparison.OrdinalIgnoreCase));
        var forbidden=new[]{"actor","userId","username","email","tenantId","role","sql","procedure","token","reviewToken"};
        Assert.DoesNotContain(methods.SelectMany(m=>m.GetParameters()),p=>forbidden.Contains(p.Name,StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DefaultDisabledFeatureNeverOpensSqlOrCallsNativeExecutor()
    {
        var db=new NoSqlDatabase();var executor=new NoNativeExecutor();
        var employees=new ClientPortalEmployeeContext(new Principal(null),Microsoft.Extensions.Options.Options.Create(new ClientPortalEmployeeOptions()));
        var service=new ClientPortalEmployeeActionService(employees,db,executor);
        var tools=new ClientPortalEmployeeActionTools(service,Microsoft.Extensions.Options.Options.Create(new ClientPortalEmployeeActionOptions()));
        Assert.False((await tools.CreateTicketAsync(Guid.NewGuid(),1,2,"title","description")).Success);
        Assert.False((await tools.StatusAsync(Guid.NewGuid())).Success);
        Assert.Equal(0,db.Calls);Assert.Equal(0,executor.Calls);
    }

    [Theory]
    [InlineData("GET","review")]
    [InlineData("POST","approve")]
    [InlineData("POST","reject")]
    public async Task ReviewEndpointsRequireAuthentication(string method,string route)
    {
        using var server=Server(true);using var client=server.CreateClient();
        using var request=new HttpRequestMessage(new HttpMethod(method),$"/employee-actions/{Guid.NewGuid():D}/{route}");
        if(method=="POST")request.Content=JsonContent.Create(new EmployeeActionDecision(new string('A',64),new string('n',43)));
        using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task DisabledReviewEndpointsCannotExecuteAnything(string route)
    {
        using var server=Server(false);using var client=server.CreateClient();client.DefaultRequestHeaders.Add("X-Fixture-Authenticated","1");
        using var response=route=="review"?await client.GetAsync($"/employee-actions/{Guid.NewGuid():D}/{route}")
            :await client.PostAsJsonAsync($"/employee-actions/{Guid.NewGuid():D}/{route}",new EmployeeActionDecision(new string('A',64),new string('n',43)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Equal(0,server.Services.GetRequiredService<NoSqlDatabase>().Calls);
        Assert.Equal("no-store",response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("review")]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task ApplicationIdentityCannotUseHumanEndpoints(string route)
    {
        using var server=Server(true);using var client=server.CreateClient();client.DefaultRequestHeaders.Add("X-Fixture-Authenticated","1");
        using var response=route=="review"?await client.GetAsync($"/employee-actions/{Guid.NewGuid():D}/{route}")
            :await client.PostAsJsonAsync($"/employee-actions/{Guid.NewGuid():D}/{route}",new EmployeeActionDecision(new string('A',64),new string('n',43)));
        Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
        Assert.Equal(0,server.Services.GetRequiredService<NoNativeExecutor>().Calls);
    }

    private static TestServer Server(bool enabled)=>new(new WebHostBuilder().ConfigureServices(s=>
    {
        s.AddRouting();s.AddLogging();s.AddHttpContextAccessor();s.AddAuthorization();
        s.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions,MachineAuthentication>("Fixture",_=>{});
        s.Configure<ClientPortalEmployeeActionOptions>(o=>o.Enabled=enabled);
        s.Configure<ClientPortalEmployeeOptions>(o=>{o.Enabled=true;o.ResourceClientId=Guid.NewGuid().ToString();});
        s.AddSingleton<IEmployeePrincipalAccessor,HttpEmployeePrincipalAccessor>();s.AddSingleton<ClientPortalEmployeeContext>();
        s.AddSingleton<NoSqlDatabase>();s.AddSingleton<IEmployeeActionDatabase>(p=>p.GetRequiredService<NoSqlDatabase>());
        s.AddSingleton<NoNativeExecutor>();s.AddSingleton<IEmployeeActionExecutor>(p=>p.GetRequiredService<NoNativeExecutor>());
        s.AddSingleton<ClientPortalEmployeeActionService>();
    }).Configure(app=>{app.UseRouting();app.UseAuthentication();app.UseAuthorization();app.UseEndpoints(e=>e.MapEmployeeActions());}));

    private sealed class Principal(ClaimsPrincipal? user):IEmployeePrincipalAccessor {public ClaimsPrincipal? GetPrincipal()=>user;}
    private sealed class NoSqlDatabase:IEmployeeActionDatabase
    {
        public int Calls;
        public async Task<T> InTransactionAsync<T>(Func<SqlConnection,SqlTransaction,Task<T>> operation,CancellationToken ct)
        {Calls++;await using var connection=new SqlConnection();return await operation(connection,null!);}
    }
    private sealed class NoNativeExecutor:IEmployeeActionExecutor
    {
        public int Calls;
        public Task<EmployeeActionPlan> PrepareAsync(string op,System.Text.Json.JsonElement p,SqlConnection c,SqlTransaction t,PortalEmployee a,CancellationToken ct)
        {Calls++;throw new InvalidOperationException("Native executor must not be reached.");}
        public Task<EmployeeActionResult> ApplyAsync(string op,System.Text.Json.JsonElement p,SqlConnection c,SqlTransaction t,PortalEmployee a,CancellationToken ct)
        {Calls++;throw new InvalidOperationException("Native executor must not be reached.");}
    }
    private sealed class MachineAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
        :AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()=>Task.FromResult(Request.Headers.ContainsKey("X-Fixture-Authenticated")
            ?AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles","machine")],Scheme.Name)),Scheme.Name))
            :AuthenticateResult.NoResult());
    }
}
