using CloudSelfService.Services.Access;
using Xunit;

namespace CloudSelfService.Tests;

public class StoragePathTests
{
    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../b")]
    [InlineData("a/./b")]
    public void TryNormalizePath_DotDot_Rejected(string raw)
    {
        Assert.False(StoragePath.TryNormalizePath(raw, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryNormalizePath_Backslash_Rejected() =>
        Assert.False(StoragePath.TryNormalizePath("a\\b", out _, out _));

    [Fact]
    public void TryNormalizePath_LeadingTrailingSlashes_Trimmed()
    {
        Assert.True(StoragePath.TryNormalizePath("/a//b/", out var n, out var error));
        Assert.Equal("a/b", n);
        Assert.Null(error);
    }

    [Fact]
    public void TryNormalizePath_SpecialCharacters_Accepted()
    {
        const string raw = "Q3 #1 50% a+b & c's/Ünïcode.txt";
        Assert.True(StoragePath.TryNormalizePath(raw, out var n, out _));
        Assert.Equal(raw, n);
    }

    [Fact]
    public void TryNormalizePath_ControlCharacter_Rejected() =>
        Assert.False(StoragePath.TryNormalizePath("a\u0001b", out _, out _));

    [Fact]
    public void TryNormalizePath_DeleteCharacter_Rejected() =>
        Assert.False(StoragePath.TryNormalizePath("a\u007fb", out _, out _));

    [Fact]
    public void TryNormalizePath_TrailingDotSegment_Rejected() =>
        Assert.False(StoragePath.TryNormalizePath("a./b", out _, out _));

    [Fact]
    public void TryNormalizePath_TrailingSpaceSegment_Rejected() =>
        Assert.False(StoragePath.TryNormalizePath("a /b", out _, out _));

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a:b")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    public void TryNormalizePath_ReservedCharacter_Rejected(string raw) =>
        Assert.False(StoragePath.TryNormalizePath(raw, out _, out _));

    [Fact]
    public void TryNormalizePath_SegmentOf256_Rejected() =>
        Assert.False(StoragePath.TryNormalizePath(new string('a', 256), out _, out _));

    [Fact]
    public void TryNormalizePath_SegmentOf255_Accepted() =>
        Assert.True(StoragePath.TryNormalizePath(new string('a', 255), out _, out _));

    [Fact]
    public void TryNormalizePath_TotalOver1024_Rejected()
    {
        var raw = string.Join("/", new string('a', 200), new string('b', 200), new string('c', 200),
            new string('d', 200), new string('e', 200), new string('f', 100));
        Assert.True(raw.Length > 1024);
        Assert.False(StoragePath.TryNormalizePath(raw, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void TryNormalizePath_NullOrBlank_IsRoot(string? raw)
    {
        Assert.True(StoragePath.TryNormalizePath(raw, out var n, out _));
        Assert.Equal("", n);
    }

    [Fact]
    public void TryValidateName_ContainsSlash_Rejected() =>
        Assert.False(StoragePath.TryValidateName("a/b", out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a:b")]
    public void TryValidateName_Invalid_Rejected(string? name) =>
        Assert.False(StoragePath.TryValidateName(name, out _));

    [Fact]
    public void TryValidateName_SpecialCharacters_Accepted()
    {
        Assert.True(StoragePath.TryValidateName("Ünï #1 50%.txt", out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Parent_RootChild_IsEmpty() => Assert.Equal("", StoragePath.Parent("a"));

    [Fact]
    public void Parent_Nested_ReturnsFolder() => Assert.Equal("a/b", StoragePath.Parent("a/b/c.txt"));

    [Fact]
    public void Parent_Root_IsEmpty() => Assert.Equal("", StoragePath.Parent(""));

    [Fact]
    public void Leaf_Nested_ReturnsLastSegment() => Assert.Equal("c.txt", StoragePath.Leaf("a/b/c.txt"));

    [Fact]
    public void Leaf_Root_IsEmpty() => Assert.Equal("", StoragePath.Leaf(""));

    [Fact]
    public void Combine_EmptyFolder_ReturnsName() => Assert.Equal("x.txt", StoragePath.Combine("", "x.txt"));

    [Fact]
    public void Combine_Folder_JoinsWithSlash() => Assert.Equal("a/b/x.txt", StoragePath.Combine("a/b", "x.txt"));

    [Fact]
    public void TryNormalizePath_TotalExactly1024_Accepted()
    {
        var raw = string.Join('/', Enumerable.Repeat(new string('a', 204), 5));
        Assert.Equal(1024, raw.Length);

        Assert.True(StoragePath.TryNormalizePath(raw, out var n, out var error));
        Assert.Equal(raw, n);
        Assert.Null(error);
        Assert.False(StoragePath.TryNormalizePath(raw + "b", out _, out _));
    }

    [Fact]
    public void TryNormalizePath_Failure_LeavesNormalizedEmpty()
    {
        Assert.False(StoragePath.TryNormalizePath("a/..", out var n, out _));
        Assert.Equal("", n);
    }
}
