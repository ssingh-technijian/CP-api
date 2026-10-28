using System.Net;
using System.Net.Http.Json;
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
using Microsoft.Extensions.Options;
using Xunit;

namespace CpApis.Shared.Tests;

// These tests use synthetic manifests and an in-process handler. No network/DB calls.
public sealed class StoredProcedureWorkflowSafetyTests
{
    public static TheoryData<string> MisclassifiedWrites => new()
    {
        "sp_Ticket_Upd", "sp_TicketEntry_Del", "stp_xml_Tkt_API_CreateV3",
        "stp_Inv_Org_Clt_Inv_Weekly_Generate", "stp_Inv_Org_Clt_Inv_Monthly_Generate",
        "stp_Change_Password"
    };

    [Theory]
    [MemberData(nameof(MisclassifiedWrites))]
    public async Task StaleManifest_CannotExecuteKnownMutationsWithoutConfirmation(string name)
    {
        using var fixture = new Fixture(name, manifestWrite: false);
        var result = await fixture.Tools.ExecuteStoredProcedureAsync("client-portal", "dbo", name,
            new() { ["TicketID"] = JsonSerializer.SerializeToElement(7) }, allowWrite: false);
        Assert.False(result.Executed);
        Assert.Equal(0, fixture.Handler.RequestCount);
    }

    [Theory]
    [MemberData(nameof(MisclassifiedWrites))]
    public void StaleManifest_CatalogDoesNotAdvertiseKnownMutationAsRead(string name)
    {
        using var fixture = new Fixture(name, manifestWrite: false);
        Assert.True(fixture.Tools.DescribeStoredProcedure("client-portal", "dbo", name).IsWriteOperation);
        Assert.Empty(fixture.Tools.ListStoredProcedures(includeWriteProcedures: false).Items);
        Assert.True(Assert.Single(fixture.Tools.ListStoredProcedures().Items).IsWriteOperation);
    }

    [Theory]
    [InlineData("stp_Technijian_User_List")]
    [InlineData("stp_xml_Ticket_DSB_MyOpnTkt_List_Get_V2")]
    [InlineData("stp_Get_DayWeek")]
    public async Task OrdinaryReadProcedures_KeepExistingBehavior(string name)
    {
        using var fixture = new Fixture(name, manifestWrite: false);
        var result = await fixture.Tools.ExecuteStoredProcedureAsync("client-portal", "dbo", name);
        Assert.True(result.Executed);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.Single(fixture.Tools.ListStoredProcedures(includeWriteProcedures: false).Items);
    }

    [Fact]
    public async Task ExplicitConfirmation_ExecutesExactlyOnceAndPreservesParameters()
    {
        using var fixture = new Fixture("stp_Test_Save", manifestWrite: true);
        var result = await fixture.Tools.ExecuteStoredProcedureAsync("client-portal", "dbo", "stp_Test_Save",
            new() { ["TicketID"] = JsonSerializer.SerializeToElement(7), ["Title"] = JsonSerializer.SerializeToElement("QA & <owned>") }, true);
        Assert.True(result.Executed);
        Assert.True(result.WriteConfirmationBypassed);
        Assert.Equal(1, fixture.Handler.RequestCount);
        using var body = JsonDocument.Parse(fixture.Handler.Body!);
        Assert.Equal(7, body.RootElement.GetProperty("parameters").GetProperty("TicketID").GetInt32());
        Assert.Equal("QA & <owned>", body.RootElement.GetProperty("parameters").GetProperty("Title").GetString());
    }

    [Fact]
    public async Task ManifestWriteFlag_IsNeverDowngradedByAnInnocentName()
    {
        using var fixture = new Fixture("stp_GetSomething", manifestWrite: true);
        var result = await fixture.Tools.ExecuteStoredProcedureAsync("client-portal", "dbo", "stp_GetSomething");
        Assert.False(result.Executed);
        Assert.Equal(0, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task FailedHttpCall_DoesNotReportSuccessOrReplay()
    {
        using var fixture = new Fixture("stp_GetSomething", false);
        fixture.Handler.Status = HttpStatusCode.Conflict;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Tools.ExecuteStoredProcedureAsync("client-portal", "dbo", "stp_GetSomething"));
        Assert.Equal(1, fixture.Handler.RequestCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CpMcpWorkflowQA-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        public Handler Handler { get; } = new();
        public StoredProcedureTools Tools { get; }
        public Fixture(string name, bool manifestWrite)
        {
            Directory.CreateDirectory(_root);
            var item = new DatabaseObjectManifest
            {
                Key = $"client-portal:dbo.{name.ToLowerInvariant()}:stored-procedure",
                Name = name, Schema = "dbo", DatabaseAlias = "client-portal", DatabaseName = "SYNTHETIC_ONLY",
                Module = "QA", Kind = "stored-procedure", IsIncluded = true, IsActiveRoot = true, IsWriteOperation = manifestWrite
            };
            File.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(new DatabaseInventoryManifest { Objects = [item] }));
            var catalog = new ManifestCatalogService(new Environment { ContentRootPath = _root },
                Microsoft.Extensions.Options.Options.Create(new RuntimeCatalogOptions { ManifestPath = "manifest.json" }), NullLogger<ManifestCatalogService>.Instance);
            var options = Microsoft.Extensions.Options.Options.Create(new CpApisMcpOptions { ApiBaseUrl = "https://synthetic.invalid/", RequireWriteConfirmation = true });
            _http = new HttpClient(Handler);
            Tools = new StoredProcedureTools(catalog, new StoredProcedureApiClient(_http, options, new NullApiAuthorizationHeaderAccessor()), options);
        }
        public void Dispose()
        {
            _http.Dispose();
            File.Delete(Path.Combine(_root, "manifest.json"));
            Directory.Delete(_root);
        }
    }
    public sealed class Handler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status) { Content = JsonContent.Create(new CommandExecutionResponse()) };
        }
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "SyntheticQA";
        public string ApplicationName { get; set; } = "SyntheticQA";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
