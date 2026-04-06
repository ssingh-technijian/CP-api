using CpApis.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace CpApis.Api.Controllers;

[ApiController]
[Route("api/catalog")]
public sealed class CatalogController : ControllerBase
{
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
}
