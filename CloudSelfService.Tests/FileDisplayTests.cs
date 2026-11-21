using CloudSelfService.Services.Files;
using Xunit;

namespace CloudSelfService.Tests;

public class FileDisplayTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData(-1L, "")]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(20L * 1024 * 1024, "20.0 MB")]
    [InlineData(153L * 1024 * 1024, "153 MB")]
    [InlineData(1048575L, "1.0 MB")]
    [InlineData(53687091200L, "50.0 GB")]
    public void Size_Humanises_BinaryUnits(long? bytes, string expected) => Assert.Equal(expected, FileDisplay.Size(bytes));

    [Fact]
    public void Modified_NoZone_ShowsUtcLabelled() =>
        Assert.Equal("2026-11-20 09:05 UTC", FileDisplay.Modified(new DateTimeOffset(2026, 11, 20, 9, 5, 0, TimeSpan.Zero), null));

    [Fact]
    public void Modified_FixedOffsetZone_ConvertsToLocal()
    {
        var zone = FileDisplay.ResolveZone(null, 600);
        Assert.Equal("2026-11-20 19:05", FileDisplay.Modified(new DateTimeOffset(2026, 11, 20, 9, 5, 0, TimeSpan.Zero), zone));
    }

    [Fact]
    public void Modified_Null_IsEmpty() => Assert.Equal("", FileDisplay.Modified(null, null));

    [Fact]
    public void ResolveZone_UnknownIdAndNoOffset_IsNull() => Assert.Null(FileDisplay.ResolveZone("Not/AZone", null));

    [Fact]
    public void ResolveZone_UnknownId_FallsBackToOffset()
    {
        var zone = FileDisplay.ResolveZone("Not/AZone", -300);
        Assert.NotNull(zone);
        Assert.Equal(TimeSpan.FromHours(-5), zone!.BaseUtcOffset);
    }

    [Fact]
    public void ResolveZone_AbsurdOffset_IsNull() => Assert.Null(FileDisplay.ResolveZone(null, 20 * 60));

    [Fact]
    public void BrowseHref_Root_HasNoTrailingSlash()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"files/{id}", FileDisplay.BrowseHref(id, ""));
    }

    [Fact]
    public void BrowseHref_EscapesEachSegment_KeepsSlashes()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"files/{id}/A%20%23%25%2B%26/b%27%C3%A9", FileDisplay.BrowseHref(id, "A #%+&/b'é"));
    }

    [Fact]
    public void DownloadHref_EscapesWholePath_PlusIsEncoded()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"/api/files/{id}/download?path=a%2Fb%20c%2B1%23.txt", FileDisplay.DownloadHref(id, "a/b c+1#.txt"));
    }

    [Fact]
    public void Breadcrumbs_Root_IsEmpty() => Assert.Empty(FileDisplay.Breadcrumbs(""));

    [Fact]
    public void Breadcrumbs_CarryRealPath_ShowSanitisedLabel()
    {
        var crumbs = FileDisplay.Breadcrumbs("Reports/a‮b/2026");

        Assert.Equal(new[] { ("Reports", "Reports"), ("a_b", "Reports/a‮b"), ("2026", "Reports/a‮b/2026") }, crumbs);
    }

    [Theory]
    [InlineData("a/b.txt", "a/c.txt", true)]
    [InlineData("b.txt", "c.txt", true)]
    [InlineData("a/b.txt", "b.txt", false)]
    [InlineData("a/b.txt", "x/b.txt", false)]
    public void IsRename_SameParentOnly(string from, string to, bool expected) => Assert.Equal(expected, FileDisplay.IsRename(from, to));

    [Theory]
    [InlineData(" /a/b/ ", "a/b")]
    [InlineData("a\\b\\c.txt", "a/b/c.txt")]
    [InlineData("a// b /c", "a/ b /c")]
    [InlineData("a/ /c", "a/c")]
    [InlineData(null, "")]
    public void CleanTypedPath_TrimsAndNormalisesSeparators(string? typed, string expected) =>
        Assert.Equal(expected, FileDisplay.CleanTypedPath(typed));
}
