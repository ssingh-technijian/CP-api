using System.Text.Json;
using System.Xml.Linq;
using CpApis.Mcp.Core.Services;
using CpApis.Mcp.Core.Tools;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class EmployeeCollectionPagingTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};

    [Fact]
    public void MissingInvoiceAmountsStayNullWhileRecordedZeroStaysNumeric()
    {
        var unknown=new EmployeeInvoice(1,2,"QA","Invoice",null,"Draft",null,null,false,false);
        var knownZero=unknown with{NativeTotal=0m,NativePaid=0m};
        var missing=JsonSerializer.SerializeToElement(unknown,Json);var zero=JsonSerializer.SerializeToElement(knownZero,Json);
        Assert.Equal(JsonValueKind.Null,missing.GetProperty("nativeTotal").ValueKind);
        Assert.Equal(JsonValueKind.Null,missing.GetProperty("nativePaid").ValueKind);
        Assert.Equal(0m,zero.GetProperty("nativeTotal").GetDecimal());
        Assert.Equal(0m,zero.GetProperty("nativePaid").GetDecimal());
    }

    [Theory]
    [InlineData("invoices")]
    [InlineData("contracts")]
    [InlineData("entries")]
    [InlineData("lines")]
    [InlineData("clients")]
    public void LargeCollectionKeepsCompleteTotalsAndAdvancesByActualReturnedRows(string name)
    {
        var records=Enumerable.Range(21,50).Select(id=>new{id,name=new string('界',200)}).ToArray();
        var data=new Dictionary<string,object?>{{"totalCount",100},{"offset",20},{"returnedCount",50},{"hasMore",true},{name,records}};
        var result=EmployeeReportResponseBounds.Bound(new(true,data,null),20);
        var text=JsonSerializer.Serialize(result,Json);Assert.True(text.Length<=7000);
        var output=JsonSerializer.SerializeToElement(result.Data,Json);
        var count=output.GetProperty("returnedCount").GetInt32();
        Assert.InRange(count,1,49);Assert.Equal(100,output.GetProperty("totalCount").GetInt32());
        Assert.Equal(20+count,output.GetProperty("nextOffset").GetInt32());
        Assert.Equal(21,output.GetProperty(name)[0].GetProperty("id").GetInt32());
        Assert.Equal(20+count,output.GetProperty(name)[count-1].GetProperty("id").GetInt32());
        Assert.True(output.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("invoices")]
    [InlineData("contracts")]
    [InlineData("entries")]
    [InlineData("clients")]
    public void EmptyLaterPageRetainsFullCountAndStopsContinuation(string name)
    {
        var data=new Dictionary<string,object?>{{"totalCount",13},{"returnedCount",0},{"hasMore",false},{name,Array.Empty<object>()}};
        var result=EmployeeReportResponseBounds.Bound(new(true,data,null),5000);
        var output=JsonSerializer.SerializeToElement(result.Data,Json);
        Assert.Equal(13,output.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Null,output.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public void NativeOpenTicketPagesVisitEachRowExactlyOnceEvenWhenBudgetReducesPages()
    {
        var xml=new XElement("Root",Enumerable.Range(1,50).Select(id=>new XElement("TicketViewModel",
            new XElement("ID",id),new XElement("Title",new string('界',200)),new XElement("Client",new string('界',100)),new XElement("Status","New")))).ToString();
        var ids=new List<int>();var offset=0;var attempts=0;var reduced=false;
        while(true)
        {
            Assert.True(attempts++<51);
            var page=ClientPortalEmployeeReadTools.ParseTicketPage(xml,20,offset);
            Assert.Equal(50,page.TotalCount);Assert.Equal(page.ReturnedCount,page.Tickets.Count);
            Assert.True(JsonSerializer.Serialize(new EmployeeToolResult<EmployeeTicketPage>(true,page,null),Json).Length<=7000);
            reduced|=page.OutputBudgetApplied;ids.AddRange(page.Tickets.Select(t=>t.TicketId));
            if(!page.HasMore){Assert.Null(page.NextOffset);break;}
            Assert.Equal(offset+page.ReturnedCount,page.NextOffset);offset=page.NextOffset!.Value;
        }
        Assert.True(reduced);Assert.Equal(Enumerable.Range(1,50),ids);
        var later=ClientPortalEmployeeReadTools.ParseTicketPage(xml,20,5000);
        Assert.Equal(50,later.TotalCount);Assert.Empty(later.Tickets);Assert.False(later.HasMore);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public void InvalidOpenTicketOffsetFailsInsteadOfLookingLikeAnEmptyResult(int offset)=>
        Assert.Throws<InvalidDataException>(()=>ClientPortalEmployeeReadTools.ParseTicketPage("<Root/>",10,offset));
}
