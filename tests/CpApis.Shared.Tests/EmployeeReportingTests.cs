using System.Reflection;
using System.Security.Claims;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using CpApis.Mcp.Http.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class EmployeeReportingTests
{
    private static TimeZoneInfo India=>TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
    private static TimeZoneInfo Pacific=>TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private static DateTimeOffset Now=>DateTimeOffset.Parse("2026-10-27T13:30:00Z");

    [Fact]
    public void LastTwoDaysIsFortyEightHoursRegardlessOfEmployeeOffice()
    {
        foreach(var zone in new[]{India,Pacific})
        {
            var w=EmployeeReportPeriods.Resolve("last_2_days",null,null,Now,zone);
            Assert.Equal(Now,w.UntilUtc);Assert.Equal(TimeSpan.FromHours(48),w.UntilUtc-w.FromUtc);
        }
    }

    [Fact]
    public void YesterdayUsesTheSubjectWorkDayRatherThanBrowserOrServerDate()
    {
        var india=EmployeeReportPeriods.Resolve("yesterday",null,null,Now,India);
        var us=EmployeeReportPeriods.Resolve("yesterday",null,null,Now,Pacific);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T18:30:00Z"),india.FromUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T18:30:00Z"),india.UntilUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T07:00:00Z"),us.FromUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T07:00:00Z"),us.UntilUtc);
    }

    [Fact]
    public void EmployeeMidnightCanBeADifferentCalendarDateFromUtc()
    {
        var now=DateTimeOffset.Parse("2026-10-27T18:40:00Z");
        var w=EmployeeReportPeriods.Resolve("today",null,null,now,India);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T18:30:00Z"),w.FromUtc);
        Assert.Equal(TimeSpan.FromMinutes(10),w.UntilUtc-w.FromUtc);
    }

    [Theory]
    [InlineData("2026-11-02T12:00:00Z",25)]
    [InlineData("2026-03-09T12:00:00Z",23)]
    public void NativePacificCalendarDaysAccountForDaylightSaving(string now,int hours)
    {
        var w=EmployeeReportPeriods.Resolve("yesterday",null,null,DateTimeOffset.Parse(now),Pacific);
        Assert.Equal(TimeSpan.FromHours(hours),w.UntilUtc-w.FromUtc);
    }

    [Fact]
    public void RawNativeClockDoesNotPretendToDisambiguateRepeatedDstHour()
    {
        var w=new EmployeeReportWindow(DateTimeOffset.Parse("2026-11-01T08:30:00Z"),DateTimeOffset.Parse("2026-11-02T08:00:00Z"),Pacific.Id,"custom");
        Assert.Throws<EmployeeActionException>(()=>w.NativeFrom(Pacific));
    }

    [Theory]
    [InlineData("this_week","2026-10-25T18:30:00Z","2026-10-27T13:30:00Z")]
    [InlineData("last_week","2026-10-18T18:30:00Z","2026-10-25T18:30:00Z")]
    [InlineData("this_month","2026-09-30T18:30:00Z","2026-10-27T13:30:00Z")]
    [InlineData("last_month","2026-08-31T18:30:00Z","2026-09-30T18:30:00Z")]
    public void NamedCalendarPeriodsHaveExplicitHalfOpenBounds(string period,string from,string until)
    {
        var w=EmployeeReportPeriods.Resolve(period,null,null,Now,India);
        Assert.Equal(DateTimeOffset.Parse(from),w.FromUtc);Assert.Equal(DateTimeOffset.Parse(until),w.UntilUtc);
    }

    [Fact]
    public void InclusiveCustomDaysAndLeapDayArePreserved()
    {
        var w=EmployeeReportPeriods.Resolve("custom","2024-02-28","2024-02-29",DateTimeOffset.Parse("2024-03-02T00:00:00Z"),India);
        Assert.Equal(TimeSpan.FromDays(2),w.UntilUtc-w.FromUtc);
        Assert.Equal(DateTimeOffset.Parse("2024-02-29T18:30:00Z"),w.UntilUtc);
    }

    [Theory]
    [InlineData("unknown",null,null)]
    [InlineData("yesterday","2026-10-26","2026-10-26")]
    [InlineData("custom","2026-10-27","2026-10-26")]
    [InlineData("custom","2026-09-01","2026-10-27")]
    [InlineData("custom","2026-10-01","2026-10-28")]
    [InlineData("custom","2026-10-32","2026-10-32")]
    [InlineData("custom",null,null)]
    public void InvalidOrFutureDateInputsDoNotBecomeAnEmptyVerifiedReport(string period,string? from,string? through)=>
        Assert.Throws<EmployeeActionException>(()=>EmployeeReportPeriods.Resolve(period,from,through,Now,India));

    [Theory]
    [InlineData("1272","Completed",true)]
    [InlineData("1275","Closed",true)]
    [InlineData("  +01275 ",null,true)]
    [InlineData("legacy","Closed",true)]
    [InlineData("1262","In Progress",false)]
    [InlineData("1262","Closed",false)]
    [InlineData("1272","In Progress",true)]
    [InlineData(null,null,false)]
    public void ClosedQuestionsCoverBothNativeTerminalStatuses(string? status,string? text,bool expected)=>
        Assert.Equal(expected,EmployeeReportPeriods.IsClosed(status,text));

    [Theory]
    [InlineData("client_portal_find_employees")]
    [InlineData("client_portal_ticket_activity")]
    [InlineData("client_portal_get_eod")]
    [InlineData("client_portal_get_ticket_history")]
    public void ReportingNamesAreEmployeeReadsWithActionableDescriptions(string name)
    {
        var method=typeof(ClientPortalEmployeeReadTools).GetMethods().Single(m=>m.GetCustomAttribute<McpServerToolAttribute>()?.Name==name);
        var tool=method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.True(tool.ReadOnly);Assert.False(tool.Destructive);Assert.True(ClientPortalEmployeeBoundary.IsEmployeeTool(name));
        var description=method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
        Assert.True(description.Length>100);Assert.DoesNotContain(method.GetParameters(),p=>p.Name is "actor" or "userId" or "role" or "tenantId" or "sql" or "token");
    }

    [Fact]
    public async Task AllNewReportsDenyUnauthenticatedCallsBeforeSql()
    {
        var context=new ClientPortalEmployeeContext(new MissingPrincipal(),Microsoft.Extensions.Options.Options.Create(new ClientPortalEmployeeOptions{Enabled=false}));
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:client-portal","invalid connection text, must never be opened"}}).Build();
        var tools=new ClientPortalEmployeeReadTools(context,config);
        var reports=new[]{await tools.FindEmployeesAsync("Rahul"),await tools.TicketActivityAsync(),await tools.EodAsync("Rahul"),await tools.TicketHistoryAsync(1)};
        Assert.All(reports,r=>{Assert.False(r.Success);Assert.Null(r.Data);Assert.NotEmpty(r.Error!);});
    }

    [Fact]
    public void SelfAndExplicitDenyKeepCoworkerReportsOutOfScope()
    {
        var self=new[]{new PortalPermission("Allow","Self")};
        Assert.True(ClientPortalPermissionPolicy.Allows(self,true));Assert.False(ClientPortalPermissionPolicy.Allows(self,false));
        Assert.False(ClientPortalPermissionPolicy.Allows(new[]{new PortalPermission("Allow","All"),new PortalPermission("Deny","Self")},true));
        Assert.False(ClientPortalPermissionPolicy.Allows(Array.Empty<PortalPermission>(),true));
    }

    [Fact]
    public void EodMenuAddsItsOwnRouteAndPreservesExistingMappings()
    {
        Assert.Equal("/Technijian/EODView",ClientPortalMenuAccess.Route(PortalMenu.EodView));
        Assert.Equal("/Ticket/Search",ClientPortalMenuAccess.Route(PortalMenu.TicketSearch));
        Assert.Equal("/Ticket/Timesheet",ClientPortalMenuAccess.Route(PortalMenu.Timesheet));
        Assert.Equal("/Reports/InvoiceUI",ClientPortalMenuAccess.Route(PortalMenu.InvoiceReports));
    }

    private sealed class MissingPrincipal:IEmployeePrincipalAccessor {public ClaimsPrincipal? GetPrincipal()=>null;}
}
