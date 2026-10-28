using System.Text;
using CpApis.Mcp.Core.Services;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class EmployeeTextChunkTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public void UnicodeAndMarkupCanBeReconstructedExactlyAcrossAllChunks(int size)
    {
        var original=string.Concat(Enumerable.Range(0,800).Select(i=>$"{i:D4}界😀<&>\n"));
        var text=new StringBuilder();var offset=0;var attempts=0;
        while(true)
        {
            Assert.True(attempts++<original.Length);
            var page=EmployeeTextChunk.Slice(original,offset,size);
            Assert.Equal(original.Length,page.TotalLength);Assert.Equal(offset,page.Offset);
            Assert.False(page.Text!.Length>0&&char.IsHighSurrogate(page.Text[^1]));
            Assert.False(page.Text.Length>0&&char.IsLowSurrogate(page.Text[0]));
            text.Append(page.Text);
            if(page.NextOffset is null)break;
            Assert.True(page.NextOffset>offset);offset=page.NextOffset.Value;
        }
        Assert.Equal(original,text.ToString());
    }

    [Fact]
    public void SqlFragmentWithLookbehindKeepsGlobalOffsetsAndSurrogatePairs()
    {
        var original=new string('a',499)+"😀"+new string('b',700);
        var first=EmployeeTextChunk.Slice(original,0);
        Assert.Equal(499,first.NextOffset);
        var offset=first.NextOffset!.Value;var start=offset-1;
        var page=EmployeeTextChunk.Slice(original.Substring(start,502),offset,500,start,original.Length);
        Assert.Equal(original.Substring(offset,page.Text!.Length),page.Text);
        Assert.Equal(offset+page.Text.Length,page.NextOffset);
        var midPair=EmployeeTextChunk.Slice(original.Substring(499,502),500,500,499,original.Length);
        Assert.Equal(499,midPair.Offset);Assert.StartsWith("😀",midPair.Text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidOffsetsFailExplicitly(int offset)=>Assert.Throws<EmployeeActionException>(()=>EmployeeTextChunk.Slice("text",offset));

    [Fact]
    public void EmptyAndMissingNativeTextStayDifferent()
    {
        Assert.Null(EmployeeTextChunk.Slice(null,0).Text);
        Assert.Equal("",EmployeeTextChunk.Slice("",0).Text);
        var end=EmployeeTextChunk.Slice("text",4);Assert.Equal("",end.Text);Assert.Null(end.NextOffset);Assert.Equal(4,end.TotalLength);
    }

    [Fact]
    public void MissingFragmentCannotPretendItIsAnEmptyNativeTextField()=>
        Assert.Throws<InvalidDataException>(()=>EmployeeTextChunk.Slice("",20,fragmentStart:19,nativeLength:100));
}
