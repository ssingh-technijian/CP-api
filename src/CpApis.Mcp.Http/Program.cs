using CpApis.Mcp.Core.Options;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using CpApis.Mcp.Http.Options;
using CpApis.Shared.Options;
using CpApis.Shared.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.Configure<RuntimeCatalogOptions>(builder.Configuration.GetSection("RuntimeCatalog"));
builder.Services.Configure<CpApisMcpOptions>(builder.Configuration.GetSection("CpApisMcp"));
builder.Services.Configure<CpApisMcpHttpOptions>(builder.Configuration.GetSection("CpApisMcpHttp"));

builder.Services.AddSingleton<ManifestCatalogService>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<StoredProcedureApiClient>();

var httpOptions = builder.Configuration.GetSection("CpApisMcpHttp").Get<CpApisMcpHttpOptions>() ?? new CpApisMcpHttpOptions();
var mcpPath = NormalizePath(httpOptions.Path);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = httpOptions.Stateless;
    })
    .WithTools<StoredProcedureTools>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "CpApis.Mcp.Http",
    transport = "http",
    mcpPath,
    health = "/health"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    transport = "http",
    mcpPath
}));

app.MapMcp(mcpPath);

app.Run();

static string NormalizePath(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return "/mcp";
    }

    return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
}
