using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;
using Microsoft.Data.SqlClient;

namespace CpApis.InventoryGenerator;

internal sealed partial class InventoryBuilder
{
    private static readonly Regex ConstRegex = new(@"(?m)(?:public|private|internal|protected)?\s*(?:const|static\s+readonly)\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]+)""\s*;", RegexOptions.Compiled);
    private static readonly Regex ClassRegex = new(@"class\s+(?<name>\w+)", RegexOptions.Compiled);
    private static readonly Regex CommandExpressionRegex = new(@"(?<kind>ExecuteCommandSP|ExecuteNonQuery|ExecuteScalarSP|ExecuteStoredProcedure(?:NonQuery|WithXmlOutput)?|new\s+SqlCommand|ExecuteCommandText)\(\s*(?<expr>[^,\r\n\)]+)", RegexOptions.Compiled);
    private static readonly Regex ViewTokenRegex = new(@"\b(vw_[A-Za-z0-9_]+)\b", RegexOptions.Compiled);
    private static readonly Regex SqlSourceRegex = new(@"\b(?:FROM|JOIN)\s+(?<name>(?:\[[^\]]+\]|\w+)(?:\.(?:\[[^\]]+\]|\w+)){0,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FunctionRegex = new(@"\b(?<name>(?:fn_|tf_|if_)\w+)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExecRegex = new(@"\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?(?<name>(?:\[[^\]]+\]|\w+)(?:\.(?:\[[^\]]+\]|\w+)){0,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _outputDirectory;
    private readonly string _repoRoot;
    private readonly string? _clientPortalConnectionStringOverride;
    private readonly string? _teamsConnectionStringOverride;
    private readonly string? _clientPortalTeamsConnectionStringOverride;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public InventoryBuilder(
        string repoRoot,
        string outputDirectory,
        string? clientPortalConnectionStringOverride = null,
        string? teamsConnectionStringOverride = null,
        string? clientPortalTeamsConnectionStringOverride = null)
    {
        _repoRoot = repoRoot;
        _outputDirectory = outputDirectory;
        _clientPortalConnectionStringOverride = NormalizeConfiguredConnectionString(
            string.IsNullOrWhiteSpace(clientPortalConnectionStringOverride)
                ? Environment.GetEnvironmentVariable("ConnectionStrings__client-portal")
                : clientPortalConnectionStringOverride);
        _teamsConnectionStringOverride = NormalizeConfiguredConnectionString(
            string.IsNullOrWhiteSpace(teamsConnectionStringOverride)
                ? Environment.GetEnvironmentVariable("ConnectionStrings__teams")
                : teamsConnectionStringOverride);
        _clientPortalTeamsConnectionStringOverride = NormalizeConfiguredConnectionString(
            string.IsNullOrWhiteSpace(clientPortalTeamsConnectionStringOverride)
                ? Environment.GetEnvironmentVariable("ConnectionStrings__client-portal-teams")
                : clientPortalTeamsConnectionStringOverride);
    }

    public async Task RunAsync()
    {
        var databaseTargets = LoadDatabaseTargets();
        var catalogs = new List<DatabaseCatalog>();
        foreach (var target in databaseTargets)
        {
            catalogs.Add(await LoadCatalogAsync(target));
        }

        var sourceFiles = EnumerateSourceFiles().ToList();
        var constMap = BuildConstMap(sourceFiles);
        var references = ScanDirectReferences(sourceFiles, constMap, catalogs);
        references.AddRange(ScanViewTokenReferences(sourceFiles, catalogs));

        var activeByObject = references
            .GroupBy(reference => reference.ObjectKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .DistinctBy(reference => $"{reference.FilePath}:{reference.LineNumber}:{reference.MatchKind}", StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var trailsByObject = BuildDependencyTrails(activeByObject.Keys, catalogs);
        var includedKeys = trailsByObject.Keys
            .Concat(activeByObject.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var manifestObjects = catalogs
            .SelectMany(catalog => catalog.Objects.Values)
            .Where(item => includedKeys.Contains(item.Key))
            .OrderBy(item => item.DatabaseAlias)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Schema)
            .ThenBy(item => item.Name)
            .Select(item => new DatabaseObjectManifest
            {
                Key = item.Key,
                Module = InferModule(item, activeByObject),
                DatabaseAlias = item.DatabaseAlias,
                DatabaseName = item.DatabaseName,
                Schema = item.Schema,
                Name = item.Name,
                Kind = item.Kind,
                TypeCode = item.TypeCode,
                TypeDescription = item.TypeDescription,
                IsActiveRoot = activeByObject.ContainsKey(item.Key),
                IsIncluded = true,
                IsWriteOperation = InferWriteOperation(item.Name),
                DefinitionHash = SqlSupport.ComputeSha256(item.Definition),
                Parameters = item.Parameters
                    .OrderBy(parameter => parameter.ParameterId)
                    .Select(parameter => new ParameterManifest
                    {
                        ParameterId = parameter.ParameterId,
                        Name = parameter.Name,
                        SqlType = parameter.SqlType,
                        MaxLength = parameter.MaxLength,
                        Precision = parameter.Precision,
                        Scale = parameter.Scale,
                        IsOutput = parameter.IsOutput,
                        IsNullable = parameter.IsNullable,
                        HasDefaultValue = parameter.HasDefaultValue
                    })
                    .ToList(),
                Columns = item.Columns
                    .OrderBy(column => column.Name)
                    .Select(column => new ColumnManifest
                    {
                        Name = column.Name,
                        SqlType = column.SqlType,
                        MaxLength = column.MaxLength,
                        Precision = column.Precision,
                        Scale = column.Scale,
                        IsNullable = column.IsNullable,
                        IsIdentity = column.IsIdentity
                    })
                    .ToList(),
                SourceReferences = activeByObject.TryGetValue(item.Key, out var refs)
                    ? refs.Select(reference => new SourceReferenceManifest
                    {
                        FilePath = reference.FilePath,
                        LineNumber = reference.LineNumber,
                        MatchKind = reference.MatchKind,
                        Snippet = reference.Snippet
                    }).ToList()
                    : [],
                Dependencies = item.Dependencies
                    .Where(dependency => includedKeys.Contains(dependency.ReferencedKey))
                    .OrderBy(dependency => dependency.ReferencedKey)
                    .Select(dependency => new ObjectDependencyManifest
                    {
                        ReferencedKey = dependency.ReferencedKey,
                        ReferencedSchema = dependency.ReferencedSchema,
                        ReferencedName = dependency.ReferencedName,
                        ReferencedKind = dependency.ReferencedKind,
                        DetectionKind = dependency.DetectionKind
                    })
                    .ToList(),
                DependencyTrails = trailsByObject.TryGetValue(item.Key, out var trails)
                    ? trails.OrderBy(value => value).ToList()
                    : []
            })
            .ToList();

        var manifest = new DatabaseInventoryManifest
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceRoot = _repoRoot,
            Databases = databaseTargets,
            Objects = manifestObjects
        };

        var manifestPath = Path.Combine(_outputDirectory, "database-object-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, _jsonOptions));

        var reportPath = Path.Combine(_outputDirectory, "database-object-report.md");
        await File.WriteAllTextAsync(reportPath, BuildMarkdownReport(manifest));

        Console.WriteLine($"Manifest written to: {manifestPath}");
        Console.WriteLine($"Report written to:   {reportPath}");
        Console.WriteLine($"Included objects:    {manifest.Objects.Count}");
        Console.WriteLine($"Active roots:        {manifest.Objects.Count(item => item.IsActiveRoot)}");
    }

    private IEnumerable<string> EnumerateSourceFiles()
    {
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".config",
            ".sql"
        };

        return Directory.EnumerateFiles(_repoRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => allowedExtensions.Contains(Path.GetExtension(path)))
            .Where(path => path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) is false)
            .Where(path => path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) is false)
            .Where(path => path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) is false)
            .Where(path => path.Contains(@"\.vs\", StringComparison.OrdinalIgnoreCase) is false)
            .Where(path => path.Contains(@"\Figma\", StringComparison.OrdinalIgnoreCase) is false)
            .Where(path => path.Contains(@"\ThirdParty\", StringComparison.OrdinalIgnoreCase) is false)
            .Where(path => path.Contains(@"\packages\", StringComparison.OrdinalIgnoreCase) is false)
            .Where(path => path.Contains(@"\CP-Apis.Work\", StringComparison.OrdinalIgnoreCase) is false);
    }

    private static Dictionary<string, string> BuildConstMap(IEnumerable<string> files)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryReadFileText(file, out var content) is false)
            {
                continue;
            }

            var className = ClassRegex.Match(content).Success ? ClassRegex.Match(content).Groups["name"].Value : string.Empty;
            foreach (Match match in ConstRegex.Matches(content))
            {
                var name = match.Groups["name"].Value;
                var value = match.Groups["value"].Value;
                values[name] = value;
                if (string.IsNullOrWhiteSpace(className) is false)
                {
                    values[$"{className}.{name}"] = value;
                }
            }
        }

        return values;
    }

    private static List<SourceReference> ScanDirectReferences(
        IEnumerable<string> files,
        IReadOnlyDictionary<string, string> constMap,
        IReadOnlyList<DatabaseCatalog> catalogs)
    {
        var references = new List<SourceReference>();

        foreach (var file in files.Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryReadFileText(file, out var content) is false)
            {
                continue;
            }

            var preferredAlias = DeterminePreferredAlias(file, content);

            foreach (Match match in CommandExpressionRegex.Matches(content))
            {
                var expression = match.Groups["expr"].Value.Trim();
                var resolved = ResolveExpression(expression, constMap);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    continue;
                }

                var lineNumber = GetLineNumber(content, match.Index);
                var snippet = GetLine(content, lineNumber);
                var kind = match.Groups["kind"].Value;

                if (kind.Equals("ExecuteCommandText", StringComparison.OrdinalIgnoreCase) || LooksLikeSqlText(resolved))
                {
                    foreach (var objectRef in MatchSqlObjects(resolved, preferredAlias, catalogs))
                    {
                        references.Add(new SourceReference(objectRef.Key, file, lineNumber, "sql-text", snippet));
                    }
                }
                else
                {
                    var objectRef = ResolveObjectReference(resolved, preferredAlias, catalogs, "stored-procedure");
                    if (objectRef is not null)
                    {
                        references.Add(new SourceReference(objectRef.Key, file, lineNumber, "direct-call", snippet));
                    }
                }
            }
        }

        return references;
    }

    private static List<SourceReference> ScanViewTokenReferences(
        IEnumerable<string> files,
        IReadOnlyList<DatabaseCatalog> catalogs)
    {
        var references = new List<SourceReference>();
        foreach (var file in files.Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryReadFileText(file, out var content) is false)
            {
                continue;
            }

            var preferredAlias = DeterminePreferredAlias(file, content);
            foreach (Match match in ViewTokenRegex.Matches(content))
            {
                var token = match.Groups[1].Value;
                if (Path.GetFileNameWithoutExtension(file).Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var objectRef = ResolveObjectReference(token, preferredAlias, catalogs, "view");
                if (objectRef is null)
                {
                    continue;
                }

                var lineNumber = GetLineNumber(content, match.Index);
                references.Add(new SourceReference(objectRef.Key, file, lineNumber, "view-token", GetLine(content, lineNumber)));
            }
        }

        return references;
    }

    private static Dictionary<string, List<string>> BuildDependencyTrails(
        IEnumerable<string> activeKeys,
        IReadOnlyList<DatabaseCatalog> catalogs)
    {
        var objectLookup = catalogs.SelectMany(catalog => catalog.Objects.Values).ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var trails = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootKey in activeKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (objectLookup.TryGetValue(rootKey, out var root) is false)
            {
                continue;
            }

            var queue = new Queue<(string CurrentKey, string Trail)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootKey };

            queue.Enqueue((rootKey, Display(root)));
            trails.GetOrAdd(rootKey, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(Display(root));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (objectLookup.TryGetValue(current.CurrentKey, out var currentObject) is false)
                {
                    continue;
                }

                foreach (var dependency in currentObject.Dependencies)
                {
                    if (objectLookup.TryGetValue(dependency.ReferencedKey, out var dependencyObject) is false)
                    {
                        continue;
                    }

                    var nextTrail = $"{current.Trail} -> {Display(dependencyObject)}";
                    trails.GetOrAdd(dependencyObject.Key, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(nextTrail);

                    if (visited.Add(dependencyObject.Key))
                    {
                        queue.Enqueue((dependencyObject.Key, nextTrail));
                    }
                }
            }
        }

        return trails.ToDictionary(pair => pair.Key, pair => pair.Value.Take(10).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildMarkdownReport(DatabaseInventoryManifest manifest)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Database Object Discovery");
        builder.AppendLine();
        builder.AppendLine($"Generated (UTC): `{manifest.GeneratedAtUtc:O}`");
        builder.AppendLine($"Source root: `{manifest.SourceRoot}`");
        builder.AppendLine();
        builder.AppendLine("## Databases");
        foreach (var database in manifest.Databases)
        {
            builder.AppendLine($"- `{database.Alias}` -> `{database.Server}` / `{database.DatabaseName}`");
            builder.AppendLine($"  - source: `{database.ConnectionSourcePath}` (`{database.ConnectionName}`)");
        }

        builder.AppendLine();
        builder.AppendLine("## Included Objects");
        foreach (var group in manifest.Objects.GroupBy(item => item.DatabaseAlias).OrderBy(group => group.Key))
        {
            builder.AppendLine($"### {group.Key}");
            foreach (var kindGroup in group.GroupBy(item => item.Kind).OrderBy(kindGroup => kindGroup.Key))
            {
                builder.AppendLine($"#### {kindGroup.Key}");
                foreach (var item in kindGroup.OrderBy(item => item.Schema).ThenBy(item => item.Name))
                {
                    var marker = item.IsActiveRoot ? "active" : "dependent";
                    builder.AppendLine($"- [{marker}] `{item.Schema}.{item.Name}` module=`{item.Module}`");
                    foreach (var reference in item.SourceReferences.OrderBy(reference => reference.FilePath).ThenBy(reference => reference.LineNumber))
                    {
                        builder.AppendLine($"  - source: `{reference.FilePath}:{reference.LineNumber}` ({reference.MatchKind})");
                    }

                    foreach (var trail in item.DependencyTrails.Take(5))
                    {
                        builder.AppendLine($"  - trail: `{trail}`");
                    }
                }
            }
        }

        return builder.ToString();
    }

    private static string InferModule(DatabaseObjectBuilder item, IReadOnlyDictionary<string, List<SourceReference>> activeByObject)
    {
        if (activeByObject.TryGetValue(item.Key, out var refs))
        {
            var inferred = refs
                .Select(reference => InferModuleFromPath(reference.FilePath))
                .FirstOrDefault(value => string.IsNullOrWhiteSpace(value) is false);

            if (string.IsNullOrWhiteSpace(inferred) is false)
            {
                return inferred!;
            }
        }

        if (item.DatabaseAlias.Equals("teams", StringComparison.OrdinalIgnoreCase))
        {
            return "Teams";
        }

        return item.Schema;
    }

    private string GetEffectiveConnectionString(string alias, string databaseName)
    {
        return alias.ToLowerInvariant() switch
        {
            "client-portal" => GetClientPortalConnectionString(),
            "teams" => _teamsConnectionStringOverride
                       ?? new SqlConnectionStringBuilder(GetClientPortalConnectionString())
                       {
                           InitialCatalog = databaseName
                       }.ConnectionString,
            "client-portal-teams" => _clientPortalTeamsConnectionStringOverride
                                     ?? new SqlConnectionStringBuilder(GetClientPortalConnectionString())
                                     {
                                         InitialCatalog = databaseName
                                     }.ConnectionString,
            _ => GetClientPortalConnectionString()
        };
    }

    private string GetClientPortalConnectionString()
    {
        return _clientPortalConnectionStringOverride
               ?? ReadConnectionString(Path.Combine(_repoRoot, "TechPortal.UI", "Web.config"), "Client_Portal");
    }

    private static string? NormalizeConfiguredConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var original = new SqlConnectionStringBuilder(connectionString);
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            TrustServerCertificate = true,
            Pooling = false,
            ConnectTimeout = Math.Max(15, original.ConnectTimeout)
        };

        return builder.ConnectionString;
    }

    private static bool TryReadFileText(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (IOException)
        {
            content = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            content = string.Empty;
            return false;
        }
    }
}
