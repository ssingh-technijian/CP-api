namespace CpApis.Mcp.Http.Options;

public sealed class CpApisMcpHttpOptions
{
    public string Path { get; init; } = "/mcp";
    public bool Stateless { get; init; }
}
