using System.Security.Cryptography;
using System.Text;
using CloudSelfService.Models;
using CloudSelfService.Services.Storage;
using Xunit;

namespace CloudSelfService.Tests;

public class FakeStorageBackendTests
{
    private static readonly StorageTarget T =
        new(Guid.NewGuid(), null, "t", "", "", "acct", StorageKind.FileShare, "share", "core.windows.net", true);

    private readonly FakeStorageBackend _fake = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private async Task PutAsync(string path, string content, bool overwrite = true)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var staging = ".staging/" + Guid.NewGuid();
        await _fake.BeginUploadAsync(T, staging, bytes.Length, _ct);
        await _fake.WriteChunkAsync(T, staging, path, "b0", 0, new MemoryStream(bytes), bytes.Length, MD5.HashData(bytes), _ct);
        await _fake.CompleteUploadAsync(T, staging, path, new[] { "b0" }, overwrite, _ct);
    }

    private static async Task<string> ReadAll(Stream s)
    {
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task WriteChunk_Md5Mismatch_ThrowsConflict()
    {
        var bytes = new byte[] { 1, 2, 3 };
        await _fake.BeginUploadAsync(T, "s", 3, _ct);
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() =>
            _fake.WriteChunkAsync(T, "s", "f.bin", "b0", 0, new MemoryStream(bytes), 3, new byte[16], _ct));
        Assert.Equal(StorageFailureKind.Conflict, ex.Kind);
        Assert.Equal(0, _fake.BytesWritten);
    }

    [Fact]
    public async Task Complete_OverwriteFalse_ExistingFile_AlreadyExists()
    {
        await PutAsync("f.txt", "one");
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => PutAsync("f.txt", "two", overwrite: false));
        Assert.Equal(StorageFailureKind.AlreadyExists, ex.Kind);
        Assert.Equal("one", await ReadAll(await _fake.OpenReadAsync(T, "f.txt", 0, null, _ct)));
    }

    [Fact]
    public async Task Complete_OverwriteTrue_ReplacesContent()
    {
        await PutAsync("f.txt", "one");
        await PutAsync("f.txt", "two", overwrite: true);
        Assert.Equal("two", await ReadAll(await _fake.OpenReadAsync(T, "f.txt", 0, null, _ct)));
    }

    [Fact]
    public async Task Upload_NothingVisibleUntilComplete()
    {
        var bytes = Encoding.UTF8.GetBytes("abc");
        await _fake.BeginUploadAsync(T, "s", 3, _ct);
        await _fake.WriteChunkAsync(T, "s", "f.txt", "b0", 0, new MemoryStream(bytes), 3, MD5.HashData(bytes), _ct);
        Assert.Null(await _fake.GetItemAsync(T, "f.txt", _ct));
        Assert.Empty((await _fake.ListAsync(T, "", null, 50, _ct)).Items);
    }

    [Fact]
    public async Task Upload_ChunksAssembledInBlockListOrder()
    {
        var a = Encoding.UTF8.GetBytes("AAA");
        var b = Encoding.UTF8.GetBytes("BB");
        await _fake.BeginUploadAsync(T, "s", 5, _ct);
        await _fake.WriteChunkAsync(T, "s", "f", "b1", 3, new MemoryStream(b), 2, MD5.HashData(b), _ct);
        await _fake.WriteChunkAsync(T, "s", "f", "b0", 0, new MemoryStream(a), 3, MD5.HashData(a), _ct);
        await _fake.CompleteUploadAsync(T, "s", "f", new[] { "b0", "b1" }, true, _ct);
        Assert.Equal("AAABB", await ReadAll(await _fake.OpenReadAsync(T, "f", 0, null, _ct)));
    }

    [Fact]
    public async Task Abort_DiscardsStagedChunks_AndIgnoresUnknown()
    {
        var bytes = new byte[] { 1 };
        await _fake.BeginUploadAsync(T, "s", 1, _ct);
        await _fake.WriteChunkAsync(T, "s", "f", "b0", 0, new MemoryStream(bytes), 1, MD5.HashData(bytes), _ct);
        await _fake.AbortUploadAsync(T, "s", _ct);
        await _fake.AbortUploadAsync(T, "never", _ct);
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() =>
            _fake.CompleteUploadAsync(T, "s", "f", new[] { "b0" }, true, _ct));
        Assert.Equal(StorageFailureKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task List_FolderMarkerHidden_FolderStillVisible()
    {
        await _fake.CreateFolderAsync(T, "docs", _ct);

        var root = await _fake.ListAsync(T, "", null, 50, _ct);
        var item = Assert.Single(root.Items);
        Assert.True(item.IsFolder);
        Assert.Equal("docs", item.Name);
        Assert.Equal("docs", item.Path);

        Assert.Empty((await _fake.ListAsync(T, "docs", null, 50, _ct)).Items);
    }

    [Fact]
    public async Task CreateFolder_CreatesMissingParents()
    {
        await _fake.CreateFolderAsync(T, "a/b/c", _ct);
        Assert.True((await _fake.GetItemAsync(T, "a/b", _ct))!.IsFolder);
        Assert.Equal("b", Assert.Single((await _fake.ListAsync(T, "a", null, 50, _ct)).Items).Name);
    }

    [Fact]
    public async Task CreateFolder_Existing_AlreadyExists()
    {
        await _fake.CreateFolderAsync(T, "a", _ct);
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.CreateFolderAsync(T, "a", _ct));
        Assert.Equal(StorageFailureKind.AlreadyExists, ex.Kind);
    }

    [Fact]
    public async Task List_FoldersFirstThenFilesByName()
    {
        await PutAsync("b.txt", "x");
        await PutAsync("a.txt", "x");
        await _fake.CreateFolderAsync(T, "z", _ct);
        var names = (await _fake.ListAsync(T, "", null, 50, _ct)).Items.Select(i => i.Name).ToArray();
        Assert.Equal(new[] { "z", "a.txt", "b.txt" }, names);
    }

    [Fact]
    public async Task Delete_Folder_Recursive()
    {
        await _fake.CreateFolderAsync(T, "d/sub", _ct);
        await PutAsync("d/one.txt", "1");
        await PutAsync("d/sub/two.txt", "2");
        await PutAsync("keep.txt", "k");

        await _fake.DeleteAsync(T, "d", isFolder: true, _ct);

        Assert.Null(await _fake.GetItemAsync(T, "d", _ct));
        Assert.Null(await _fake.GetItemAsync(T, "d/sub/two.txt", _ct));
        Assert.NotNull(await _fake.GetItemAsync(T, "keep.txt", _ct));
    }

    [Fact]
    public async Task Delete_Missing_NotFound()
    {
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.DeleteAsync(T, "nope", false, _ct));
        Assert.Equal(StorageFailureKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task List_PageSizeOne_ReturnsContinuation()
    {
        await PutAsync("a.txt", "x");
        await PutAsync("b.txt", "x");
        await PutAsync("c.txt", "x");

        var p1 = await _fake.ListAsync(T, "", null, 1, _ct);
        Assert.Single(p1.Items);
        Assert.NotNull(p1.ContinuationToken);

        var p2 = await _fake.ListAsync(T, "", p1.ContinuationToken, 1, _ct);
        Assert.Equal("b.txt", p2.Items[0].Name);

        var p3 = await _fake.ListAsync(T, "", p2.ContinuationToken, 1, _ct);
        Assert.Equal("c.txt", p3.Items[0].Name);
        Assert.Null(p3.ContinuationToken);
    }

    [Fact]
    public async Task Move_File_FailsIfTargetExists_ElseMoves()
    {
        await PutAsync("a.txt", "A");
        await PutAsync("b.txt", "B");
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.MoveAsync(T, "a.txt", "b.txt", false, _ct));
        Assert.Equal(StorageFailureKind.AlreadyExists, ex.Kind);

        await _fake.MoveAsync(T, "a.txt", "c.txt", false, _ct);
        Assert.Null(await _fake.GetItemAsync(T, "a.txt", _ct));
        Assert.Equal("A", await ReadAll(await _fake.OpenReadAsync(T, "c.txt", 0, null, _ct)));
    }

    [Fact]
    public async Task Move_Folder_MovesContentsAndMarker()
    {
        await _fake.CreateFolderAsync(T, "old/inner", _ct);
        await PutAsync("old/inner/f.txt", "F");
        await _fake.MoveAsync(T, "old", "new", true, _ct);

        Assert.Null(await _fake.GetItemAsync(T, "old", _ct));
        Assert.Equal("F", await ReadAll(await _fake.OpenReadAsync(T, "new/inner/f.txt", 0, null, _ct)));
        Assert.True((await _fake.GetItemAsync(T, "new/inner", _ct))!.IsFolder);
    }

    [Fact]
    public async Task OpenRead_OffsetAndLength_ReturnsSlice()
    {
        await PutAsync("f.txt", "0123456789");
        Assert.Equal("345", await ReadAll(await _fake.OpenReadAsync(T, "f.txt", 3, 3, _ct)));
        Assert.Equal("789", await ReadAll(await _fake.OpenReadAsync(T, "f.txt", 7, null, _ct)));
    }

    [Fact]
    public async Task OpenRead_Missing_NotFound()
    {
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.OpenReadAsync(T, "nope", 0, null, _ct));
        Assert.Equal(StorageFailureKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Targets_AreIsolatedFromEachOther()
    {
        var other = T with { Id = Guid.NewGuid() };
        await PutAsync("only-here.txt", "x");
        Assert.Null(await _fake.GetItemAsync(other, "only-here.txt", _ct));
    }

    [Fact]
    public async Task Switches_ThrowMappedExceptions_AndWriteNothing()
    {
        _fake.ThrowUnreachable = true;
        var u = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.ListAsync(T, "", null, 10, _ct));
        Assert.Equal(StorageFailureKind.Unreachable, u.Kind);

        _fake.ThrowUnreachable = false;
        _fake.ThrowNotAuthorised = true;
        var n = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.ProbeAsync(T, _ct));
        Assert.Equal(StorageFailureKind.NotAuthorised, n.Kind);
        Assert.Equal(0, _fake.BytesWritten);
    }

    [Fact]
    public async Task Calls_Recorded_AndBytesWrittenCounted()
    {
        await PutAsync("f.txt", "abc");
        Assert.Equal(3, _fake.BytesWritten);
        Assert.Contains(_fake.Calls, c => c.StartsWith("WriteChunk"));
        Assert.StartsWith("BeginUpload", _fake.Calls[0]);
    }

    [Fact]
    public async Task Seed_AddsSampleContent_Idempotently()
    {
        _fake.Seed(T.Id);
        _fake.Seed(T.Id);
        var items = (await _fake.ListAsync(T, "", null, 50, _ct)).Items;
        Assert.Contains(items, i => i.IsFolder);
        Assert.Contains(items, i => !i.IsFolder);
        Assert.Equal(items.Count, items.Select(i => i.Name).Distinct().Count());
    }
}
