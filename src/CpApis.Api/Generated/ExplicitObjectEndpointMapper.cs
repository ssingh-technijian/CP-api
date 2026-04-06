using System.Text.RegularExpressions;
using CpApis.Api.OpenApi;
using CpApis.Api.Services;
using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;
using CpApis.Shared.Services;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;

namespace CpApis.Api.Generated;

internal static class ExplicitObjectEndpointMapper
{
    public static void Map(WebApplication app)
    {
        var catalog = app.Services.GetRequiredService<ManifestCatalogService>();
        var objects = catalog.GetObjects(kind: "stored-procedure")
            .DistinctBy(item => item.Key)
            .ToList();
        var mappedRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var moduleGroup in objects.GroupBy(item => NormalizeSegment(item.Module)).OrderBy(group => group.Key))
        {
            var moduleName = moduleGroup.First().Module;
            var moduleSegment = moduleGroup.Key;
            var baseGroup = app.MapGroup($"/api/modules/{moduleGroup.Key}")
                .WithTags(moduleName);

            var storedProcedureGroup = baseGroup.MapGroup("/stored-procedures");

            foreach (var item in moduleGroup)
            {
                var captured = item;
                var objectRoute = $"/{captured.DatabaseAlias}/{captured.Schema}/{captured.Name}";
                var executeRoute = $"{objectRoute}/execute";
                if (!TryReserveRoutes(mappedRoutes, moduleSegment, "stored-procedures", executeRoute))
                {
                    continue;
                }

                storedProcedureGroup.MapPost(
                        executeRoute,
                        async (StoredProcedureExecutionRequest? request, DatabaseExecutionService executionService, CancellationToken cancellationToken) =>
                        {
                            try
                            {
                                var response = await executionService.ExecuteStoredProcedureAsync(captured, request ?? new StoredProcedureExecutionRequest(), cancellationToken);
                                return Results.Ok(response);
                            }
                            catch (ArgumentException ex)
                            {
                                return Results.BadRequest(new ProblemDetails
                                {
                                    Title = "Invalid request",
                                    Detail = ex.Message,
                                    Status = StatusCodes.Status400BadRequest
                                });
                            }
                            catch (SqlException ex)
                            {
                                return Results.Problem(
                                    title: "Stored procedure execution failed",
                                    detail: ex.Message,
                                    statusCode: StatusCodes.Status500InternalServerError);
                            }
                            catch (Exception ex)
                            {
                                return Results.Problem(
                                    title: "Unexpected error",
                                    detail: ex.Message,
                                    statusCode: StatusCodes.Status500InternalServerError);
                            }
                        })
                    .WithMetadata(new ObjectRequestBodyMetadata(
                        StoredProcedureMetadataBuilder.BuildRequestTemplate(captured),
                        StoredProcedureMetadataBuilder.BuildRequestBodyDescription(captured)))
                    .WithName(BuildEndpointName("sp", captured))
                    .WithSummary($"Execute stored procedure {captured.Schema}.{captured.Name}")
                    .WithDescription("Fill the request body example shown in Swagger and replace the sample values before execution.");
            }
        }
    }

    private static string BuildEndpointName(string prefix, DatabaseObjectManifest item)
    {
        var raw = $"{prefix}-{item.DatabaseAlias}-{item.Schema}-{item.Name}";
        return Regex.Replace(raw, @"[^A-Za-z0-9\-]+", "-");
    }

    private static string NormalizeSegment(string value)
    {
        return StoredProcedureMetadataBuilder.NormalizeModuleSegment(value);
    }

    private static string BuildRouteKey(string moduleSegment, string objectSegment, string route)
    {
        return $"/api/modules/{moduleSegment}/{objectSegment}{route}";
    }

    private static bool TryReserveRoutes(HashSet<string> mappedRoutes, string moduleSegment, string objectSegment, params string[] routes)
    {
        var keys = routes.Select(route => BuildRouteKey(moduleSegment, objectSegment, route)).ToArray();
        if (keys.Any(mappedRoutes.Contains))
        {
            return false;
        }

        foreach (var key in keys)
        {
            mappedRoutes.Add(key);
        }

        return true;
    }

}
