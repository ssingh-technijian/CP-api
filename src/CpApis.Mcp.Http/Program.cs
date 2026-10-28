using CpApis.Mcp.Core.Options;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using CpApis.Mcp.Http.Options;
using CpApis.Mcp.Http.Services;
using CpApis.Shared.Options;
using CpApis.Shared.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

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
builder.Services.Configure<ClientPortalEmployeeOptions>(builder.Configuration.GetSection("ClientPortalEmployee"));
builder.Services.Configure<ClientPortalEmployeeActionOptions>(builder.Configuration.GetSection("ClientPortalEmployeeActions"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IApiAuthorizationHeaderAccessor, HttpContextAuthorizationHeaderAccessor>();
builder.Services.AddSingleton<IEmployeePrincipalAccessor, HttpEmployeePrincipalAccessor>();
builder.Services.AddSingleton<ClientPortalEmployeeContext>();
builder.Services.AddSingleton<IEmployeeActionDatabase, SqlEmployeeActionDatabase>();
builder.Services.AddSingleton<IEmployeeActionExecutor, ClientPortalNativeActionExecutor>();
builder.Services.AddSingleton<ClientPortalEmployeeActionService>();
builder.Services.AddSingleton<ILogger<ClientPortalEmployeeActionTools>>(_ => new EmployeeActionFileLogger(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Technijian","ClientPortalMcp","Diagnostics")));

var azureAdSection = builder.Configuration.GetRequiredSection("AzureAd");
var azureAdInstance = (azureAdSection["Instance"] ?? "https://login.microsoftonline.com/").TrimEnd('/');
var azureAdTenantId = azureAdSection["TenantId"]?.Trim();
var azureAdClientId = azureAdSection["ClientId"]?.Trim();
var azureAdAudience = azureAdSection["Audience"]?.Trim();
var employeeResourceClientId = builder.Configuration["ClientPortalEmployee:ResourceClientId"]?.Trim();

if (string.IsNullOrWhiteSpace(azureAdTenantId) || string.IsNullOrWhiteSpace(azureAdClientId))
{
    throw new InvalidOperationException("AzureAd:TenantId and AzureAd:ClientId must be configured.");
}

azureAdAudience ??= $"api://{azureAdClientId}";
var validAudiences = new List<string> { azureAdClientId, azureAdAudience };
if (Guid.TryParse(employeeResourceClientId, out var employeeResource) && employeeResource != Guid.Empty
    && employeeResource.ToString("D") != azureAdClientId)
{
    validAudiences.Add(employeeResource.ToString("D"));
    validAudiences.Add($"api://{employeeResource:D}");
}
var authority = $"{azureAdInstance}/{azureAdTenantId}/v2.0";
var validIssuers = new[]
{
    $"{azureAdInstance}/{azureAdTenantId}/v2.0",
    $"{azureAdInstance}/{azureAdTenantId}/",
    $"https://sts.windows.net/{azureAdTenantId}/"
};

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            ValidateAudience = true,
            ValidAudiences = validAudiences,
            ValidateLifetime = true,
            NameClaimType = "name"
        };
    });
builder.Services.AddAuthorization();

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

var httpOptions = builder.Configuration.GetSection("CpApisMcpHttp").Get<CpApisMcpHttpOptions>() ?? new CpApisMcpHttpOptions();
var mcpPath = NormalizePath(httpOptions.Path);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = httpOptions.Stateless;
    })
    .WithTools<StoredProcedureTools>()
    .WithTools<CardPointeTools>()
    .WithTools<ClientPortalEmployeeReadTools>()
    .WithTools<ClientPortalEmployeeActionTools>()
    .WithTools<ClientPortalEmployeeDetailTools>()
    .WithRequestFilters(filters =>
    {
        filters.AddListToolsFilter(ClientPortalEmployeeBoundary.FilterList);
        filters.AddCallToolFilter(ClientPortalEmployeeBoundary.FilterCall);
    });

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments(mcpPath)
        && (context.Request.Headers.Authorization.Count == 0
            || context.User.Identity?.IsAuthenticated is not true))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }

    await next();
});

app.UseMiddleware<ClientPortalEmployeeBoundary>();

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

app.MapMcp(mcpPath).RequireAuthorization();
app.MapEmployeeActions();

app.Run();

static string NormalizePath(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return "/mcp";
    }

    return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
}
