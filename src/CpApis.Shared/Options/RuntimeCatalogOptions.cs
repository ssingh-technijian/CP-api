namespace CpApis.Shared.Options;

public sealed class RuntimeCatalogOptions
{
    public string ManifestPath { get; init; } = @"..\..\artifacts\database-object-manifest.json";
    public int DefaultTop { get; init; } = 100;
    public int MaxTop { get; init; } = 500;
    public int CommandTimeoutSeconds { get; init; } = 180;

    /// <summary>
    /// Extra case-insensitive .NET regexes matched against each manifest object's <c>Name</c>.
    /// Matching objects are dropped from the catalog at load time (never listed, described, routed, or executed).
    /// The built-in credential patterns in <see cref="Services.ManifestCatalogService.BuiltInDeniedNamePatterns"/>
    /// always apply and cannot be removed through configuration.
    /// </summary>
    public string[] AdditionalDeniedNamePatterns { get; init; } = [];
}
