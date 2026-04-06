using System.Text.RegularExpressions;
using CpApis.Shared.Inventory;

namespace CpApis.Shared.Runtime;

public static class StoredProcedureMetadataBuilder
{
    public static object BuildRequestTemplate(DatabaseObjectManifest item)
    {
        return item.Kind switch
        {
            "stored-procedure" => new Dictionary<string, object?>
            {
                ["Parameters"] = BuildParameterTemplate(item)
            },
            _ => new Dictionary<string, object?>()
        };
    }

    public static Dictionary<string, object?> BuildParameterTemplate(DatabaseObjectManifest item)
    {
        return item.Parameters
            .Where(parameter => parameter.ParameterId > 0 && parameter.IsOutput is false && string.IsNullOrWhiteSpace(parameter.Name) is false)
            .OrderBy(parameter => parameter.ParameterId)
            .ToDictionary(
                parameter => parameter.Name.TrimStart('@'),
                parameter => (object?)BuildExampleValue(parameter),
                StringComparer.OrdinalIgnoreCase);
    }

    public static string BuildRequestBodyDescription(DatabaseObjectManifest item)
    {
        var inputParameters = GetInputParameters(item).ToList();

        if (inputParameters.Count == 0)
        {
            return "This stored procedure has no input parameters. The example body can be sent as-is.";
        }

        var parameterSummary = string.Join(", ", inputParameters.Select(parameter =>
            $"{parameter.Name.TrimStart('@')} ({parameter.SqlType}{(parameter.IsNullable ? ", nullable" : string.Empty)}{(parameter.HasDefaultValue ? ", default" : string.Empty)})"));

        return $"Fill the JSON body below using these input parameter keys: {parameterSummary}. Use parameter names without the leading @. Output parameters are returned by the API and usually should not be sent.";
    }

    public static IReadOnlyList<ParameterManifest> GetInputParameters(DatabaseObjectManifest item)
    {
        return item.Parameters
            .Where(parameter => parameter.ParameterId > 0 && parameter.IsOutput is false && string.IsNullOrWhiteSpace(parameter.Name) is false)
            .OrderBy(parameter => parameter.ParameterId)
            .ToList();
    }

    public static object BuildExampleValue(ParameterManifest parameter)
    {
        var sqlType = parameter.SqlType.ToLowerInvariant();
        var parameterName = parameter.Name.TrimStart('@');

        return sqlType switch
        {
            "bit" => true,
            "tinyint" or "smallint" or "int" or "bigint" => 0,
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => 0,
            "date" => "2026-01-01",
            "datetime" or "datetime2" or "smalldatetime" => "2026-01-01T00:00:00",
            "datetimeoffset" => "2026-01-01T00:00:00+00:00",
            "time" => "12:00:00",
            "uniqueidentifier" => "00000000-0000-0000-0000-000000000000",
            "xml" => "<Root />",
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "AA==",
            _ when LooksLikeXmlParameter(parameterName) => "<Root />",
            _ when LooksLikeDateParameter(parameterName) => "2026-01-01T00:00:00",
            _ when LooksLikeTimeParameter(parameterName) => "12:00:00",
            _ when LooksLikeGuidTextParameter(parameterName, parameter.MaxLength) => "00000000-0000-0000-0000-000000000000",
            _ when LooksLikeNumericTextParameter(parameterName) => "0",
            _ => string.Empty
        };
    }

    public static string NormalizeModuleSegment(string value)
    {
        var cleaned = Regex.Replace(value, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(cleaned) ? "misc" : cleaned;
    }

    private static bool LooksLikeXmlParameter(string parameterName) =>
        parameterName.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDateParameter(string parameterName) =>
        parameterName.Contains("date", StringComparison.OrdinalIgnoreCase)
        || parameterName.Contains("_dt", StringComparison.OrdinalIgnoreCase)
        || parameterName.EndsWith("dt", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTimeParameter(string parameterName) =>
        parameterName.Contains("time", StringComparison.OrdinalIgnoreCase)
        && parameterName.Contains("datetime", StringComparison.OrdinalIgnoreCase) is false;

    private static bool LooksLikeNumericTextParameter(string parameterName) =>
        Regex.IsMatch(
            parameterName,
            @"(^|_)(id|ids|seq|count|mode|month|months|year|years|qty|quantity|number|num)($|_)",
            RegexOptions.IgnoreCase)
        || parameterName.EndsWith("id", StringComparison.OrdinalIgnoreCase)
        || parameterName.EndsWith("ids", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeGuidTextParameter(string parameterName, short maxLength) =>
        parameterName.Contains("guid", StringComparison.OrdinalIgnoreCase)
        || (parameterName.Equals("ID", StringComparison.OrdinalIgnoreCase) && maxLength >= 36);
}
