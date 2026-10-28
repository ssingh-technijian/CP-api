using System.Net;
using System.Text.Json;
using CpApis.Mcp.Core.Options;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using CpApis.Shared.Inventory;
using CpApis.Shared.Options;
using CpApis.Shared.Runtime;
using CpApis.Shared.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class StaffDirectorySafetyTests
{
    [Fact]
    public async Task StaffLookup_ReturnsOnlyReviewedIdentityFieldsAndNoOutputSecrets()
    {
        using var f = new Fixture();
        var result = await f.Call(new() { ["GetInActive"] = JsonSerializer.SerializeToElement(false) });
        Assert.True(result.Executed);
        var row = Assert.Single(Assert.Single(result.Response!.ResultSets).Rows);
        Assert.Equal(new[] { "Email", "IsActive", "IsDelete", "UserID" }, row.Keys.OrderBy(x => x).ToArray());
        Assert.Equal(7, ((JsonElement)row["UserID"]!).GetInt32());
        Assert.Equal("person@mcpqa.invalid", ((JsonElement)row["Email"]!).GetString());
        Assert.Empty(result.Response.OutputParameters);
        Assert.DoesNotContain("sensitive-placeholder", JsonSerializer.Serialize(result));
        Assert.Equal(1, f.Handler.Calls);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("\"true\"")]
    [InlineData("\"1\"")]
    public async Task InactiveLookup_StopsBeforeAnyApiRequest(string json)
    {
        using var f = new Fixture();
        var result = await f.Call(new() { ["GetInActive"] = JsonSerializer.Deserialize<JsonElement>(json) });
        Assert.False(result.Executed);
        Assert.Contains("client filter", result.Message);
        Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public async Task DuplicateNormalizedParameters_AreRejectedBeforeHttp()
    {
        using var f = new Fixture();
        var result = await f.Call(new() { ["GetInActive"] = JsonSerializer.SerializeToElement(false), ["@getinactive"] = JsonSerializer.SerializeToElement(false) });
        Assert.False(result.Executed);
        Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public async Task UnexpectedStaffSchema_FailsWithoutReturningRawRows()
    {
        using var f = new Fixture();
        f.Handler.OmitEmail = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Call([]));
        Assert.DoesNotContain("sensitive-placeholder", error.Message);
        Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task SameNameInAnotherDatabase_RetainsTheExistingResponse()
    {
        using var f = new Fixture("unrelated");
        var result = await f.Call(new() { ["GetInActive"] = JsonSerializer.SerializeToElement(true) });
        Assert.True(result.Executed);
        Assert.True(Assert.Single(Assert.Single(result.Response!.ResultSets).Rows).ContainsKey("Password"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CpStaffQA-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly StoredProcedureTools _tools;
        private readonly string _alias;
        public Handler Handler { get; } = new();
        public Fixture(string alias = "client-portal")
        {
            _alias = alias;
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(new DatabaseInventoryManifest
            {
                Objects = [new DatabaseObjectManifest { Key = $"{alias}:dbo.stp_technijian_user_list:stored-procedure", DatabaseAlias = alias, Schema = "dbo", Name = "stp_Technijian_User_List", Module = "QA", Kind = "stored-procedure", IsIncluded = true, IsActiveRoot = true }]
            }));
            var options = Microsoft.Extensions.Options.Options.Create(new CpApisMcpOptions { ApiBaseUrl = "https://fixture.invalid/", RequireWriteConfirmation = true });
            var catalog = new ManifestCatalogService(new TestEnvironment { ContentRootPath = _root }, Microsoft.Extensions.Options.Options.Create(new RuntimeCatalogOptions { ManifestPath = "manifest.json" }), NullLogger<ManifestCatalogService>.Instance);
            _http = new HttpClient(Handler);
            _tools = new StoredProcedureTools(catalog, new StoredProcedureApiClient(_http, options, new EmptyAuthorization()), options);
        }
        public Task<CpApis.Mcp.Core.Models.StoredProcedureExecutionResult> Call(Dictionary<string, JsonElement> p) => _tools.ExecuteStoredProcedureAsync(_alias, "dbo", "stp_Technijian_User_List", p);
        public void Dispose() { _http.Dispose(); File.Delete(Path.Combine(_root, "manifest.json")); Directory.Delete(_root); }
    }
    private sealed class EmptyAuthorization : IApiAuthorizationHeaderAccessor { public string? GetAuthorizationHeader() => null; }
    public sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool OmitEmail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var row = new Dictionary<string, object?> { ["UserID"] = 7, ["Email"] = "person@mcpqa.invalid", ["IsActive"] = true, ["IsDelete"] = false, ["Password"] = "sensitive-placeholder", ["HomePhone"] = "sensitive-placeholder" };
            if (OmitEmail) row.Remove("Email");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new CommandExecutionResponse
                {
                    DatabaseAlias = "client-portal", Schema = "dbo", Name = "stp_Technijian_User_List", Kind = "stored-procedure",
                    ResultSets = [new ResultSetPayload { Index = 0, RowCount = 1, Rows = [row] }], OutputParameters = new() { ["UnexpectedPassword"] = "sensitive-placeholder" }
                }))
            });
        }
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "QA";
        public string ApplicationName { get; set; } = "QA";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
