using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptionsFactory=Microsoft.Extensions.Options.Options;
using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace CpApis.Shared.Tests;

public sealed class EmployeeActionDiagnosticsTests
{
    [Theory]
    [InlineData(-2)]
    [InlineData(1205)]
    [InlineData(1222)]
    [InlineData(245)]
    public async Task DatabaseFailureRetainsCorrelationWithoutDisclosingSqlOrPayload(int number)
    {
        var log=new CaptureLog();var request=Guid.NewGuid();var tools=Tools(SqlFailure(number),log);
        var result=await tools.UpdateContractAsync(request,42,"do-not-log-payload");
        Assert.False(result.Success);Assert.Null(result.Data);Assert.Contains("No successful change is confirmed",result.Error);
        var entry=Assert.Single(log.Entries);Assert.Equal(6201,entry.Id.Id);Assert.Null(entry.Exception);
        Assert.Equal("contract.edit",entry.Fields["Operation"]);Assert.Equal(request,entry.Fields["RequestId"]);
        Assert.Equal(number,entry.Fields["SqlErrorNumber"]);Assert.Equal("database",entry.Fields["Category"]);
        var combined=JsonSerializer.Serialize(result)+JsonSerializer.Serialize(entry.Fields);
        Assert.DoesNotContain("secret-database-marker",combined);Assert.DoesNotContain("do-not-log-payload",combined);
    }

    [Fact]
    public async Task StatusFailureIsCorrelatedToActionRatherThanAProposalRequest()
    {
        var log=new CaptureLog();var id=Guid.NewGuid();await Tools(SqlFailure(-2),log).StatusAsync(id);
        var entry=Assert.Single(log.Entries);Assert.Equal("status",entry.Fields["Operation"]);
        Assert.Equal(id,entry.Fields["ActionId"]);Assert.Null(entry.Fields["RequestId"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DenialAndNativeRuleFailureKeepTheirExistingOutcome(bool permission)
    {
        Exception failure=permission?new EmployeeAccessDeniedException("secret-database-marker"):new EmployeeActionException(409,"Native record is protected.");
        var log=new CaptureLog();var result=await Tools(failure,log).UpdateContractAsync(Guid.NewGuid(),42,"unused");
        Assert.False(result.Success);Assert.Null(result.Data);var entry=Assert.Single(log.Entries);
        Assert.Equal(permission?403:409,entry.Fields["StatusCode"]);Assert.Null(entry.Exception);
        Assert.DoesNotContain("secret-database-marker",JsonSerializer.Serialize(entry.Fields));
        if(!permission)Assert.Equal("Native record is protected.",result.Error);
    }

    [Fact]
    public async Task BrokenDiagnosticSinkCannotTurnHandledFailureIntoAnUnhandledError()
    {
        var result=await Tools(SqlFailure(1222),new CaptureLog{ThrowOnWrite=true}).UpdateContractAsync(Guid.NewGuid(),42,"unused");
        Assert.False(result.Success);Assert.Contains("No successful change is confirmed",result.Error);
    }

    [Fact]
    public async Task CancellationStillPropagatesWithoutBeingMisreportedAsADatabaseFailure()
    {
        var log=new CaptureLog();var tools=Tools(new OperationCanceledException(),log);
        await Assert.ThrowsAsync<OperationCanceledException>(()=>tools.StatusAsync(Guid.NewGuid()));Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task ExistingTwoArgumentConstructorRemainsCallable()
    {
        var service=Service(new EmployeeActionException(409,"protected"));
        var tools=new ClientPortalEmployeeActionTools(service,OptionsFactory.Create(new ClientPortalEmployeeActionOptions{Enabled=true}));
        Assert.False((await tools.StatusAsync(Guid.NewGuid())).Success);
        Assert.NotNull(typeof(ClientPortalEmployeeActionTools).GetConstructor([typeof(ClientPortalEmployeeActionService),typeof(IOptions<ClientPortalEmployeeActionOptions>)]));
    }

    [Fact]
    public async Task DependencyInjectionUsesTheDiagnosticConstructor()
    {
        var log=new CaptureLog();var services=new ServiceCollection();services.AddSingleton(Service(SqlFailure(-2)));
        services.AddSingleton<IOptions<ClientPortalEmployeeActionOptions>>(OptionsFactory.Create(new ClientPortalEmployeeActionOptions{Enabled=true}));
        services.AddSingleton<ILogger<ClientPortalEmployeeActionTools>>(log);using var provider=services.BuildServiceProvider();
        var tools=ActivatorUtilities.CreateInstance<ClientPortalEmployeeActionTools>(provider);
        Assert.False((await tools.StatusAsync(Guid.NewGuid())).Success);Assert.Single(log.Entries);
    }

    [Fact]
    public async Task ActualMcpSdkDispatchRetainsSafeFailureDiagnostics()
    {
        var log=new CaptureLog();
        using var server=new TestServer(new WebHostBuilder().ConfigureServices(services=>
        {
            services.AddRouting();services.AddLogging();services.AddSingleton(Service(SqlFailure(1222)));
            services.AddSingleton<IOptions<ClientPortalEmployeeActionOptions>>(OptionsFactory.Create(new ClientPortalEmployeeActionOptions{Enabled=true}));
            services.AddSingleton<ILogger<ClientPortalEmployeeActionTools>>(log);
            services.AddMcpServer().WithHttpTransport(o=>o.Stateless=true).WithTools<ClientPortalEmployeeActionTools>();
        }).Configure(app=>{app.UseRouting();app.UseEndpoints(e=>e.MapMcp("/mcp"));}));
        using var client=server.CreateClient();client.DefaultRequestHeaders.Accept.ParseAdd("application/json");client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");client.DefaultRequestHeaders.Add("MCP-Protocol-Version","2025-06-18");
        using var response=await client.PostAsync("/mcp",new StringContent(JsonSerializer.Serialize(new {jsonrpc="2.0",id=1,method="tools/call",@params=new {name="client_portal_update_contract_draft",arguments=new {requestId=Guid.NewGuid(),contractId=42,name="owned metadata"}}}),System.Text.Encoding.UTF8,"application/json"));
        var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,text);Assert.Contains("No successful change is confirmed",text);
        Assert.Single(log.Entries);Assert.Equal(1222,log.Entries[0].Fields["SqlErrorNumber"]);
    }

    private static ClientPortalEmployeeActionTools Tools(Exception failure,CaptureLog log)=>
        new(Service(failure),OptionsFactory.Create(new ClientPortalEmployeeActionOptions{Enabled=true}),log);
    private static ClientPortalEmployeeActionService Service(Exception failure)=>
        new(new ClientPortalEmployeeContext(new NoPrincipal(),OptionsFactory.Create(new ClientPortalEmployeeOptions())),new FailingDatabase(failure),new ClientPortalNativeActionExecutor());
    private sealed class NoPrincipal:IEmployeePrincipalAccessor{public ClaimsPrincipal? GetPrincipal()=>null;}
    private sealed class FailingDatabase(Exception failure):IEmployeeActionDatabase
    {
        public Task<T> InTransactionAsync<T>(Func<SqlConnection,SqlTransaction,Task<T>> operation,CancellationToken ct)=>Task.FromException<T>(failure);
    }
    private sealed class CaptureLog:ILogger<ClientPortalEmployeeActionTools>
    {
        public bool ThrowOnWrite;public List<(EventId Id,Exception? Exception,Dictionary<string,object?> Fields)> Entries=[];
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {if(ThrowOnWrite)throw new InvalidOperationException("sink unavailable");Entries.Add((id,exception,((IEnumerable<KeyValuePair<string,object?>>)state!).ToDictionary(x=>x.Key,x=>x.Value)));}
    }
    private static SqlException SqlFailure(int number)
    {
        // The pinned SqlClient has no public exception factory. Keep reflection confined to test construction.
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var constructor=typeof(SqlError).GetConstructors(flags).OrderBy(c=>c.GetParameters().Length).First();
        var args=constructor.GetParameters().Select((p,i)=>i==0?(object)number:p.ParameterType==typeof(byte)?(byte)1:p.ParameterType==typeof(string)?"secret-database-marker":p.ParameterType.IsValueType?Activator.CreateInstance(p.ParameterType):null).ToArray();
        var error=(SqlError)constructor.Invoke(args);var errors=(SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection),true)!;
        typeof(SqlErrorCollection).GetMethod("Add",flags)!.Invoke(errors,[error]);
        var factory=typeof(SqlException).GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Single(m=>m.Name=="CreateException"&&m.GetParameters().Length==2);
        return (SqlException)factory.Invoke(null,[errors,"16.0"])!;
    }
}
