using CpApis.Shared.Runtime;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class CardPointeReportingPeriodTests
{
    [Fact]
    public void ExpandsFebruaryToItsCalendarDays()
    {
        var days = CardPointeReportingPeriod.Expand("202402");

        Assert.NotNull(days);
        Assert.Equal(29, days!.Count);
        Assert.Equal("20240201", days[0]);
        Assert.Equal("20240229", days[^1]);
    }

    [Fact]
    public void PreservesSingleDayCompatibility()
    {
        var days = CardPointeReportingPeriod.Expand("20260924");

        Assert.Equal(["20260924"], days);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("202613")]
    [InlineData("20260230")]
    public void RejectsInvalidPeriods(string? value)
    {
        Assert.Null(CardPointeReportingPeriod.Expand(value));
    }
}
