using System.Text.Json;
using System.Text.Json.Nodes;
using CpApis.Mcp.Core.Tools;

namespace CpApis.Mcp.Core.Services;

/// <summary>Leave room beneath Chat-AI's existing8000-character tool cap, without cutting JSON or totals.</summary>
public static class EmployeeReportResponseBounds
{
    public const int MaxCharacters=7000;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};

    public static EmployeeToolResult<object> Bound(EmployeeToolResult<object> result,int offset=0)
    {
        if(result.Data is null)return result;
        if(JsonSerializer.Serialize(result,Json).Length<=MaxCharacters)
        {
            // Continuation metadata also belongs on pages which did not need budget reduction.
            var small=JsonSerializer.SerializeToNode(result.Data,Json) as JsonObject;
            if(small is null)return result;
            var key=new[]{"entries","events","invoices","contracts","lines","clients"}.FirstOrDefault(k=>small[k] is JsonArray);
            if(key is not null&&small[key] is JsonArray page)
            {
                small["offset"]=offset;
                small["nextOffset"]=small["hasMore"]?.GetValue<bool>()==true?JsonValue.Create(offset+page.Count):null;
                var prepared=new EmployeeToolResult<object>(result.Success,JsonSerializer.SerializeToElement(small,Json),result.Error);
                if(JsonSerializer.Serialize(prepared,Json).Length<=MaxCharacters)return prepared;
            }
            else return result;
        }
        var data=JsonSerializer.SerializeToNode(result.Data,Json) as JsonObject;
        if(data is null)throw new InvalidDataException("Native report shape cannot be paged safely.");
        var arrayKey=new[]{"entries","events","employees","invoices","contracts","lines","clients"}.FirstOrDefault(k=>data[k] is JsonArray)
            ??throw new InvalidDataException("Native report exceeded its safe bound.");
        var rows=(JsonArray)data[arrayKey]!;var removed=false;
        data["outputBudgetApplied"]=true;
        var pageable=arrayKey!="employees";
        if(pageable)data["offset"]=offset;
        while(true)
        {
            data["returnedCount"]=rows.Count;
            if(removed)data["hasMore"]=true;
            if(pageable)data["nextOffset"]=data["hasMore"]?.GetValue<bool>()==true?JsonValue.Create(offset+rows.Count):null;
            else if(removed)data["refineSearchForMore"]=true;
            var bounded=new EmployeeToolResult<object>(result.Success,JsonSerializer.SerializeToElement(data,Json),result.Error);
            if(JsonSerializer.Serialize(bounded,Json).Length<=MaxCharacters)return bounded;
            if(rows.Count>1){rows.RemoveAt(rows.Count-1);removed=true;continue;}
            if(rows.Count==1&&rows[0] is JsonObject row&&row["notes"] is JsonValue note&&note.TryGetValue<string>(out var text)&&text.Length>0)
            {row["notes"]=text[..(text.Length/2)];row["notesTruncated"]=true;continue;}
            throw new InvalidDataException("Native report metadata exceeded its safe bound.");
        }
    }
}
