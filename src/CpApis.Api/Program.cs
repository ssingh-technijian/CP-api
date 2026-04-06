using CpApis.Api.Generated;
using CpApis.Api.OpenApi;
using CpApis.Api.Services;
using CpApis.Shared.Options;
using CpApis.Shared.Services;

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
});

builder.Services.AddSingleton<ManifestCatalogService>();
builder.Services.AddSingleton<DatabaseExecutionService>();

var app = builder.Build();

app.UseCors("Open");
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapControllers();
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
