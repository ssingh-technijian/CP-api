using CpApis.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace CpApis.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    private readonly ManifestCatalogService _catalogService;

    public SystemController(ManifestCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        var manifest = _catalogService.GetManifest();
        return Ok(new
        {
            status = "ok",
            generatedAtUtc = manifest.GeneratedAtUtc,
            databaseCount = manifest.Databases.Count,
            objectCount = manifest.Objects.Count(item => item.IsIncluded),
            swagger = "/swagger"
        });
    }
}
