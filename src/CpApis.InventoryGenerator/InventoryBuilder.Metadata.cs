using System.Xml.Linq;
using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace CpApis.InventoryGenerator;

internal sealed partial class InventoryBuilder
{
    private List<DatabaseTargetManifest> LoadDatabaseTargets()
    {
        var targets = new List<DatabaseTargetManifest>();

        var portalConfigPath = Path.Combine(_repoRoot, "TechPortal.UI", "Web.config");
        var portalConnection = GetEffectiveConnectionString("client-portal", "CP_Production_New");
        targets.Add(BuildTarget("client-portal", "Client Portal", portalConnection, portalConfigPath, "Client_Portal"));

        var teamsConfigPath = Path.Combine(_repoRoot, "APIAccess", "Web.config");
        var teamsConnection = GetEffectiveConnectionString("teams", "MicrosoftTeams");
        targets.Add(BuildTarget("teams", "Microsoft Teams", teamsConnection, teamsConfigPath, "Teams", isDerived: true));

        var clientPortalTeamsConnection = GetEffectiveConnectionString("client-portal-teams", "ClientPortal_Teams");
        targets.Add(BuildTarget("client-portal-teams", "Client Portal Teams", clientPortalTeamsConnection, portalConfigPath, "ClientPortal_Teams", isDerived: true));

        return targets;
    }

    private static DatabaseTargetManifest BuildTarget(
        string alias,
        string displayName,
        string connectionString,
        string sourcePath,
        string connectionName,
        bool isDerived = false)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return new DatabaseTargetManifest
        {
            Alias = alias,
            DisplayName = displayName,
            DatabaseName = builder.InitialCatalog,
            Server = builder.DataSource,
            ConnectionName = connectionName,
            ConnectionSourcePath = sourcePath,
            IsDerivedTarget = isDerived
        };
    }

    private static string ReadConnectionString(string configPath, string name)
    {
        var document = XDocument.Load(configPath);
        var element = document
            .Descendants("add")
            .FirstOrDefault(node =>
                string.Equals((string?)node.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));

        var rawConnectionString = element?.Attribute("connectionString")?.Value
                                  ?? throw new InvalidOperationException($"Connection string '{name}' was not found in '{configPath}'.");

        return NormalizeConfiguredConnectionString(rawConnectionString)
               ?? throw new InvalidOperationException($"Connection string '{name}' in '{configPath}' is empty.");
    }

    private async Task<DatabaseCatalog> LoadCatalogAsync(DatabaseTargetManifest target)
    {
        var connectionString = GetEffectiveConnectionString(target.Alias, target.DatabaseName);

        var catalog = new DatabaseCatalog(target);

        await using var connection = await OpenConnectionWithRetryAsync(connectionString, target.Alias);

        await using (var command = new SqlCommand(
                         """
                         SELECT o.object_id,
                                s.name AS schema_name,
                                o.name,
                                o.type,
                                o.type_desc,
                                CONVERT(nvarchar(max), m.definition) AS definition,
                                p.parameter_id,
                                p.name AS parameter_name,
                                COALESCE(base_parameter_type.name, parameter_type.name, N'sql_variant') AS parameter_type,
                                p.max_length,
                                p.precision,
                                p.scale,
                                p.is_output,
                                p.is_nullable,
                                p.has_default_value
                         FROM sys.objects o
                         JOIN sys.schemas s ON s.schema_id = o.schema_id
                         LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
                         LEFT JOIN sys.parameters p ON p.object_id = o.object_id
                         LEFT JOIN sys.types parameter_type ON parameter_type.user_type_id = p.user_type_id
                         LEFT JOIN sys.types base_parameter_type
                           ON base_parameter_type.user_type_id = p.system_type_id
                          AND base_parameter_type.user_type_id = base_parameter_type.system_type_id
                         WHERE o.type IN ('P','V','FN','TF','IF')
                         ORDER BY o.object_id, p.parameter_id;
                         """,
                         connection))
               await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var objectId = reader.GetInt32(0);
                if (catalog.ObjectsById.TryGetValue(objectId, out var item) is false)
                {
                    item = new DatabaseObjectBuilder
                    {
                        DatabaseAlias = target.Alias,
                        DatabaseName = target.DatabaseName,
                        Schema = reader.GetString(1),
                        Name = reader.GetString(2),
                        TypeCode = reader.GetString(3),
                        TypeDescription = reader.GetString(4),
                        Definition = reader.IsDBNull(5) ? null : reader.GetString(5)
                    };

                    catalog.ObjectsById[objectId] = item;
                    catalog.Objects[item.Key] = item;
                    catalog.ObjectsByNormalizedName.GetOrAdd(item.NormalizedName, _ => []).Add(item);
                }

                if (reader.IsDBNull(6) is false && reader.IsDBNull(7) is false)
                {
                    item.Parameters.Add(new ParameterBuilder
                    {
                        ParameterId = reader.GetInt32(6),
                        Name = reader.GetString(7),
                        SqlType = reader.IsDBNull(8) ? "sql_variant" : reader.GetString(8),
                        MaxLength = reader.IsDBNull(9) ? (short)0 : reader.GetInt16(9),
                        Precision = reader.IsDBNull(10) ? (byte)0 : reader.GetByte(10),
                        Scale = reader.IsDBNull(11) ? (byte)0 : reader.GetByte(11),
                        IsOutput = reader.IsDBNull(12) is false && reader.GetBoolean(12),
                        IsNullable = reader.IsDBNull(13) is false && reader.GetBoolean(13),
                        HasDefaultValue = reader.IsDBNull(14) is false && reader.GetBoolean(14)
                    });
                }
            }
        }

        await using (var command = new SqlCommand(
                         """
                         SELECT o.object_id,
                                c.name,
                                COALESCE(base_column_type.name, column_type.name, N'sql_variant') AS column_type,
                                c.max_length,
                                c.precision,
                                c.scale,
                                c.is_nullable,
                                COLUMNPROPERTY(c.object_id, c.name, 'IsIdentity') AS is_identity
                         FROM sys.objects o
                         JOIN sys.columns c ON c.object_id = o.object_id
                         LEFT JOIN sys.types column_type ON column_type.user_type_id = c.user_type_id
                         LEFT JOIN sys.types base_column_type
                           ON base_column_type.user_type_id = c.system_type_id
                          AND base_column_type.user_type_id = base_column_type.system_type_id
                         WHERE o.type IN ('V','TF','IF')
                         ORDER BY o.object_id, c.column_id;
                         """,
                         connection))
               await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (catalog.ObjectsById.TryGetValue(reader.GetInt32(0), out var item) is false)
                {
                    continue;
                }

                item.Columns.Add(new ColumnBuilder
                {
                    Name = reader.GetString(1),
                    SqlType = reader.IsDBNull(2) ? "sql_variant" : reader.GetString(2),
                    MaxLength = reader.IsDBNull(3) ? (short)0 : reader.GetInt16(3),
                    Precision = reader.IsDBNull(4) ? (byte)0 : reader.GetByte(4),
                    Scale = reader.IsDBNull(5) ? (byte)0 : reader.GetByte(5),
                    IsNullable = reader.IsDBNull(6) is false && reader.GetBoolean(6),
                    IsIdentity = reader.IsDBNull(7) is false && Convert.ToInt32(reader.GetValue(7)) == 1
                });
            }
        }

        await using (var command = new SqlCommand(
                         """
                         SELECT sed.referencing_id,
                                sed.referenced_schema_name,
                                sed.referenced_entity_name
                         FROM sys.sql_expression_dependencies sed
                         JOIN sys.objects ro ON ro.object_id = sed.referencing_id
                         WHERE ro.type IN ('P','V','FN','TF','IF')
                           AND sed.referenced_entity_name IS NOT NULL;
                         """,
                         connection))
               await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var referencingId = reader.GetInt32(0);
                if (catalog.ObjectsById.TryGetValue(referencingId, out var item) is false)
                {
                    continue;
                }

                var referencedSchema = reader.IsDBNull(1) ? item.Schema : reader.GetString(1);
                var referencedName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (string.IsNullOrWhiteSpace(referencedName))
                {
                    continue;
                }

                var normalized = SqlSupport.NormalizeObjectName($"{referencedSchema}.{referencedName}");
                if (catalog.ObjectsByNormalizedName.TryGetValue(normalized, out var matches) is false)
                {
                    continue;
                }

                foreach (var match in matches)
                {
                    AddDependency(item, match, "sql-expression");
                }
            }
        }

        foreach (var item in catalog.Objects.Values)
        {
            if (string.IsNullOrWhiteSpace(item.Definition))
            {
                continue;
            }

            foreach (Match match in ExecRegex.Matches(item.Definition))
            {
                var referenced = ResolveByNormalizedName(catalog, SqlSupport.NormalizeObjectName(match.Groups["name"].Value), "stored-procedure");
                if (referenced is not null)
                {
                    AddDependency(item, referenced, "definition-exec");
                }
            }
        }

        return catalog;
    }

    private static string DeterminePreferredAlias(string filePath, string content)
    {
        if (filePath.Contains(@"\TeamsPortal.DataAccess\", StringComparison.OrdinalIgnoreCase))
        {
            return "client-portal-teams";
        }

        if (filePath.Contains(@"\APIAccess\AppCode\TeamDA.cs", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"\TechPortal.DataAccess\MSTeamDA.cs", StringComparison.OrdinalIgnoreCase)
            || content.Contains("DBConnector(\"Teams\")", StringComparison.OrdinalIgnoreCase))
        {
            return "teams";
        }

        if (content.Contains("Old_Client_Portal", StringComparison.OrdinalIgnoreCase))
        {
            return "client-portal-teams";
        }

        return "client-portal";
    }

    private static string ResolveExpression(string expression, IReadOnlyDictionary<string, string> constMap)
    {
        if (expression.StartsWith("@\"", StringComparison.Ordinal))
        {
            return expression[2..].TrimEnd('"');
        }

        if (expression.StartsWith("\"", StringComparison.Ordinal))
        {
            return expression.Trim('"');
        }

        if (constMap.TryGetValue(expression, out var value))
        {
            return value;
        }

        var lastToken = expression.Split('.').LastOrDefault();
        if (lastToken is not null && constMap.TryGetValue(lastToken, out value))
        {
            return value;
        }

        return string.Empty;
    }

    private static bool LooksLikeSqlText(string value)
    {
        var lowered = value.ToLowerInvariant();
        return lowered.Contains("select ", StringComparison.Ordinal)
               || lowered.Contains(" from ", StringComparison.Ordinal)
               || lowered.Contains(" join ", StringComparison.Ordinal);
    }

    private static IEnumerable<DatabaseObjectBuilder> MatchSqlObjects(
        string sql,
        string preferredAlias,
        IReadOnlyList<DatabaseCatalog> catalogs)
    {
        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SqlSourceRegex.Matches(sql))
        {
            var resolved = ResolveObjectReference(match.Groups["name"].Value, preferredAlias, catalogs, "view");
            if (resolved is not null)
            {
                matchedKeys.Add(resolved.Key);
            }
        }

        foreach (Match match in FunctionRegex.Matches(sql))
        {
            var resolved = ResolveObjectReference(match.Groups["name"].Value, preferredAlias, catalogs, null);
            if (resolved is not null && resolved.Kind.Contains("function", StringComparison.OrdinalIgnoreCase))
            {
                matchedKeys.Add(resolved.Key);
            }
        }

        var objectLookup = catalogs.SelectMany(catalog => catalog.Objects.Values).ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        return matchedKeys.Select(key => objectLookup[key]);
    }

    private static DatabaseObjectBuilder? ResolveObjectReference(
        string value,
        string preferredAlias,
        IReadOnlyList<DatabaseCatalog> catalogs,
        string? expectedKind)
    {
        var normalized = SqlSupport.NormalizeObjectName(value);
        var preferred = catalogs.FirstOrDefault(catalog => catalog.Target.Alias.Equals(preferredAlias, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            var preferredMatch = ResolveByNormalizedName(preferred, normalized, expectedKind);
            if (preferredMatch is not null)
            {
                return preferredMatch;
            }
        }

        var matches = catalogs
            .Select(catalog => ResolveByNormalizedName(catalog, normalized, expectedKind))
            .Where(item => item is not null)
            .Cast<DatabaseObjectBuilder>()
            .DistinctBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count == 1 ? matches[0] : matches.FirstOrDefault();
    }

    private static DatabaseObjectBuilder? ResolveByNormalizedName(DatabaseCatalog catalog, string normalizedName, string? expectedKind)
    {
        if (catalog.ObjectsByNormalizedName.TryGetValue(normalizedName, out var matches) is false)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(expectedKind))
        {
            return matches.FirstOrDefault();
        }

        return matches.FirstOrDefault(item => item.Kind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase))
               ?? matches.FirstOrDefault();
    }

    private static void AddDependency(DatabaseObjectBuilder source, DatabaseObjectBuilder target, string detectionKind)
    {
        if (source.Key.Equals(target.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (source.Dependencies.Any(item => item.ReferencedKey.Equals(target.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        source.Dependencies.Add(new DependencyBuilder
        {
            ReferencedKey = target.Key,
            ReferencedSchema = target.Schema,
            ReferencedName = target.Name,
            ReferencedKind = target.Kind,
            DetectionKind = detectionKind
        });
    }

    private static bool InferWriteOperation(string name)
    {
        return CpApis.Shared.Runtime.StoredProcedureWriteSafety.IsWriteName(name);
    }

    private static string GetLine(string content, int lineNumber)
    {
        using var reader = new StringReader(content);
        for (var current = 1; current <= lineNumber; current++)
        {
            var line = reader.ReadLine() ?? string.Empty;
            if (current == lineNumber)
            {
                return line.Trim();
            }
        }

        return string.Empty;
    }

    private static int GetLineNumber(string content, int index)
    {
        var line = 1;
        for (var current = 0; current < index && current < content.Length; current++)
        {
            if (content[current] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string Display(DatabaseObjectBuilder item) => $"{item.DatabaseAlias}:{item.Schema}.{item.Name}";

    private static async Task<SqlConnection> OpenConnectionWithRetryAsync(string connectionString, string databaseAlias)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                return connection;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }

        throw new InvalidOperationException($"Unable to connect to database alias '{databaseAlias}' after 3 attempts.", lastError);
    }

    private static string? InferModuleFromPath(string filePath)
    {
        var normalized = filePath.Replace('/', '\\');

        var techPortalRoot = @"D:\Technijian\TechPortal.DataAccess\";
        if (normalized.StartsWith(techPortalRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalized[techPortalRoot.Length..];
            var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            if (segments.Length > 1)
            {
                return segments[0];
            }

            return Path.GetFileNameWithoutExtension(segments[0])?.Replace("DA", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        if (normalized.Contains(@"\APIAccess\AppCode\TeamDA.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "Teams";
        }

        if (normalized.Contains(@"\TeamsPortal.DataAccess\", StringComparison.OrdinalIgnoreCase))
        {
            return "TeamsPortal";
        }

        if (normalized.Contains(@"\TechPortal.UI\Controllers\", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(normalized);
            return name?.Replace("Controller", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }
}
