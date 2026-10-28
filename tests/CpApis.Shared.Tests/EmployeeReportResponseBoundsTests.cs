using System.Text.Json;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class EmployeeReportResponseBoundsTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    [Fact]
    public void LongUnicodeNotesKeepValidJsonFullTotalsAndProgress()
    {
        var entries=Enumerable.Range(20,40).Select(i=>new{ticketEntryId=i,notes=new string('अ',3000),notesTruncated=false}).ToArray();
        var bounded=EmployeeReportResponseBounds.Bound(new(true,new{totalEntryCount=100,totalAllocatedWorkHours=123.5m,returnedCount=40,hasMore=true,entries},null),20);
        var text=JsonSerializer.Serialize(bounded,Json);Assert.True(text.Length<=7000);
        using var doc=JsonDocument.Parse(text);var data=doc.RootElement.GetProperty("data");
        var count=data.GetProperty("returnedCount").GetInt32();Assert.True(count>0&&count<40);
        Assert.Equal(100,data.GetProperty("totalEntryCount").GetInt32());Assert.Equal(123.5m,data.GetProperty("totalAllocatedWorkHours").GetDecimal());
        Assert.Equal(20+count,data.GetProperty("nextOffset").GetInt32());Assert.Equal(20,data.GetProperty("entries")[0].GetProperty("ticketEntryId").GetInt32());
        Assert.True(data.GetProperty("entries")[0].GetProperty("notesTruncated").GetBoolean());
    }
    [Fact]
    public void ActivityBudgetDoesNotChangeDistinctOrEventCounts()
    {
        var events=Enumerable.Range(0,50).Select(i=>new{ticketId=i,title=new string('中',200),beforeTitle=new string('中',200)}).ToArray();
        var bounded=EmployeeReportResponseBounds.Bound(new(true,new{closedTicketCount=12,closureEventCount=15,returnedCount=50,hasMore=false,events},null));
        var text=JsonSerializer.Serialize(bounded,Json);Assert.True(text.Length<=7000);
        var data=JsonSerializer.SerializeToElement(bounded.Data,Json);
        Assert.Equal(12,data.GetProperty("closedTicketCount").GetInt32());Assert.Equal(15,data.GetProperty("closureEventCount").GetInt32());
        Assert.True(data.GetProperty("hasMore").GetBoolean());Assert.Equal(data.GetProperty("returnedCount").GetInt32(),data.GetProperty("nextOffset").GetInt32());
    }
    [Fact]
    public void EmptyLaterPagePreservesReportTotalsAndTerminates()
    {
        var bounded=EmployeeReportResponseBounds.Bound(new(true,new{totalEntryCount=2,totalAllocatedWorkHours=10m,returnedCount=0,hasMore=false,entries=Array.Empty<object>()},null),5000);
        var data=JsonSerializer.SerializeToElement(bounded.Data,Json);
        Assert.Equal(2,data.GetProperty("totalEntryCount").GetInt32());Assert.Equal(10m,data.GetProperty("totalAllocatedWorkHours").GetDecimal());
        Assert.Equal(JsonValueKind.Null,data.GetProperty("nextOffset").ValueKind);
    }
    [Fact]
    public void AmbiguousLookupRemainsAnErrorWithChoices()
    {
        var employees=Enumerable.Range(0,20).Select(i=>new{directoryId=i+1,displayName=new string('中',200),office=new string('अ',100)}).ToArray();
        var bounded=EmployeeReportResponseBounds.Bound(new(false,new{matchStatus="ambiguous",employees},"Choose a full employee name."));
        var text=JsonSerializer.Serialize(bounded,Json);Assert.True(text.Length<=7000);Assert.False(bounded.Success);
        var data=JsonSerializer.SerializeToElement(bounded.Data,Json);Assert.Equal("ambiguous",data.GetProperty("matchStatus").GetString());
        Assert.True(data.GetProperty("employees").GetArrayLength()>0);Assert.True(data.GetProperty("refineSearchForMore").GetBoolean());
    }
}
