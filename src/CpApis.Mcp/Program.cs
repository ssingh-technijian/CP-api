using CpApis.Mcp.Core.Options;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using CpApis.Shared.Options;
using CpApis.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddDebug();

builder.Services.Configure<RuntimeCatalogOptions>(builder.Configuration.GetSection("RuntimeCatalog"));
builder.Services.Configure<CpApisMcpOptions>(builder.Configuration.GetSection("CpApisMcp"));

builder.Services.AddSingleton<ManifestCatalogService>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<StoredProcedureApiClient>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<StoredProcedureTools>();

await builder.Build().RunAsync();
