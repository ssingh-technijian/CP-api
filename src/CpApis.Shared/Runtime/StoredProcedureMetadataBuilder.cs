using System.Text.RegularExpressions;
using CpApis.Shared.Inventory;

namespace CpApis.Shared.Runtime;

public static class StoredProcedureMetadataBuilder
{
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "stp", "sp", "usp", "fn", "vw", "dt", "int", "str", "bool", "apiaccess",
        "get", "gets", "list", "getlist", "view", "save", "update", "create", "delete",
        "remove", "add", "set", "current", "new", "v1", "v2", "v3", "v4", "all",
        "dbo", "dir", "internal", "lookups", "lookup", "filter", "filters", "misc"
    };

    private static readonly Dictionary<string, string> TokenExpansions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = "AI",
        ["appr"] = "Approval",
        ["asst"] = "Asset",
        ["cal"] = "Calendar",
        ["chk"] = "Checklist",
        ["clt"] = "Client",
        ["client"] = "Client",
        ["clients"] = "Clients",
        ["con"] = "Contract",
        ["contract"] = "Contract",
        ["contracts"] = "Contracts",
        ["copyto"] = "Copy To",
        ["detail"] = "Detail",
        ["dir"] = "Directory",
        ["dsign"] = "Digital Signature",
        ["dsb"] = "Dashboard",
        ["ddl"] = "Dropdown",
        ["email"] = "Email",
        ["enty"] = "Entry",
        ["entry"] = "Entry",
        ["est"] = "Estimate",
        ["estm"] = "Estimate",
        ["eom"] = "EOM",
        ["fix"] = "Fix",
        ["grp"] = "Group",
        ["hdr"] = "Header",
        ["hrs"] = "Hours",
        ["hr"] = "Hours",
        ["info"] = "Info",
        ["inv"] = "Invoice",
        ["invoice"] = "Invoice",
        ["invoices"] = "Invoices",
        ["loc"] = "Location",
        ["lst"] = "List",
        ["mc"] = "Monthly Count",
        ["need"] = "Need",
        ["notif"] = "Notification",
        ["org"] = "Organization",
        ["op"] = "Operations",
        ["payroll"] = "Payroll",
        ["phase"] = "Phase",
        ["pod"] = "POD",
        ["preview"] = "Preview",
        ["proj"] = "Project",
        ["project"] = "Project",
        ["prop"] = "Proposal",
        ["proposal"] = "Proposal",
        ["proposals"] = "Proposals",
        ["px"] = "Price",
        ["qty"] = "Quantity",
        ["rep"] = "Rep",
        ["report"] = "Report",
        ["salesrep"] = "Sales Rep",
        ["sch"] = "Schedule",
        ["sup"] = "Support",
        ["svc"] = "Service",
        ["te"] = "Time Entry",
        ["tech"] = "Tech",
        ["temp"] = "Temp",
        ["tfa"] = "Two Factor",
        ["tkt"] = "Ticket",
        ["tickets"] = "Tickets",
        ["treeview"] = "Tree",
        ["usr"] = "User",
        ["users"] = "Users",
        ["wf"] = "Workflow",
        ["xml"] = "XML"
    };

    private static readonly Dictionary<string, string> ActionPrompts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Approve"] = "approve",
        ["Create"] = "create",
        ["Delete"] = "delete",
        ["Execute"] = "run",
        ["Get"] = "get",
        ["List"] = "list",
        ["Save"] = "save",
        ["Sign"] = "sign",
        ["Update"] = "update",
        ["Validate"] = "validate"
    };

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

    public static string BuildFriendlyLabel(DatabaseObjectManifest item)
    {
        var action = DetectAction(item);
        var subject = DetectSubject(item, action);
        return string.IsNullOrWhiteSpace(subject) ? $"{action} {item.Name}" : $"{action} {subject}";
    }

    public static string BuildBusinessArea(DatabaseObjectManifest item)
    {
        var moduleTokens = GetMeaningfulTokens(item.Module);
        if (moduleTokens.Count > 0)
        {
            return string.Join(" ", moduleTokens.Take(3));
        }

        var fallback = GetMeaningfulTokens(item.Name).Take(3).ToList();
        return fallback.Count == 0 ? "General" : string.Join(" ", fallback);
    }

    public static string BuildIntent(DatabaseObjectManifest item) => DetectAction(item);

    public static string BuildParameterSummary(DatabaseObjectManifest item)
    {
        var inputParameters = GetInputParameters(item).ToList();
        if (inputParameters.Count == 0)
        {
            return "No input parameters.";
        }

        var required = inputParameters
            .Where(parameter => parameter.IsNullable is false && parameter.HasDefaultValue is false)
            .Select(parameter => parameter.Name.TrimStart('@'))
            .ToList();

        var optional = inputParameters
            .Where(parameter => parameter.IsNullable || parameter.HasDefaultValue)
            .Select(parameter => parameter.Name.TrimStart('@'))
            .ToList();

        if (required.Count > 0 && optional.Count > 0)
        {
            return $"Required parameters: {string.Join(", ", required)}. Optional parameters: {string.Join(", ", optional)}.";
        }

        if (required.Count > 0)
        {
            return $"Required parameters: {string.Join(", ", required)}.";
        }

        return $"Optional parameters: {string.Join(", ", optional)}.";
    }

    public static string BuildResponseHint(DatabaseObjectManifest item)
    {
        if (item.IsWriteOperation)
        {
            return "Write or save result";
        }

        if (LooksLikeXmlProcedure(item) && LooksLikeReportProcedure(item))
        {
            return "XML or report-style response";
        }

        if (LooksLikeXmlProcedure(item))
        {
            return "XML-style structured response";
        }

        if (LooksLikeListProcedure(item))
        {
            return "List response";
        }

        if (LooksLikeReportProcedure(item))
        {
            return "Report response";
        }

        return "Structured response";
    }

    public static string BuildUseCaseSummary(DatabaseObjectManifest item)
    {
        var label = BuildFriendlyLabel(item);
        var responseHint = BuildResponseHint(item);
        var parameterSummary = BuildParameterSummary(item);
        var safetyHint = item.IsWriteOperation
            ? "This can change data."
            : "This is read-only.";

        return $"{label}. {responseHint}. {parameterSummary} {safetyHint}";
    }

    public static string BuildEndpointDescription(DatabaseObjectManifest item)
    {
        var prompts = BuildSamplePrompts(item).Take(3).ToArray();
        var promptText = prompts.Length == 0
            ? "Use the label and parameter summary to decide when to call this route."
            : $"Example prompts: {string.Join("; ", prompts)}.";

        return $"{BuildUseCaseSummary(item)} {promptText} Stored procedure: {item.Schema}.{item.Name}.";
    }

    public static IReadOnlyList<string> BuildIntentKeywords(DatabaseObjectManifest item)
    {
        return GetMeaningfulTokens(item.Module)
            .Concat(GetMeaningfulTokens(item.Name))
            .Concat(Tokenize(BuildIntent(item)).Select(ExpandToken))
            .Where(token => string.IsNullOrWhiteSpace(token) is false)
            .GroupBy(NormalizeTokenForComparison, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public static IReadOnlyList<string> BuildSearchAliases(DatabaseObjectManifest item)
    {
        var values = new List<string>
        {
            BuildFriendlyLabel(item),
            BuildBusinessArea(item),
            BuildIntent(item),
            BuildResponseHint(item)
        };

        values.AddRange(BuildIntentKeywords(item));
        values.AddRange(BuildSamplePrompts(item));

        if (LooksLikeXmlProcedure(item) && item.IsWriteOperation is false)
        {
            values.Add($"sample {BuildPromptSubject(item)} xml");
            values.Add($"{BuildPromptSubject(item)} xml");
        }

        if (LooksLikeReportProcedure(item) && item.IsWriteOperation is false)
        {
            values.Add($"{BuildPromptSubject(item)} report");
        }

        if (LooksLikeListProcedure(item) && item.IsWriteOperation is false)
        {
            values.Add($"all {BuildPromptSubject(item)}");
        }

        return values
            .Where(value => string.IsNullOrWhiteSpace(value) is false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string BuildRoute(DatabaseObjectManifest item)
    {
        var moduleSegment = NormalizeModuleSegment(item.Module);
        return $"/api/modules/{moduleSegment}/stored-procedures/{item.DatabaseAlias}/{item.Schema}/{item.Name}/execute";
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

    private static string DetectAction(DatabaseObjectManifest item)
    {
        var tokens = Tokenize(item.Name)
            .Concat(Tokenize(item.Module))
            .Select(NormalizeTokenForComparison)
            .ToArray();

        if (tokens.Contains("approve") || tokens.Contains("approval"))
        {
            return "Approve";
        }

        if (tokens.Contains("validate"))
        {
            return "Validate";
        }

        if (tokens.Contains("sign"))
        {
            return "Sign";
        }

        if (item.IsWriteOperation)
        {
            if (tokens.Contains("delete") || tokens.Contains("remove"))
            {
                return "Delete";
            }

            if (tokens.Contains("update"))
            {
                return "Update";
            }

            if (tokens.Contains("create") || tokens.Contains("add"))
            {
                return "Create";
            }

            if (tokens.Contains("save"))
            {
                return "Save";
            }
        }

        if (tokens.Contains("list") || tokens.Contains("grid"))
        {
            return "List";
        }

        if (tokens.Contains("report") || tokens.Contains("preview") || tokens.Contains("info") || tokens.Contains("header") || tokens.Contains("detail") || tokens.Contains("tree"))
        {
            return "Get";
        }

        return item.IsWriteOperation ? "Execute" : "Get";
    }

    private static string DetectSubject(DatabaseObjectManifest item, string action)
    {
        var combined = GetMeaningfulTokens(item.Module)
            .Concat(GetMeaningfulTokens(item.Name))
            .GroupBy(NormalizeTokenForComparison, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (combined.Count == 0)
        {
            return item.Name;
        }

        while (combined.Count > 0 && IsEquivalentToAction(combined[0], action))
        {
            combined.RemoveAt(0);
        }

        if (combined.Count == 0)
        {
            return item.Name;
        }

        if (combined.Count == 1 && string.Equals(action, "List", StringComparison.OrdinalIgnoreCase))
        {
            combined[0] = Pluralize(combined[0]);
        }

        return string.Join(" ", combined.Take(8));
    }

    public static IReadOnlyList<string> BuildSamplePrompts(DatabaseObjectManifest item)
    {
        var subject = BuildPromptSubject(item);
        var prompts = new List<string>();
        var action = BuildIntent(item);
        var actionPrompt = ActionPrompts.TryGetValue(action, out var promptVerb)
            ? promptVerb
            : "get";

        prompts.Add($"{actionPrompt} {subject}");

        if (action.Equals("List", StringComparison.OrdinalIgnoreCase) is false && LooksLikeListProcedure(item))
        {
            prompts.Add($"list {subject}");
        }

        if (LooksLikeXmlProcedure(item) && item.IsWriteOperation is false)
        {
            prompts.Add($"sample {subject} xml");
            prompts.Add($"{subject} xml");
        }

        if (LooksLikeReportProcedure(item) && item.IsWriteOperation is false)
        {
            prompts.Add($"{subject} report");
        }

        if (BuildBusinessArea(item).Contains("Invoice", StringComparison.OrdinalIgnoreCase) &&
            subject.Contains("weekly", StringComparison.OrdinalIgnoreCase))
        {
            prompts.Add("sample weekly invoice");
        }

        if (BuildBusinessArea(item).Contains("Proposal", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeXmlProcedure(item) &&
            item.IsWriteOperation is false)
        {
            prompts.Add("sample proposal xml");
        }

        if (BuildBusinessArea(item).Contains("Contract", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeXmlProcedure(item) &&
            item.IsWriteOperation is false)
        {
            prompts.Add("sample contract xml");
        }

        return prompts
            .Where(prompt => string.IsNullOrWhiteSpace(prompt) is false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static string BuildPromptSubject(DatabaseObjectManifest item)
    {
        var subject = DetectSubject(item, BuildIntent(item));
        var normalized = subject
            .Replace("XML", "xml", StringComparison.OrdinalIgnoreCase)
            .Replace("POD", "pod", StringComparison.OrdinalIgnoreCase)
            .Trim();

        return normalized.ToLowerInvariant();
    }

    private static List<string> GetMeaningfulTokens(string value)
    {
        return Tokenize(value)
            .Select(ExpandToken)
            .Where(token => string.IsNullOrWhiteSpace(token) is false)
            .GroupBy(NormalizeTokenForComparison, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        foreach (Match match in Regex.Matches(value ?? string.Empty, @"[A-Z]+(?=$|[A-Z][a-z])|[A-Z]?[a-z]+|[0-9]+"))
        {
            var token = match.Value.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            yield return token;
        }
    }

    private static string ExpandToken(string token)
    {
        if (TokenExpansions.TryGetValue(token, out var expanded))
        {
            return expanded;
        }

        if (NoiseTokens.Contains(token))
        {
            return string.Empty;
        }

        if (Regex.IsMatch(token, @"^v\d+$", RegexOptions.IgnoreCase))
        {
            return string.Empty;
        }

        return token;
    }

    private static bool IsEquivalentToAction(string token, string action)
    {
        var normalizedToken = NormalizeTokenForComparison(token);
        var normalizedAction = NormalizeTokenForComparison(action);

        return normalizedToken == normalizedAction
            || (normalizedAction == "approve" && normalizedToken == "approval")
            || (normalizedAction == "get" && normalizedToken == "view");
    }

    private static bool LooksLikeXmlProcedure(DatabaseObjectManifest item) =>
        item.Name.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeListProcedure(DatabaseObjectManifest item)
    {
        if (BuildIntent(item).Equals("List", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return item.Name.Contains("grid", StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains("list", StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains("getlist", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeReportProcedure(DatabaseObjectManifest item) =>
        item.Name.Contains("report", StringComparison.OrdinalIgnoreCase)
        || item.Name.Contains("preview", StringComparison.OrdinalIgnoreCase)
        || item.Name.Contains("detail", StringComparison.OrdinalIgnoreCase)
        || item.Name.Contains("header", StringComparison.OrdinalIgnoreCase)
        || item.Name.Contains("info", StringComparison.OrdinalIgnoreCase);

    private static string Pluralize(string value)
    {
        if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (value.EndsWith("y", StringComparison.OrdinalIgnoreCase))
        {
            return value[..^1] + "ies";
        }

        return value + "s";
    }

    private static string NormalizeTokenForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && normalized.Length > 3)
        {
            return normalized[..^3] + "y";
        }

        if (normalized.EndsWith("s", StringComparison.OrdinalIgnoreCase) && normalized.Length > 1)
        {
            return normalized[..^1];
        }

        return normalized;
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
