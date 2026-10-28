using System.Collections.Concurrent;
using System.Security.Claims;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Http.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CpApis.Mcp.Http.Services;

public sealed class HttpEmployeePrincipalAccessor(IHttpContextAccessor accessor) : IEmployeePrincipalAccessor
{
    public ClaimsPrincipal? GetPrincipal() => accessor.HttpContext?.User;
}

/// <summary>Separates employee sessions and curated tools from the legacy machine connector.</summary>
public sealed class ClientPortalEmployeeBoundary(
    RequestDelegate next, IOptions<ClientPortalEmployeeOptions> options, IOptions<CpApisMcpHttpOptions> httpOptions)
{
    private sealed record SessionOwner(string Actor, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, SessionOwner> _sessions = new(StringComparer.Ordinal);
    private readonly PathString _path = new("/" + (httpOptions.Value.Path ?? "mcp").Trim('/'));

    public static bool IsEmployeeTool(string? name) => name is
        "client_portal_whoami" or "client_portal_list_tickets" or "client_portal_list_time_entries"
        or "client_portal_list_invoices" or "client_portal_list_contracts" or "client_portal_billing_hours"
        or "client_portal_create_ticket_draft" or "client_portal_update_ticket_draft" or "client_portal_delete_ticket_draft"
        or "client_portal_create_time_entry_draft" or "client_portal_update_time_entry_draft" or "client_portal_delete_time_entry_draft"
        or "client_portal_create_invoice_draft" or "client_portal_update_invoice_draft" or "client_portal_delete_invoice_draft"
        or "client_portal_create_contract_draft" or "client_portal_update_contract_draft" or "client_portal_delete_contract_draft"
        or "client_portal_get_action_status" or "client_portal_find_clients" or "client_portal_lookup_options"
        or "client_portal_get_ticket" or "client_portal_get_time_entry" or "client_portal_get_invoice" or "client_portal_get_contract"
        or "client_portal_find_employees" or "client_portal_ticket_activity" or "client_portal_get_eod" or "client_portal_get_ticket_history";

    public static bool IsDelegated(ClaimsPrincipal? user) => user?.HasClaim(c => c.Type == "scp") == true;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(_path)) { await next(context); return; }
        var sessionHeaders = context.Request.Headers["Mcp-Session-Id"];
        if (sessionHeaders.Count > 1) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var session = sessionHeaders.FirstOrDefault();
        if (!IsDelegated(context.User))
        {
            var employeeClient = context.User.Claims.Where(c => c.Type is "azp" or "appid")
                .Any(c => Guid.TryParse(c.Value, out var caller) && options.Value.TrustedClientIds
                    .Any(id => Guid.TryParse(id, out var trusted) && caller == trusted));
            var employeeResource = Guid.TryParse(options.Value.ResourceClientId, out var resource)
                && context.User.FindAll("aud").Any(c => c.Value == resource.ToString("D") || c.Value == $"api://{resource:D}");
            if (employeeClient || employeeResource || session is not null && _sessions.ContainsKey(session))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            await next(context); return;
        }
        MicrosoftEmployeeIdentity employee;
        try { employee = ClientPortalEmployeeContext.ValidateIdentity(context.User, options.Value); }
        catch (EmployeeAccessDeniedException) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }

        var actor = $"cp-employee:{ClientPortalEmployeeContext.MicrosoftTenant}:{employee.ObjectId:D}:{employee.ClientId:D}";
        if (session is not null && (!_sessions.TryGetValue(session, out var owner)
            || owner.Expires <= DateTimeOffset.UtcNow || owner.Actor != actor))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }

        // Supply a stable, server-derived user identifier to SDK authorization as well.
        var principal = context.User.Clone();
        principal.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor)]));
        context.User = principal;

        if (session is null)
        {
            foreach (var item in _sessions.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow)) _sessions.TryRemove(item.Key, out _);
            if (_sessions.Count >= 10_000) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            context.Response.OnStarting(() =>
            {
                var created = context.Response.Headers["Mcp-Session-Id"];
                if (context.Response.StatusCode is >= 200 and < 300 && created.Count == 1 && !string.IsNullOrEmpty(created[0]))
                {
                    if (!_sessions.TryAdd(created[0]!, new(actor, DateTimeOffset.UtcNow.AddHours(1))))
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
                return Task.CompletedTask;
            });
        }
        await next(context);
        if (session is not null && HttpMethods.IsDelete(context.Request.Method) && context.Response.StatusCode is >= 200 and < 300)
            _sessions.TryRemove(session, out _);
    }

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> FilterList(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) => async (request, ct) =>
    {
        var result = await next(request, ct);
        var employee = IsDelegated(request.User);
        result.Tools = result.Tools.Where(tool => IsEmployeeTool(tool.Name) == employee).ToArray();
        return result;
    };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCall(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => (request, ct) =>
    {
        if (IsEmployeeTool(request.Params?.Name) != IsDelegated(request.User))
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "This sign-in cannot call this tool. Employee access uses only reviewed business tools; arbitrary procedures, payments and approval are unavailable through MCP." }]
            });
        return next(request, ct);
    };
}
