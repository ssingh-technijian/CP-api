using System.Text.Json;
using CpApis.Shared.Inventory;
using CpApis.Shared.Options;
using CpApis.Shared.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CpApis.Shared.Services;

public sealed class ManifestCatalogService
{
    private readonly Dictionary<string, DatabaseObjectManifest> _objectsByKey;
    private readonly DatabaseInventoryManifest _manifest;

    public ManifestCatalogService(
        IHostEnvironment environment,
        IOptions<RuntimeCatalogOptions> options)
    {
        var manifestPath = options.Value.ManifestPath;
        if (Path.IsPathRooted(manifestPath) is false)
        {
            manifestPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, manifestPath));
        }

        if (File.Exists(manifestPath) is false)
        {
            throw new FileNotFoundException($"Inventory manifest not found at '{manifestPath}'. Run CpApis.InventoryGenerator first.");
        }

        using var stream = File.OpenRead(manifestPath);
        _manifest = JsonSerializer.Deserialize<DatabaseInventoryManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Unable to deserialize inventory manifest '{manifestPath}'.");

        _objectsByKey = _manifest.Objects.ToDictionary(item => item.Key, SqlSupport.NameComparer);
    }

    public DatabaseInventoryManifest GetManifest() => _manifest;

    public IReadOnlyList<DatabaseTargetManifest> GetDatabaseTargets() => _manifest.Databases;

    public IReadOnlyList<DatabaseObjectManifest> GetObjects(string? databaseAlias = null, string? kind = null)
    {
        IEnumerable<DatabaseObjectManifest> query = _manifest.Objects.Where(item => item.IsIncluded);

        if (string.IsNullOrWhiteSpace(databaseAlias) is false)
        {
            query = query.Where(item => SqlSupport.NameComparer.Equals(item.DatabaseAlias, databaseAlias));
        }

        if (string.IsNullOrWhiteSpace(kind) is false)
        {
            query = query.Where(item => SqlSupport.NameComparer.Equals(item.Kind, kind));
        }

        return query
            .OrderBy(item => item.DatabaseAlias)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Schema)
            .ThenBy(item => item.Name)
            .ToList();
    }

    public DatabaseObjectManifest GetRequiredObject(string databaseAlias, string kind, string schema, string name)
    {
        var key = SqlSupport.NormalizeCompositeKey(databaseAlias, schema, name, kind);
        if (_objectsByKey.TryGetValue(key, out var item))
        {
            return item;
        }

        throw new KeyNotFoundException($"Object '{databaseAlias}:{schema}.{name}' with kind '{kind}' is not present in the manifest.");
    }
}
