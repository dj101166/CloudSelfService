using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

public class RangeHeaderTests
{
    private static void AssertRange(string header, long length, long start, long end)
    {
        var r = RangeHeader.Parse(header, length);
        Assert.Equal(RangeParseKind.Satisfiable, r.Kind);
        Assert.Equal((start, end), (r.Start, r.End));
    }

    [Fact]
    public void Parse_BytesStartEnd() => AssertRange("bytes=0-99", 1000, 0, 99);

    [Fact]
    public void Parse_OpenEnded() => AssertRange("bytes=500-", 1000, 500, 999);

    [Fact]
    public void Parse_Suffix() => AssertRange("bytes=-100", 1000, 900, 999);

    [Fact]
    public void Parse_EndBeyondLength_Clamped() => AssertRange("bytes=900-5000", 1000, 900, 999);

    [Fact]
    public void Parse_SuffixLargerThanLength_WholeFile() => AssertRange("bytes=-5000", 1000, 0, 999);

    [Fact]
    public void Parse_MultiRange_None() =>
        Assert.Equal(RangeParseKind.None, RangeHeader.Parse("bytes=0-10,20-30", 1000).Kind);

    [Fact]
    public void Parse_StartBeyondLength_Unsatisfiable() =>
        Assert.Equal(RangeParseKind.Unsatisfiable, RangeHeader.Parse("bytes=1000-", 1000).Kind);

    [Fact]
    public void Parse_EndBeforeStart_NoneBecauseMalformed() =>
        Assert.Equal(RangeParseKind.None, RangeHeader.Parse("bytes=50-10", 1000).Kind);

    [Fact]
    public void Parse_Null_None() => Assert.Equal(RangeParseKind.None, RangeHeader.Parse(null, 1000).Kind);

    [Theory]
    [InlineData("")]
    [InlineData("items=0-9")]
    [InlineData("bytes=abc-")]
    [InlineData("bytes=-")]
    [InlineData("bytes=5-3")]
    [InlineData("bytes=0")]
    public void Parse_Malformed_None(string header) =>
        Assert.Equal(RangeParseKind.None, RangeHeader.Parse(header, 1000).Kind);

    [Fact]
    public void Parse_ZeroSuffix_Unsatisfiable() =>
        Assert.Equal(RangeParseKind.Unsatisfiable, RangeHeader.Parse("bytes=-0", 1000).Kind);

    [Theory]
    [InlineData("bytes=0-")]
    [InlineData("bytes=0-9")]
    [InlineData("bytes=-5")]
    public void Parse_ZeroLengthFile_Unsatisfiable(string header) =>
        Assert.Equal(RangeParseKind.Unsatisfiable, RangeHeader.Parse(header, 0).Kind);

    [Fact]
    public void Parse_ZeroLengthFile_MalformedStillNone() =>
        Assert.Equal(RangeParseKind.None, RangeHeader.Parse("garbage", 0).Kind);
}
