using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

public class RangeHeaderTests
{
    [Fact]
    public void TryParse_BytesStartEnd()
    {
        Assert.True(RangeHeader.TryParse("bytes=0-99", 1000, out var s, out var e));
        Assert.Equal((0L, 99L), (s, e));
    }

    [Fact]
    public void TryParse_OpenEnded()
    {
        Assert.True(RangeHeader.TryParse("bytes=500-", 1000, out var s, out var e));
        Assert.Equal((500L, 999L), (s, e));
    }

    [Fact]
    public void TryParse_Suffix()
    {
        Assert.True(RangeHeader.TryParse("bytes=-100", 1000, out var s, out var e));
        Assert.Equal((900L, 999L), (s, e));
    }

    [Fact]
    public void TryParse_EndBeyondLength_Clamped()
    {
        Assert.True(RangeHeader.TryParse("bytes=900-5000", 1000, out var s, out var e));
        Assert.Equal((900L, 999L), (s, e));
    }

    [Fact]
    public void TryParse_SuffixLargerThanLength_WholeFile()
    {
        Assert.True(RangeHeader.TryParse("bytes=-5000", 1000, out var s, out var e));
        Assert.Equal((0L, 999L), (s, e));
    }

    [Fact]
    public void TryParse_MultiRange_False() =>
        Assert.False(RangeHeader.TryParse("bytes=0-10,20-30", 1000, out _, out _));

    [Fact]
    public void TryParse_StartBeyondLength_False() =>
        Assert.False(RangeHeader.TryParse("bytes=1000-", 1000, out _, out _));

    [Fact]
    public void TryParse_EndBeforeStart_False() =>
        Assert.False(RangeHeader.TryParse("bytes=50-10", 1000, out _, out _));

    [Fact]
    public void TryParse_Null_False() => Assert.False(RangeHeader.TryParse(null, 1000, out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("items=0-9")]
    [InlineData("bytes=abc-")]
    [InlineData("bytes=-")]
    [InlineData("bytes=-0")]
    public void TryParse_Malformed_False(string header) =>
        Assert.False(RangeHeader.TryParse(header, 1000, out _, out _));

    [Fact]
    public void TryParse_ZeroLength_False() => Assert.False(RangeHeader.TryParse("bytes=0-", 0, out _, out _));
}
