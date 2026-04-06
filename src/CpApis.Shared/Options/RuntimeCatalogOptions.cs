namespace CpApis.Shared.Options;

public sealed class RuntimeCatalogOptions
{
    public string ManifestPath { get; init; } = @"..\..\artifacts\database-object-manifest.json";
    public int DefaultTop { get; init; } = 100;
    public int MaxTop { get; init; } = 500;
    public int CommandTimeoutSeconds { get; init; } = 180;
}
