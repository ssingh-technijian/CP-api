using CpApis.Mcp.Core.Options;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using CpApis.Shared.Options;
using CpApis.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddDebug();

builder.Services.Configure<RuntimeCatalogOptions>(builder.Configuration.GetSection("RuntimeCatalog"));
builder.Services.Configure<CpApisMcpOptions>(builder.Configuration.GetSection("CpApisMcp"));
builder.Services.AddSingleton<IApiAuthorizationHeaderAccessor, NullApiAuthorizationHeaderAccessor>();

builder.Services.AddSingleton<ManifestCatalogService>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<StoredProcedureApiClient>(sp =>
    new StoredProcedureApiClient(
        new HttpClient(),
        sp.GetRequiredService<IOptions<CpApisMcpOptions>>(),
        sp.GetRequiredService<IApiAuthorizationHeaderAccessor>()));
builder.Services.AddSingleton(sp =>
    new CardPointeApiClient(
        new HttpClient(),
        sp.GetRequiredService<IOptions<CpApisMcpOptions>>(),
        sp.GetRequiredService<IApiAuthorizationHeaderAccessor>()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<StoredProcedureTools>()
    .WithTools<CardPointeTools>();

await builder.Build().RunAsync();
