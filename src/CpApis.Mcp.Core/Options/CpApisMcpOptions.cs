namespace CpApis.Mcp.Core.Options;

public sealed class CpApisMcpOptions
{
    public string ApiBaseUrl { get; init; } = "http://127.0.0.1:5180";
    public int DefaultListLimit { get; init; } = 100;
    public int MaxListLimit { get; init; } = 500;
    public bool RequireWriteConfirmation { get; init; } = true;
}
