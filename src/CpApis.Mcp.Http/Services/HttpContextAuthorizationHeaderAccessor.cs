using CpApis.Mcp.Core.Services;
using Microsoft.AspNetCore.Http;

namespace CpApis.Mcp.Http.Services;

public sealed class HttpContextAuthorizationHeaderAccessor : IApiAuthorizationHeaderAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextAuthorizationHeaderAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? GetAuthorizationHeader() =>
        _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
}
