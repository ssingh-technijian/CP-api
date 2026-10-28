using System.Text.Json;
using System.Text.RegularExpressions;
using CpApis.Shared.Inventory;
using CpApis.Shared.Options;
using CpApis.Shared.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CpApis.Shared.Services;

public sealed class ManifestCatalogService
{
    /// <summary>
    /// Objects whose <c>Name</c> matches any of these are removed from the catalog when the manifest is loaded,
    /// so they can never be listed, described, routed, or executed through REST or MCP. Configuration may add
    /// patterns (<see cref="RuntimeCatalogOptions.AdditionalDeniedNamePatterns"/>) but never remove these.
    /// <c>Cred(?!it)</c> covers Cred / Creds / Credential(s) / QBCred / EWS_Cred without matching "Credit".
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInDeniedNamePatterns =
    [
        "Cred(?!it)",
        "ApiKey",
        "Secret"
    ];

    private readonly Dictionary<string, DatabaseObjectManifest> _objectsByKey;
    private readonly DatabaseInventoryManifest _manifest;
    private readonly IReadOnlyList<string> _deniedObjectKeys;

    public ManifestCatalogService(
        IHostEnvironment environment,
        IOptions<RuntimeCatalogOptions> options,
        ILogger<ManifestCatalogService> logger)
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
        var raw = JsonSerializer.Deserialize<DatabaseInventoryManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Unable to deserialize inventory manifest '{manifestPath}'.");

        (_manifest, _deniedObjectKeys) = ApplyDenyList(raw, options.Value.AdditionalDeniedNamePatterns);
        _objectsByKey = _manifest.Objects.ToDictionary(item => item.Key, SqlSupport.NameComparer);

        logger.LogInformation(
            "Catalog loaded from {ManifestPath}: {ExposedCount} objects exposed, {DeniedCount} denied by name pattern: {DeniedKeys}",
            manifestPath,
            _manifest.Objects.Count,
            _deniedObjectKeys.Count,
            string.Join(", ", _deniedObjectKeys));
    }

    /// <summary>Keys of manifest objects removed by the deny list. Surfaced on /api/system/health for monitoring.</summary>
    public IReadOnlyList<string> DeniedObjectKeys => _deniedObjectKeys;

    /// <summary>
    /// Pure filter: returns a copy of <paramref name="raw"/> without objects whose Name matches the built-in or
    /// additional deny patterns, plus the keys that were removed. Throws <see cref="ArgumentException"/> on an
    /// invalid pattern so a bad configuration fails closed at startup instead of silently exposing everything.
    /// </summary>
    public static (DatabaseInventoryManifest Exposed, IReadOnlyList<string> DeniedKeys) ApplyDenyList(
        DatabaseInventoryManifest raw,
        IEnumerable<string>? additionalPatterns)
    {
        var deny = BuildDenyRegex(additionalPatterns);
        var exposed = new List<DatabaseObjectManifest>(raw.Objects.Count);
        var denied = new List<string>();

        foreach (var item in raw.Objects)
        {
            if (deny.IsMatch(item.Name))
            {
                denied.Add(item.Key);
            }
            else
            {
                exposed.Add(item);
            }
        }

        var manifest = new DatabaseInventoryManifest
        {
            GeneratedAtUtc = raw.GeneratedAtUtc,
            SourceRoot = raw.SourceRoot,
            Databases = raw.Databases,
            Objects = exposed
        };

        return (manifest, denied);
    }

    private static Regex BuildDenyRegex(IEnumerable<string>? additionalPatterns)
    {
        var patterns = BuiltInDeniedNamePatterns
            .Concat(additionalPatterns ?? [])
            .Where(pattern => string.IsNullOrWhiteSpace(pattern) is false)
            .ToList();

        foreach (var pattern in patterns)
        {
            try
            {
                _ = new Regex(pattern);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid RuntimeCatalog denied-name pattern '{pattern}': {ex.Message}", ex);
            }
        }

        var combined = string.Join("|", patterns.Select(pattern => $"(?:{pattern})"));
        return new Regex(combined, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
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
