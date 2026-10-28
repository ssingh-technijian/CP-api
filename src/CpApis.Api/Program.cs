using CpApis.Api.Generated;
using CpApis.Api.OpenApi;
using CpApis.Api.Options;
using CpApis.Api.Services;
using CpApis.Shared.Options;
using CpApis.Shared.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = ResolveProjectContentRoot()
});

builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.Configure<RuntimeCatalogOptions>(builder.Configuration.GetSection("RuntimeCatalog"));
builder.Services.Configure<CardPointeOptions>(builder.Configuration.GetSection(CardPointeOptions.SectionName));
builder.Services.Configure<ClientPortalEmployeeApiOptions>(builder.Configuration.GetSection("ClientPortalEmployee"));
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<CardPointeGatewayClient>();

var azureAdSection = builder.Configuration.GetRequiredSection("AzureAd");
var azureAdInstance = (azureAdSection["Instance"] ?? "https://login.microsoftonline.com/").TrimEnd('/');
var azureAdTenantId = azureAdSection["TenantId"]?.Trim();
var azureAdClientId = azureAdSection["ClientId"]?.Trim();
var azureAdAudience = azureAdSection["Audience"]?.Trim();

if (string.IsNullOrWhiteSpace(azureAdTenantId) || string.IsNullOrWhiteSpace(azureAdClientId))
{
    throw new InvalidOperationException("AzureAd:TenantId and AzureAd:ClientId must be configured.");
}

azureAdAudience ??= $"api://{azureAdClientId}";

var authority = $"{azureAdInstance}/{azureAdTenantId}/v2.0";
var validIssuers = new[]
{
    $"{azureAdInstance}/{azureAdTenantId}/v2.0",
    $"{azureAdInstance}/{azureAdTenantId}/",
    $"https://sts.windows.net/{azureAdTenantId}/"
};

builder.Services.AddCors(options =>
{
    options.AddPolicy("Open", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

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
            ValidAudiences = new[]
            {
                azureAdClientId,
                azureAdAudience
            },
            ValidateLifetime = true,
            NameClaimType = "name"
        };
    });

builder.Services.AddAuthorization();

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "CP APIs",
        Version = "v1",
        Description = "Metadata-driven API for stored procedures discovered from the Client Portal codebase."
    });
    options.OperationFilter<ObjectRequestBodyOperationFilter>();
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste a Microsoft Entra access token from the existing Client Portal login."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddSingleton<ManifestCatalogService>();
builder.Services.AddSingleton<DatabaseExecutionService>();

var app = builder.Build();

app.UseCors("Open");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ClientPortalEmployeeApiBoundary>();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.MapControllers().RequireAuthorization();
ExplicitObjectEndpointMapper.Map(app);

app.Run();

static string ResolveProjectContentRoot()
{
    var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    if (File.Exists(Path.Combine(candidate, "appsettings.json")))
    {
        return candidate;
    }

    return Directory.GetCurrentDirectory();
}
