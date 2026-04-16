using System.Text.RegularExpressions;
using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;
using CpApis.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace CpApis.Api.Controllers;

[ApiController]
[Route("api/catalog")]
public sealed class CatalogController : ControllerBase
{
    private static readonly HashSet<string> QueryNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "all", "api", "apis", "endpoint", "endpoints", "please",
        "show", "give", "find", "fetch", "for", "of", "to", "me", "with", "and"
    };

    private readonly ManifestCatalogService _catalogService;

    public CatalogController(ManifestCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    [HttpGet("databases")]
    public IActionResult GetDatabases() => Ok(_catalogService.GetDatabaseTargets());

    [HttpGet("objects")]
    public IActionResult GetObjects([FromQuery] string? databaseAlias, [FromQuery] string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) is false &&
            string.Equals(kind, "stored-procedure", StringComparison.OrdinalIgnoreCase) is false)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unsupported kind",
                Detail = "This API exposes stored procedures only. Use kind=stored-procedure."
            });
        }

        return Ok(_catalogService.GetObjects(databaseAlias, "stored-procedure"));
    }

    [HttpGet("objects/{databaseAlias}/{kind}/{schema}/{name}")]
    public IActionResult GetObject(string databaseAlias, string kind, string schema, string name)
    {
        if (string.Equals(kind, "stored-procedure", StringComparison.OrdinalIgnoreCase) is false)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unsupported kind",
                Detail = "This API exposes stored procedures only."
            });
        }

        try
        {
            return Ok(_catalogService.GetRequiredObject(databaseAlias, "stored-procedure", schema, name));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails { Title = "Object not found", Detail = ex.Message });
        }
    }

    [HttpGet("modules/guide")]
    public IActionResult GetModuleGuide([FromQuery] string? databaseAlias, [FromQuery] int take = 50)
    {
        var normalizedTake = Math.Clamp(take <= 0 ? 50 : take, 1, 200);

        var response = GetIncludedObjects(databaseAlias)
            .GroupBy(item => item.Module, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var orderedGuides = group
                    .Select(BuildGuideItem)
                    .OrderByDescending(CalculateDefaultScore)
                    .ThenBy(item => item.Label)
                    .ToList();
                var representative = orderedGuides.First();
                var exampleApis = orderedGuides
                    .Take(5)
                    .Select(ToGuideResponse)
                    .ToArray();

                return new
                {
                    module = group.First().Module,
                    moduleSegment = StoredProcedureMetadataBuilder.NormalizeModuleSegment(group.First().Module),
                    businessArea = representative.BusinessArea,
                    routePrefix = $"/api/modules/{StoredProcedureMetadataBuilder.NormalizeModuleSegment(group.First().Module)}/stored-procedures",
                    endpointCount = group.Count(),
                    readEndpointCount = group.Count(item => item.IsWriteOperation is false),
                    writeEndpointCount = group.Count(item => item.IsWriteOperation),
                    examplePrompts = orderedGuides
                        .SelectMany(item => item.SamplePrompts)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(6)
                        .ToArray(),
                    exampleApis
                };
            })
            .OrderByDescending(item => item.endpointCount)
            .ThenBy(item => item.module)
            .Take(normalizedTake)
            .ToList();

        return Ok(response);
    }

    [HttpGet("guide")]
    public IActionResult GetGuide(
        [FromQuery] string? q,
        [FromQuery] string? module,
        [FromQuery] string? intent,
        [FromQuery] string? databaseAlias,
        [FromQuery] bool includeWrite = true,
        [FromQuery] int take = 100)
    {
        var normalizedTake = Math.Clamp(take <= 0 ? 100 : take, 1, 1000);

        var items = GetIncludedObjects(databaseAlias)
            .Where(item => includeWrite || item.IsWriteOperation is false)
            .Where(item =>
                string.IsNullOrWhiteSpace(module)
                || item.Module.Equals(module, StringComparison.OrdinalIgnoreCase)
                || StoredProcedureMetadataBuilder.NormalizeModuleSegment(item.Module).Equals(module, StringComparison.OrdinalIgnoreCase))
            .Select(BuildGuideItem)
            .ToList();

        if (string.IsNullOrWhiteSpace(intent) is false)
        {
            items = items
                .Where(item => item.Intent.Equals(intent, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(q) is false)
        {
            var terms = BuildSearchTerms(q);
            if (terms.Count > 0)
            {
                items = items
                    .Select(item => new SearchMatch(
                        item,
                        BuildSearchDocument(item),
                        CalculateSearchScore(item, q, terms)))
                    .Where(match => terms.All(term => match.SearchDocument.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(match => match.Score)
                    .ThenBy(match => match.Item.Label)
                    .ThenBy(match => match.Item.ObjectName)
                    .Take(normalizedTake)
                    .Select(match => match.Item)
                    .ToList();

                return Ok(items.Select(ToGuideResponse));
            }
        }

        return Ok(items
            .OrderBy(item => item.IsWriteOperation)
            .ThenByDescending(CalculateDefaultScore)
            .ThenBy(item => item.Label)
            .Take(normalizedTake)
            .Select(ToGuideResponse));
    }

    [HttpGet("guide/{databaseAlias}/{schema}/{name}")]
    public IActionResult GetGuideDetail(string databaseAlias, string schema, string name)
    {
        try
        {
            var item = _catalogService.GetRequiredObject(databaseAlias, "stored-procedure", schema, name);
            var guideItem = BuildGuideItem(item);

            return Ok(new
            {
                label = guideItem.Label,
                businessArea = guideItem.BusinessArea,
                intent = guideItem.Intent,
                route = guideItem.Route,
                method = guideItem.Method,
                module = guideItem.Module,
                databaseAlias = guideItem.DatabaseAlias,
                schema = guideItem.Schema,
                objectName = guideItem.ObjectName,
                whenToUse = guideItem.WhenToUse,
                description = guideItem.Description,
                responseHint = guideItem.ResponseHint,
                isWriteOperation = guideItem.IsWriteOperation,
                safeForReadOnlyUse = guideItem.IsWriteOperation is false,
                parameterSummary = guideItem.ParameterSummary,
                parameterNames = guideItem.ParameterNames,
                requiredParameterNames = guideItem.RequiredParameterNames,
                optionalParameterNames = guideItem.OptionalParameterNames,
                samplePrompts = guideItem.SamplePrompts,
                keywords = guideItem.Keywords,
                requestTemplate = StoredProcedureMetadataBuilder.BuildRequestTemplate(item),
                requestBodyDescription = StoredProcedureMetadataBuilder.BuildRequestBodyDescription(item),
                sourceReferences = item.SourceReferences.Select(reference => new
                {
                    filePath = reference.FilePath,
                    lineNumber = reference.LineNumber,
                    matchKind = reference.MatchKind
                }),
                dependencyTrails = item.DependencyTrails
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails { Title = "Object not found", Detail = ex.Message });
        }
    }

    private IEnumerable<DatabaseObjectManifest> GetIncludedObjects(string? databaseAlias = null)
    {
        return _catalogService.GetObjects(databaseAlias, "stored-procedure")
            .Where(item => item.IsIncluded)
            .DistinctBy(item => item.Key);
    }

    private static GuideItem BuildGuideItem(DatabaseObjectManifest item)
    {
        var inputParameters = StoredProcedureMetadataBuilder.GetInputParameters(item);
        var requiredParameterNames = inputParameters
            .Where(parameter => parameter.IsNullable is false && parameter.HasDefaultValue is false)
            .Select(parameter => parameter.Name.TrimStart('@'))
            .ToArray();
        var optionalParameterNames = inputParameters
            .Where(parameter => parameter.IsNullable || parameter.HasDefaultValue)
            .Select(parameter => parameter.Name.TrimStart('@'))
            .ToArray();
        var parameterNames = inputParameters
            .Select(parameter => parameter.Name.TrimStart('@'))
            .ToArray();

        return new GuideItem(
            item,
            StoredProcedureMetadataBuilder.BuildFriendlyLabel(item),
            StoredProcedureMetadataBuilder.BuildBusinessArea(item),
            StoredProcedureMetadataBuilder.BuildIntent(item),
            item.Module,
            item.DatabaseAlias,
            item.Schema,
            item.Name,
            StoredProcedureMetadataBuilder.BuildRoute(item),
            "POST",
            item.IsWriteOperation,
            StoredProcedureMetadataBuilder.BuildResponseHint(item),
            StoredProcedureMetadataBuilder.BuildUseCaseSummary(item),
            StoredProcedureMetadataBuilder.BuildEndpointDescription(item),
            StoredProcedureMetadataBuilder.BuildParameterSummary(item),
            StoredProcedureMetadataBuilder.BuildIntentKeywords(item),
            StoredProcedureMetadataBuilder.BuildSamplePrompts(item),
            StoredProcedureMetadataBuilder.BuildSearchAliases(item),
            parameterNames,
            requiredParameterNames,
            optionalParameterNames);
    }

    private static object ToGuideResponse(GuideItem item)
    {
        return new
        {
            label = item.Label,
            businessArea = item.BusinessArea,
            intent = item.Intent,
            module = item.Module,
            databaseAlias = item.DatabaseAlias,
            schema = item.Schema,
            objectName = item.ObjectName,
            route = item.Route,
            method = item.Method,
            isWriteOperation = item.IsWriteOperation,
            responseHint = item.ResponseHint,
            whenToUse = item.WhenToUse,
            description = item.Description,
            parameterSummary = item.ParameterSummary,
            parameterNames = item.ParameterNames,
            requiredParameterNames = item.RequiredParameterNames,
            optionalParameterNames = item.OptionalParameterNames,
            samplePrompts = item.SamplePrompts,
            keywords = item.Keywords
        };
    }

    private static IReadOnlyList<string> BuildSearchTerms(string query)
    {
        return Regex.Split(query, @"[^A-Za-z0-9]+")
            .Select(term => term.Trim())
            .Where(term => string.IsNullOrWhiteSpace(term) is false)
            .Where(term => QueryNoiseTokens.Contains(term) is false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildSearchDocument(GuideItem item)
    {
        return string.Join(" ", new[]
        {
            item.Label,
            item.BusinessArea,
            item.Intent,
            item.Module,
            item.ObjectName,
            item.ResponseHint,
            item.WhenToUse,
            item.Description,
            item.ParameterSummary,
            string.Join(" ", item.Keywords),
            string.Join(" ", item.ParameterNames),
            string.Join(" ", item.SamplePrompts),
            string.Join(" ", item.SearchAliases)
        });
    }

    private static int CalculateSearchScore(GuideItem item, string query, IReadOnlyList<string> terms)
    {
        var score = item.IsWriteOperation ? 0 : 10;

        foreach (var term in terms)
        {
            if (item.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 12;
            }

            if (item.BusinessArea.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (item.Intent.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 8;
            }

            if (item.Module.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 8;
            }

            if (item.ObjectName.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 8;
            }

            if (item.SamplePrompts.Any(prompt => prompt.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                score += 14;
            }

            if (item.SearchAliases.Any(alias => alias.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                score += 10;
            }

            if (item.ParameterNames.Any(parameter => parameter.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                score += 4;
            }
        }

        if (query.Contains("sample", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("example", StringComparison.OrdinalIgnoreCase))
        {
            score += item.IsWriteOperation ? -12 : 12;

            if (item.Label.Contains("Copy", StringComparison.OrdinalIgnoreCase)
                || item.Label.Contains("Temp", StringComparison.OrdinalIgnoreCase)
                || item.Label.Contains("Notification", StringComparison.OrdinalIgnoreCase)
                || item.Label.Contains("Manual", StringComparison.OrdinalIgnoreCase))
            {
                score -= 24;
            }

            if (item.Label.Contains("Header", StringComparison.OrdinalIgnoreCase)
                || item.Label.Contains("Detail", StringComparison.OrdinalIgnoreCase)
                || item.Label.Contains("Report", StringComparison.OrdinalIgnoreCase)
                || item.Label.Contains("Info", StringComparison.OrdinalIgnoreCase))
            {
                score += 16;
            }
        }

        if (query.Contains("xml", StringComparison.OrdinalIgnoreCase) &&
            item.ResponseHint.Contains("XML", StringComparison.OrdinalIgnoreCase))
        {
            score += 12;
        }

        if (query.Contains("report", StringComparison.OrdinalIgnoreCase) &&
            item.ResponseHint.Contains("Report", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (query.Contains("proposal", StringComparison.OrdinalIgnoreCase) &&
            item.Module.Equals("ProjectProposal", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (query.Contains("contract", StringComparison.OrdinalIgnoreCase) &&
            item.Module.Equals("Contract", StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
        }

        if (query.Contains("weekly invoice", StringComparison.OrdinalIgnoreCase) &&
            item.BusinessArea.Contains("Invoice", StringComparison.OrdinalIgnoreCase))
        {
            score += 16;

            if (item.Module.Equals("Invoices", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
        }

        return score;
    }

    private static int CalculateDefaultScore(GuideItem item)
    {
        var score = item.IsWriteOperation ? -10 : 20;

        if (item.Intent.Equals("Get", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (item.Intent.Equals("List", StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (item.ResponseHint.Contains("Structured", StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (item.ResponseHint.Contains("List", StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (item.ResponseHint.Contains("XML", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (item.ResponseHint.Contains("Report", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        if (item.ParameterNames.Length <= 2)
        {
            score += 4;
        }

        if (item.SamplePrompts.Any(prompt =>
                prompt.Contains("sample weekly invoice", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("sample contract xml", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("sample proposal xml", StringComparison.OrdinalIgnoreCase)))
        {
            score += 14;
        }

        if (item.Label.Contains("Copy", StringComparison.OrdinalIgnoreCase)
            || item.Label.Contains("Temp", StringComparison.OrdinalIgnoreCase)
            || item.Label.Contains("Fix", StringComparison.OrdinalIgnoreCase)
            || item.Label.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || item.Label.Contains("Workflow", StringComparison.OrdinalIgnoreCase)
            || item.Label.Contains("Manual", StringComparison.OrdinalIgnoreCase))
        {
            score -= 20;
        }

        return score;
    }

    private sealed record GuideItem(
        DatabaseObjectManifest Source,
        string Label,
        string BusinessArea,
        string Intent,
        string Module,
        string DatabaseAlias,
        string Schema,
        string ObjectName,
        string Route,
        string Method,
        bool IsWriteOperation,
        string ResponseHint,
        string WhenToUse,
        string Description,
        string ParameterSummary,
        IReadOnlyList<string> Keywords,
        IReadOnlyList<string> SamplePrompts,
        IReadOnlyList<string> SearchAliases,
        string[] ParameterNames,
        string[] RequiredParameterNames,
        string[] OptionalParameterNames);

    private sealed record SearchMatch(GuideItem Item, string SearchDocument, int Score);
}
