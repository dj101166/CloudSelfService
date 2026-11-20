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
