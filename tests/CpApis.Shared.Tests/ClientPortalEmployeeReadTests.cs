using CpApis.Mcp.Core.Tools;
using CpApis.Mcp.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class ClientPortalEmployeeReadTests
{
    [Fact]
    public async Task InvalidIdentityIsRejectedBeforeDatabaseConfigurationIsUsed()
    {
        var employees = new ClientPortalEmployeeContext(new AnonymousAccessor(),
            Microsoft.Extensions.Options.Options.Create(new ClientPortalEmployeeOptions { Enabled = true }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:client-portal"] = "intentionally invalid configuration; must never be used" }).Build();
        var tools = new ClientPortalEmployeeReadTools(employees, config);
        var result = await tools.WhoAmIAsync(default);
        Assert.False(result.Success); Assert.Null(result.Data); Assert.DoesNotContain("configuration", result.Error!);
    }

    [Fact]
    public void NativeCountIsCompleteEvenWhenPageIsLimitedAndFieldsAreProjected()
    {
        var xml = "<Root>" + string.Join("", Enumerable.Range(1, 4).Select(id => $"<TicketViewModel><ID>{id}</ID><Title>Owned &amp; safe</Title><Client>Test</Client><Status>New</Status><Password>never-return</Password></TicketViewModel>")) + "</Root>";
        var page = ClientPortalEmployeeReadTools.ParseTicketPage(xml, 2);
        Assert.Equal(4, page.TotalCount); Assert.Equal(2, page.ReturnedCount); Assert.Equal("Owned & safe", page.Tickets[0].Title);
        Assert.DoesNotContain("never-return", System.Text.Json.JsonSerializer.Serialize(page));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Unexpected/>")]
    [InlineData("<Root><Other/></Root>")]
    [InlineData("<Root>Permission denied</Root>")]
    [InlineData("<Root success='false'/>")]
    [InlineData("<Root><TicketViewModel><ID><Other>1</Other></ID></TicketViewModel></Root>")]
    [InlineData("<Root><TicketViewModel><ID>0</ID></TicketViewModel></Root>")]
    [InlineData("<Root><TicketViewModel><ID>1</ID><ID>2</ID></TicketViewModel></Root>")]
    [InlineData("<Root><TicketViewModel><ID>1</ID></TicketViewModel><TicketViewModel><ID>1</ID></TicketViewModel></Root>")]
    public void RejectsMissingMalformedAndDuplicateNativeResults(string xml) =>
        Assert.Throws<InvalidDataException>(() => ClientPortalEmployeeReadTools.ParseTicketPage(xml, 10));

    [Fact]
    public void DtdIsNeverResolved() => Assert.Throws<System.Xml.XmlException>(() =>
        ClientPortalEmployeeReadTools.ParseTicketPage("<!DOCTYPE Root [<!ENTITY x SYSTEM 'file:///never'>]><Root>&x;</Root>", 10));

    [Fact]
    public void EmptyNativeRootIsAVerifiedZero() => Assert.Equal(0, ClientPortalEmployeeReadTools.ParseTicketPage("<Root/>", 10).TotalCount);

    [Theory]
    [InlineData("2026-10-01", "2026-10-31", true)]
    [InlineData("2026-10-01", "2026-11-01", false)]
    [InlineData("2026-10-23", "2026-10-23", true)]
    [InlineData("2026-10-24", "2026-10-23", false)]
    [InlineData("10/23/2026", "2026-10-23", false)]
    [InlineData("2026-02-30", "2026-03-01", false)]
    [InlineData("9999-12-31", "9999-12-31", false)]
    public void RequiresBoundedUnambiguousNativeDates(string start, string end, bool expected) =>
        Assert.Equal(expected, ClientPortalEmployeeReadTools.TryDateRange(start, end, out _, out _));

    private sealed class AnonymousAccessor : IEmployeePrincipalAccessor
    {
        public System.Security.Claims.ClaimsPrincipal? GetPrincipal() => null;
    }
}
