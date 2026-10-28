using Microsoft.Extensions.Options;

namespace CpApis.Api.Services;

public sealed class ClientPortalEmployeeApiOptions
{
    public bool Enabled { get; set; }
    public string[] TrustedClientIds { get; set; } = [];
}

/// <summary>
/// Employee OBO callers use the curated MCP service, which queries native SQL itself.
/// They cannot bypass those checks through this metadata-driven arbitrary-procedure API.
/// Existing machine and other explicitly integrated API callers retain their contracts.
/// </summary>
public sealed class ClientPortalEmployeeApiBoundary(RequestDelegate next, IOptions<ClientPortalEmployeeApiOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var policy = options.Value;
        // Turning employee access off must not grant this reserved client raw API access.
        // An empty trusted-client list preserves the pre-existing API behavior by default.
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.Claims.Where(c => c.Type is "azp" or "appid")
                .Any(c => Guid.TryParse(c.Value, out var caller) && policy.TrustedClientIds
                    .Any(id => Guid.TryParse(id, out var trusted) && caller == trusted)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }
}
