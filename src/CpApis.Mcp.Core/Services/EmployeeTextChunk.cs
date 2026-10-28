namespace CpApis.Mcp.Core.Services;

/// <summary>UTF-16 offsets match SQL's explicit non-SC binary collation. Continuations never split a surrogate pair.</summary>
public sealed record EmployeeTextChunk(string? Text,int Offset,int? NextOffset,long TotalLength,bool Truncated)
{
    public static void ValidateOffset(int offset)
    {
        if(offset<0||offset>int.MaxValue-1024)throw new EmployeeActionException(400,"The text offset is invalid.");
    }

    public static EmployeeTextChunk Slice(string? value,int offset,int maximum=500,int fragmentStart=0,long? nativeLength=null)
    {
        ValidateOffset(offset);
        if(maximum is <2 or >500||fragmentStart<0||fragmentStart>offset)throw new InvalidDataException("Invalid native text window.");
        var total=nativeLength??value?.Length??0;
        if(value is null)return new(null,offset,null,total,offset>0);
        if(offset>=total)return new("",offset,null,total,offset>0);
        var start=offset-fragmentStart;
        if(start>=value.Length)throw new InvalidDataException("Native text window is incomplete.");
        if(start>0&&char.IsLowSurrogate(value[start])&&char.IsHighSurrogate(value[start-1]))start--;
        var end=Math.Min(value.Length,start+maximum);
        if(end<value.Length&&end>start&&char.IsHighSurrogate(value[end-1])&&char.IsLowSurrogate(value[end]))end--;
        if(end<=start)throw new InvalidDataException("Native text window cannot advance.");
        var actual=fragmentStart+start;var next=(long)fragmentStart+end<total?fragmentStart+end:(int?)null;
        return new(value[start..end],actual,next,total,actual>0||next is not null);
    }
}
