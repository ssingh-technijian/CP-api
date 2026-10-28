namespace CpApis.Api.Options;

/// <summary>
/// CardPointe (CardConnect) Gateway REST configuration, bound from the "CardPointe" section.
/// Site is the CardPointe-assigned host without scheme (production: fts.cardconnect.com,
/// UAT: fts-uat.cardconnect.com). Credentials must come from environment variables
/// (CardPointe__Username / CardPointe__Password) or appsettings.Local.json, never from a
/// committed file.
/// </summary>
public sealed class CardPointeOptions
{
    public const string SectionName = "CardPointe";

    public string Site { get; init; } = string.Empty;
    public string MerchantId { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    public bool IsConfigured =>
        string.IsNullOrWhiteSpace(Site) is false
        && string.IsNullOrWhiteSpace(MerchantId) is false
        && string.IsNullOrWhiteSpace(Username) is false
        && string.IsNullOrWhiteSpace(Password) is false;
}
