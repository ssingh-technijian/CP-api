using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CpApis.Mcp.Http.Services;

public static class EmployeeActionEndpoints
{
    public static IEndpointRouteBuilder MapEmployeeActions(this IEndpointRouteBuilder routes)
    {
        var group=routes.MapGroup("/employee-actions").RequireAuthorization();
        group.MapGet("/{actionId:guid}/review",async(Guid actionId,HttpContext context,
            ClientPortalEmployeeActionService actions,IOptions<ClientPortalEmployeeActionOptions> options,CancellationToken ct)=>
            await Safe(context,options,async()=>await actions.GetReviewAsync(actionId,ct)));
        group.MapPost("/{actionId:guid}/approve",async(Guid actionId,EmployeeActionDecision decision,HttpContext context,
            ClientPortalEmployeeActionService actions,IOptions<ClientPortalEmployeeActionOptions> options,CancellationToken ct)=>
            await Safe(context,options,async()=>await actions.ApproveAsync(actionId,decision,ct)));
        group.MapPost("/{actionId:guid}/reject",async(Guid actionId,EmployeeActionDecision decision,HttpContext context,
            ClientPortalEmployeeActionService actions,IOptions<ClientPortalEmployeeActionOptions> options,CancellationToken ct)=>
            await Safe(context,options,async()=>await actions.RejectAsync(actionId,decision,ct)));
        return routes;
    }

    private static async Task<IResult> Safe(HttpContext context,IOptions<ClientPortalEmployeeActionOptions> options,Func<Task<object>> call)
    {
        context.Response.Headers.CacheControl="no-store";context.Response.Headers.Pragma="no-cache";
        context.Response.Headers["Referrer-Policy"]="no-referrer";
        if(!options.Value.Enabled)return Results.Problem(statusCode:503,title:"Employee action review is not enabled.");
        try { return Results.Ok(await call()); }
        catch(EmployeeAccessDeniedException){return Results.Problem(statusCode:403,title:"Your sign-in or native Portal permission does not allow this action.");}
        catch(EmployeeActionException ex){return Results.Problem(statusCode:ex.StatusCode,title:ex.Message);}
        catch(SqlException){return Results.Problem(statusCode:503,title:"Native action verification failed. No successful change is confirmed.");}
    }
}
