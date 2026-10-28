namespace CpApis.Mcp.Core.Services;

public interface IApiAuthorizationHeaderAccessor
{
    string? GetAuthorizationHeader();
}

public sealed class NullApiAuthorizationHeaderAccessor : IApiAuthorizationHeaderAccessor
{
    public string? GetAuthorizationHeader() => null;
}
