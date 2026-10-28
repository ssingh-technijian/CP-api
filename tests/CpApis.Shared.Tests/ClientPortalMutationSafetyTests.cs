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

public sealed class ClientPortalMutationSafetyTests
{
    [Fact]
    public async Task PartialTicketEdit_UsesAuthenticatedPatchWithoutSendingOmittedFields()
    {
        using var f = new Fixture("sp_Ticket_Upd");
        var result = await f.Call(new() { ["TicketID"] = JsonSerializer.SerializeToElement(7), ["Title"] = JsonSerializer.SerializeToElement("Updated & <safe>") });
        Assert.True(result.Executed);
        Assert.Equal(HttpMethod.Patch, f.Handler.Method);
        Assert.Equal("/api/tickets/7", f.Handler.Path);
        Assert.Equal("Bearer synthetic-user-token", f.Handler.Authorization);
        using var body = JsonDocument.Parse(f.Handler.Body!);
        Assert.Equal("Updated & <safe>", body.RootElement.GetProperty("Title").GetString());
        Assert.False(body.RootElement.TryGetProperty("Description", out _));
        Assert.False(body.RootElement.TryGetProperty("TicketID", out _));
        Assert.Single(f.Handler.Calls);
        Assert.Single(result.Response!.ResultSets);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("\"all\"")]
    public async Task InvalidTicketId_StopsBeforeHttp(string json)
    {
        using var f = new Fixture("sp_Ticket_Upd");
        var result = await f.Call(new() { ["TicketID"] = JsonSerializer.Deserialize<JsonElement>(json), ["Title"] = JsonSerializer.SerializeToElement("x") });
        Assert.False(result.Executed);
        Assert.Empty(f.Handler.Calls);
    }

    [Fact]
    public async Task DuplicateNormalizedId_StopsBeforeHttp()
    {
        using var f = new Fixture("sp_Ticket_Upd");
        var result = await f.Call(new() { ["TicketID"] = JsonSerializer.SerializeToElement(7), ["@ticketid"] = JsonSerializer.SerializeToElement(8), ["Title"] = JsonSerializer.SerializeToElement("x") });
        Assert.False(result.Executed);
        Assert.Empty(f.Handler.Calls);
    }

    [Fact]
    public async Task ConfirmationStillPrecedesThePartialPatch()
    {
        using var f = new Fixture("sp_Ticket_Upd");
        var result = await f.Call(new() { ["TicketID"] = JsonSerializer.SerializeToElement(7), ["Title"] = JsonSerializer.SerializeToElement("x") }, allowWrite: false);
        Assert.False(result.Executed);
        Assert.Empty(f.Handler.Calls);
    }

    [Theory]
    [InlineData("sp_TicketEntry_Del")]
    [InlineData("stp_TktEntry_Delete")]
    [InlineData("stp_TktEntry_Delete_V2")]
    public async Task UnsafeLegacyDelete_CannotReachTheApiEvenWithWriteConfirmation(string procedure)
    {
        using var f = new Fixture(procedure);
        var result = await f.Call(new() { ["TicketEntryID"] = JsonSerializer.SerializeToElement(7) });
        Assert.False(result.Executed);
        Assert.Contains("approval", result.Message);
        Assert.Empty(f.Handler.Calls);
    }

    [Fact]
    public async Task NativePatchFailure_IsNotReportedSuccessfulOrRetried()
    {
        using var f = new Fixture("sp_Ticket_Upd");
        f.Handler.Status = HttpStatusCode.Conflict;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Call(new() { ["TicketID"] = JsonSerializer.SerializeToElement(7), ["Title"] = JsonSerializer.SerializeToElement("x") }));
        Assert.Single(f.Handler.Calls);
    }

    [Fact]
    public async Task FullLegacyUpdate_RetainsItsOriginalProcedureContract()
    {
        using var f = new Fixture("sp_Ticket_Upd");
        var p = new Dictionary<string, JsonElement>();
        foreach (var name in new[] { "TicketID", "Requestor", "AssignToUsername", "Title", "Description", "Priority", "Status", "Estimated_Hours", "RequestDate", "Internal_Notes", "Description_RTF" }) p[name] = JsonSerializer.SerializeToElement<object?>(null);
        p["TicketID"] = JsonSerializer.SerializeToElement(7);
        var result = await f.Call(p);
        Assert.True(result.Executed);
        Assert.Equal(HttpMethod.Post, f.Handler.Method);
        Assert.Contains("sp_Ticket_Upd/execute", f.Handler.Path);
    }

    [Fact]
    public async Task NativeIdMappings_AreTypedAndDoNotCreateMissingFields()
    {
        using var f = new Fixture("sp_Ticket_Upd");
        var result = await f.Call(new() { ["@TicketID"] = JsonSerializer.SerializeToElement("7"), ["@Requestor"] = JsonSerializer.SerializeToElement(9), ["Status"] = JsonSerializer.SerializeToElement(1259) });
        Assert.True(result.Executed);
        using var body = JsonDocument.Parse(f.Handler.Body!);
        Assert.Equal(9, body.RootElement.GetProperty("Requestor_DirID").GetInt32());
        Assert.Equal("1259", body.RootElement.GetProperty("Status").GetString());
        Assert.False(body.RootElement.TryGetProperty("Description", out _));
    }

    [Fact]
    public async Task NumericPartialAssignee_IsRejectedBeforeHttp()
    {
        using var f = new Fixture("sp_Ticket_Upd");
        var result = await f.Call(new() { ["TicketID"] = JsonSerializer.SerializeToElement(7), ["AssignToUsername"] = JsonSerializer.SerializeToElement("9") });
        Assert.False(result.Executed);
        Assert.Empty(f.Handler.Calls);
    }

    [Fact]
    public async Task OtherDatabaseWithTheSameProcedureName_IsNotRerouted()
    {
        using var f = new Fixture("sp_Ticket_Upd", "unrelated");
        var result = await f.Call(new() { ["TicketID"] = JsonSerializer.SerializeToElement(7), ["Title"] = JsonSerializer.SerializeToElement("x") });
        Assert.True(result.Executed);
        Assert.Equal(HttpMethod.Post, f.Handler.Method);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CpMutationQA-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly string _name;
        private readonly string _alias;
        private readonly StoredProcedureTools _tools;
        public CaptureHandler Handler { get; } = new();
        public Fixture(string name, string alias = "client-portal")
        {
            _name = name; _alias = alias;
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(new DatabaseInventoryManifest
            {
                Objects = [new DatabaseObjectManifest { Key = $"{alias}:dbo.{name.ToLowerInvariant()}:stored-procedure", DatabaseAlias = alias, Schema = "dbo", Name = name, Module = "QA", Kind = "stored-procedure", IsIncluded = true, IsActiveRoot = true }]
            }));
            var catalog = new ManifestCatalogService(new TestEnvironment { ContentRootPath = _root }, Microsoft.Extensions.Options.Options.Create(new RuntimeCatalogOptions { ManifestPath = "manifest.json" }), NullLogger<ManifestCatalogService>.Instance);
            var options = Microsoft.Extensions.Options.Options.Create(new CpApisMcpOptions { ApiBaseUrl = "https://fixture.invalid/", RequireWriteConfirmation = true });
            _http = new HttpClient(Handler);
            _tools = new StoredProcedureTools(catalog, new StoredProcedureApiClient(_http, options, new HeaderAccessor()), options);
        }
        public Task<CpApis.Mcp.Core.Models.StoredProcedureExecutionResult> Call(Dictionary<string, JsonElement> p, bool allowWrite = true) => _tools.ExecuteStoredProcedureAsync(_alias, "dbo", _name, p, allowWrite);
        public void Dispose() { _http.Dispose(); File.Delete(Path.Combine(_root, "manifest.json")); Directory.Delete(_root); }
    }
    private sealed class HeaderAccessor : IApiAuthorizationHeaderAccessor { public string? GetAuthorizationHeader() => "Bearer synthetic-user-token"; }
    public sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method; Path = request.RequestUri!.AbsolutePath; Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct); Calls.Add(Path);
            return new HttpResponseMessage(Status) { Content = new StringContent(Method == HttpMethod.Patch ? "{\"TicketID\":7,\"Title\":\"Updated\"}" : "{}") };
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
