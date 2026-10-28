using System.Reflection;
using System.Security.Claims;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class EmployeeToolIdentityMatrixTests
{
    private const string Client="95271648-21aa-436b-a088-7de56a6f0955",Resource="1a1c6254-cc0a-4628-b712-75601ff166d4";
    private static readonly Type[] Types=[typeof(ClientPortalEmployeeReadTools),typeof(ClientPortalEmployeeDetailTools),typeof(ClientPortalEmployeeActionTools)];
    public static IEnumerable<object[]> Cases()=>
        from type in Types from method in type.GetMethods().Where(m=>m.IsDefined(typeof(McpServerToolAttribute)))
        from failure in new[]{"unauthenticated","disabled","tenant","issuer","audience","client","scope","application","guest","missing-object","empty-object"}
        select new object[]{method.GetCustomAttribute<McpServerToolAttribute>()!.Name!,failure};

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryToolRejectsInvalidIdentityWithoutReturningData(string toolName,string failure)
    {
        var claims=new List<Claim>{new("iss",$"https://login.microsoftonline.com/{ClientPortalEmployeeContext.MicrosoftTenant}/v2.0"),
            new("tid",ClientPortalEmployeeContext.MicrosoftTenant),new("oid","e8bcfe4e-35f2-4e17-843a-0867462a16d6"),
            new("aud",Resource),new("azp",Client),new("scp",ClientPortalEmployeeContext.EmployeeScope),new("acct","0")};
        void Replace(string key,string? value){claims.RemoveAll(c=>c.Type==key);if(value is not null)claims.Add(new(key,value));}
        switch(failure)
        {
            case "tenant":Replace("tid",Guid.NewGuid().ToString());break;
            case "issuer":Replace("iss","https://example.invalid/v2.0");break;
            case "audience":Replace("aud",Guid.NewGuid().ToString());break;
            case "client":Replace("azp",Guid.NewGuid().ToString());break;
            case "scope":Replace("scp","User.Read");break;
            case "application":Replace("scp",null);claims.Add(new("roles",ClientPortalEmployeeContext.EmployeeScope));break;
            case "guest":Replace("acct","1");break;
            case "missing-object":Replace("oid",null);break;
            case "empty-object":Replace("oid",Guid.Empty.ToString());break;
        }
        var user=failure=="unauthenticated"?null:new ClaimsPrincipal(new ClaimsIdentity(claims,"fixture"));
        var context=new ClientPortalEmployeeContext(new Principal(user),Microsoft.Extensions.Options.Options.Create(new ClientPortalEmployeeOptions
            {Enabled=failure!="disabled",ResourceClientId=Resource,TrustedClientIds=[Client]}));
        Assert.Throws<EmployeeAccessDeniedException>(()=>context.GetIdentity());
        var database=new BoundaryDatabase();var executor=new UnreachableExecutor();
        var objects=new object[]{new ClientPortalEmployeeReadTools(context,new ConfigurationBuilder().Build()),
            new ClientPortalEmployeeDetailTools(context,database),
            new ClientPortalEmployeeActionTools(new ClientPortalEmployeeActionService(context,database,executor),Microsoft.Extensions.Options.Options.Create(new ClientPortalEmployeeActionOptions{Enabled=true}))};
        var target=objects.Single(o=>o.GetType().GetMethods().Any(m=>m.GetCustomAttribute<McpServerToolAttribute>()?.Name==toolName));
        var method=target.GetType().GetMethods().Single(m=>m.GetCustomAttribute<McpServerToolAttribute>()?.Name==toolName);
        var parameters=method.GetParameters().Select(p=>p.ParameterType==typeof(CancellationToken)?(object)default(CancellationToken):
            p.Name is "fromDate" or "throughDate"?"2026-10-26":p.HasDefaultValue?p.DefaultValue:
            p.ParameterType==typeof(Guid)?Guid.NewGuid():p.ParameterType==typeof(int)?1:p.ParameterType==typeof(string)?"Owned test":throw new Exception("Unmapped test parameter")).ToArray();
        var call=(Task)method.Invoke(target,parameters)!;await call;
        var result=call.GetType().GetProperty("Result")!.GetValue(call)!;var resultType=result.GetType();
        Assert.False((bool)resultType.GetProperty("Success")!.GetValue(result)!);
        Assert.Null(resultType.GetProperty("Data")!.GetValue(result));
        Assert.False(string.IsNullOrWhiteSpace((string?)resultType.GetProperty("Error")!.GetValue(result)));
        Assert.Equal(0,executor.Calls);
    }

    private sealed class Principal(ClaimsPrincipal? user):IEmployeePrincipalAccessor{public ClaimsPrincipal? GetPrincipal()=>user;}
    private sealed class BoundaryDatabase:IEmployeeActionDatabase
    {
        public async Task<T> InTransactionAsync<T>(Func<SqlConnection,SqlTransaction,Task<T>> action,CancellationToken ct)
        {await using var c=new SqlConnection();return await action(c,null!);}
    }
    private sealed class UnreachableExecutor:IEmployeeActionExecutor
    {
        public int Calls;
        public Task<EmployeeActionPlan> PrepareAsync(string operation,System.Text.Json.JsonElement payload,SqlConnection connection,SqlTransaction transaction,PortalEmployee actor,CancellationToken ct)
        {Calls++;throw new Exception("Untrusted identity reached native prepare");}
        public Task<EmployeeActionResult> ApplyAsync(string operation,System.Text.Json.JsonElement payload,SqlConnection connection,SqlTransaction transaction,PortalEmployee actor,CancellationToken ct)
        {Calls++;throw new Exception("Untrusted identity reached native apply");}
    }
}
