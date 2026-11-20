using CloudSelfService.Services.Storage;
using Xunit;

namespace CloudSelfService.Tests;

public class StoragePathsTests
{
    [Theory]
    [InlineData("", null)]
    [InlineData("a", "a/")]
    [InlineData("a/b", "a/b/")]
    public void BlobPrefix_RootIsNullOtherwiseTrailingSlash(string folder, string? expected) =>
        Assert.Equal(expected, StoragePaths.BlobPrefix(folder));

    [Fact]
    public void IsMarker_OnlyTheFolderMarkerLeaf()
    {
        Assert.True(StoragePaths.IsMarker("a/b/.folder"));
        Assert.True(StoragePaths.IsMarker(".folder"));
        Assert.False(StoragePaths.IsMarker("a/b/x.folder"));
        Assert.False(StoragePaths.IsMarker("a/.folder/x"));
    }

    [Theory]
    [InlineData("a/b/", "a/b")]
    [InlineData("a/", "a")]
    public void TrimPrefix_DropsTrailingSlash(string prefix, string expected) =>
        Assert.Equal(expected, StoragePaths.TrimPrefix(prefix));

    [Theory]
    [InlineData("a/b/c.txt", "a/b", "x/y", "x/y/c.txt")]
    [InlineData("a/b", "a/b", "x", "x")]
    [InlineData("a/b/c/d", "a/b", "", "c/d")]
    public void Rebase_MovesPathUnderNewFolder(string path, string from, string to, string expected) =>
        Assert.Equal(expected, StoragePaths.Rebase(path, from, to));

    [Theory]
    [InlineData(".big.bin.cssupload-0123456789abcdef0123456789abcdef", true)]
    [InlineData(".a.cssupload-0123456789abcdef0123456789abcde", false)]
    [InlineData(".a.cssupload-0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("big.bin.cssupload-0123456789abcdef0123456789abcdef", false)]
    [InlineData("report.txt", false)]
    public void IsStagingName_MatchesOnlyCallerStagingNames(string name, bool expected) =>
        Assert.Equal(expected, StoragePaths.IsStagingName(name));

    [Fact]
    public void IsHiddenName_MarkerAndStaging()
    {
        Assert.True(StoragePaths.IsHiddenName(".folder"));
        Assert.True(StoragePaths.IsHiddenName(".x.cssupload-0123456789abcdef0123456789abcdef"));
        Assert.False(StoragePaths.IsHiddenName("folder"));
    }

    [Theory]
    [InlineData("a", "a", true)]
    [InlineData("a", "a/b", true)]
    [InlineData("a", "ab", false)]
    [InlineData("a/b", "a", false)]
    public void IsMoveIntoItself_Cases(string from, string to, bool expected) =>
        Assert.Equal(expected, StoragePaths.IsMoveIntoItself(from, to));

    [Fact]
    public void SortPage_FoldersFirstThenFilesIgnoringCase()
    {
        var items = new[]
        {
            new CloudSelfService.Models.StorageItem("b.txt", "b.txt", false, 1, null),
            new CloudSelfService.Models.StorageItem("Zed", "Zed", true, null, null),
            new CloudSelfService.Models.StorageItem("A.txt", "A.txt", false, 1, null),
            new CloudSelfService.Models.StorageItem("alpha", "alpha", true, null, null),
        };
        Assert.Equal(new[] { "alpha", "Zed", "A.txt", "b.txt" }, StoragePaths.SortPage(items).Select(i => i.Name).ToArray());
    }

    private const long Mib = 1024 * 1024;

    [Fact]
    public void BlockPlanner_ZeroBytes_NoBlocks() => Assert.Empty(BlockPlanner.Plan(0));

    [Fact]
    public void BlockPlanner_OneByte_SingleBlock()
    {
        var b = Assert.Single(BlockPlanner.Plan(1));
        Assert.Equal((0L, 1L), (b.Offset, b.Length));
    }

    [Fact]
    public void BlockPlanner_ExactlyMax_SingleBlock()
    {
        var b = Assert.Single(BlockPlanner.Plan(4000 * Mib));
        Assert.Equal(4000 * Mib, b.Length);
    }

    [Fact]
    public void BlockPlanner_MaxPlusOne_TwoBlocks()
    {
        var blocks = BlockPlanner.Plan(4000 * Mib + 1);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(4000 * Mib, blocks[1].Offset);
        Assert.Equal(1, blocks[1].Length);
    }

    [Fact]
    public void BlockPlanner_TenGiB_ContiguousFixedWidthIds()
    {
        var size = 10L * 1024 * Mib;
        var blocks = BlockPlanner.Plan(size);
        Assert.Equal(3, blocks.Count);
        Assert.Equal(size, blocks.Sum(b => b.Length));
        long expected = 0;
        foreach (var b in blocks) { Assert.Equal(expected, b.Offset); expected += b.Length; }
        Assert.Single(blocks.Select(b => b.BlockId.Length).Distinct());
        Assert.Equal(blocks.Count, blocks.Select(b => b.BlockId).Distinct().Count());
    }

    [Fact]
    public void Ancestors_ShallowestFirst() =>
        Assert.Equal(new[] { "a", "a/b", "a/b/c" }, StoragePaths.Ancestors("a/b/c").ToArray());

    [Fact]
    public async Task RecursiveDelete_FilesThenSubfoldersDeepestFirstThenSelf()
    {
        var tree = new Dictionary<string, (string[] Files, string[] Folders)>
        {
            ["a"] = (new[] { "f1" }, new[] { "b", "c" }),
            ["a/b"] = (new[] { "f2" }, new[] { "d" }),
            ["a/b/d"] = (new[] { "f3" }, Array.Empty<string>()),
            ["a/c"] = (Array.Empty<string>(), Array.Empty<string>()),
        };
        var order = new List<string>();

        await RecursiveDelete.RunAsync("a",
            f => Task.FromResult(((IReadOnlyList<string>)tree[f].Files, (IReadOnlyList<string>)tree[f].Folders)),
            f => { order.Add("file:" + f); return Task.CompletedTask; },
            d => { order.Add("dir:" + d); return Task.CompletedTask; });

        Assert.Equal(new[]
        {
            "file:a/f1", "file:a/b/f2", "file:a/b/d/f3", "dir:a/b/d", "dir:a/b", "dir:a/c", "dir:a",
        }, order);
    }
}
