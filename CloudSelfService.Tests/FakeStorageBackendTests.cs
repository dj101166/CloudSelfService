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
        var staging = Stage(path);
        await _fake.BeginUploadAsync(T, staging, bytes.Length, _ct);
        await _fake.GrowStagingAsync(T, staging, bytes.Length, _ct);
        await _fake.WriteChunkAsync(T, staging, path, "b0", 0, new MemoryStream(bytes), bytes.Length, MD5.HashData(bytes), _ct);
        await _fake.CompleteUploadAsync(T, staging, path, new[] { "b0" }, overwrite, _ct);
    }

    private static string Stage(string path) =>
        StoragePaths.Join(StoragePaths.Parent(path), $".{StoragePaths.Leaf(path)}.cssupload-{Guid.NewGuid():N}");

    private static async Task<string> ReadAll(Stream s)
    {
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task WriteChunk_Md5Mismatch_ThrowsConflict()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var s = Stage("f.bin");
        await _fake.BeginUploadAsync(T, s, 3, _ct);
        await _fake.GrowStagingAsync(T, s, 3, _ct);
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() =>
            _fake.WriteChunkAsync(T, s, "f.bin", "b0", 0, new MemoryStream(bytes), 3, new byte[16], _ct));
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
        var s = Stage("f.txt");
        await _fake.BeginUploadAsync(T, s, 3, _ct);
        await _fake.GrowStagingAsync(T, s, 3, _ct);
        await _fake.WriteChunkAsync(T, s, "f.txt", "b0", 0, new MemoryStream(bytes), 3, MD5.HashData(bytes), _ct);
        Assert.Null(await _fake.GetItemAsync(T, "f.txt", _ct));
        Assert.Empty((await _fake.ListAsync(T, "", null, 50, _ct)).Items);
    }

    [Fact]
    public async Task Upload_ChunksAssembledInBlockListOrder()
    {
        var a = Encoding.UTF8.GetBytes("AAA");
        var b = Encoding.UTF8.GetBytes("BB");
        var s = Stage("f");
        await _fake.BeginUploadAsync(T, s, 5, _ct);
        await _fake.GrowStagingAsync(T, s, 5, _ct);
        await _fake.WriteChunkAsync(T, s, "f", "b1", 3, new MemoryStream(b), 2, MD5.HashData(b), _ct);
        await _fake.WriteChunkAsync(T, s, "f", "b0", 0, new MemoryStream(a), 3, MD5.HashData(a), _ct);
        await _fake.CompleteUploadAsync(T, s, "f", new[] { "b0", "b1" }, true, _ct);
        Assert.Equal("AAABB", await ReadAll(await _fake.OpenReadAsync(T, "f", 0, null, _ct)));
    }

    [Fact]
    public async Task Abort_DiscardsStagedChunks_AndIgnoresUnknown()
    {
        var bytes = new byte[] { 1 };
        var s = Stage("f");
        await _fake.BeginUploadAsync(T, s, 1, _ct);
        await _fake.GrowStagingAsync(T, s, 1, _ct);
        await _fake.WriteChunkAsync(T, s, "f", "b0", 0, new MemoryStream(bytes), 1, MD5.HashData(bytes), _ct);
        await _fake.AbortUploadAsync(T, s, _ct);
        await _fake.AbortUploadAsync(T, Stage("never"), _ct);
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() =>
            _fake.CompleteUploadAsync(T, s, "f", new[] { "b0" }, true, _ct));
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
    public async Task OpenRead_ZeroByteFile_EmptyStream()
    {
        await PutAsync("empty.txt", "");
        Assert.Equal("", await ReadAll(await _fake.OpenReadAsync(T, "empty.txt", 0, null, _ct)));
        Assert.Equal("", await ReadAll(await _fake.OpenReadAsync(T, "empty.txt", 0, 0, _ct)));
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a", "a/b")]
    public async Task Move_FolderIntoItself_Conflict(string from, string to)
    {
        await _fake.CreateFolderAsync(T, from, _ct);
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.MoveAsync(T, from, to, true, _ct));
        Assert.Equal(StorageFailureKind.Conflict, ex.Kind);
        Assert.Equal("Can't move a folder into itself.", ex.Message);
    }

    [Fact]
    public async Task Staging_InFlightFileHiddenFromListAndGetItem()
    {
        var staging = Stage("docs/f.bin");
        await _fake.BeginUploadAsync(T, staging, 3, _ct);
        Assert.Empty((await _fake.ListAsync(T, "docs", null, 50, _ct)).Items);
        Assert.Null(await _fake.GetItemAsync(T, staging, _ct));
    }

    [Fact]
    public async Task WriteChunk_StreamLengthMismatch_Throws()
    {
        var name = Stage("f");
        await _fake.BeginUploadAsync(T, name, 5, _ct);
        await _fake.GrowStagingAsync(T, name, 5, _ct);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _fake.WriteChunkAsync(T, name, "f", "b0", 0, new MemoryStream(new byte[2]), 5, new byte[16], _ct));
        Assert.Equal(0, _fake.BytesWritten);
    }

    // Mirrors Azure.Core's "stream must be seekable": the Kestrel request body must never be handed to storage.
    [Fact]
    public async Task WriteChunk_NonSeekableStream_Throws()
    {
        var name = Stage("f");
        await _fake.BeginUploadAsync(T, name, 3, _ct);
        var bytes = new byte[] { 1, 2, 3 };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _fake.WriteChunkAsync(T, name, "f", "b0", 0, new BufferedStream(new NonSeekable(bytes)), 3, MD5.HashData(bytes), _ct));
        Assert.Contains("seekable", ex.Message);
    }

    [Fact]
    public async Task HasLanded_Files_StagingGoneAndFinalPresentWithSessionStamp()
    {
        var mine = Guid.NewGuid();
        var staging = Stage("f.txt");
        await _fake.BeginUploadAsync(T, staging, 0, _ct, mine);
        Assert.False(await _fake.HasLandedAsync(T, staging, "f.txt", mine, 0, _ct));
        await _fake.CompleteUploadAsync(T, staging, "f.txt", Array.Empty<string>(), false, _ct, mine);
        Assert.True(await _fake.HasLandedAsync(T, staging, "f.txt", mine, 0, _ct));
        Assert.False(await _fake.HasLandedAsync(T, staging, "f.txt", Guid.NewGuid(), 0, _ct));
        Assert.False(await _fake.HasLandedAsync(T, staging, "other.txt", mine, 0, _ct));
    }

    // R32: Azure Files charges quota on logical size, so Begin must not reserve the declared size.
    [Fact]
    public async Task Begin_Files_CreatesEmptyStaging_GrowsOnlyWhenAsked()
    {
        var staging = Stage("big.iso");
        await _fake.BeginUploadAsync(T, staging, 50L * 1024 * 1024 * 1024, _ct);
        Assert.Equal(0, _fake.StagingLength(T.Id, staging));

        var bytes = new byte[] { 1, 2, 3 };
        var tooFar = await Assert.ThrowsAsync<StorageFailureException>(() =>
            _fake.WriteChunkAsync(T, staging, "big.iso", "b0", 0, new MemoryStream(bytes), 3, MD5.HashData(bytes), _ct));
        Assert.Equal(StorageFailureKind.Other, tooFar.Kind);

        await _fake.GrowStagingAsync(T, staging, 3, _ct);
        await _fake.WriteChunkAsync(T, staging, "big.iso", "b0", 0, new MemoryStream(bytes), 3, MD5.HashData(bytes), _ct);
        Assert.Equal(3, _fake.StagingLength(T.Id, staging));
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("Docs/report.pdf")]
    [InlineData("Docs/.report.pdf.cssupload-xyz")]
    public async Task Abort_Files_RefusesNonStagingName(string path)
    {
        await PutAsync("Docs/report.pdf", "keep");

        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.AbortUploadAsync(T, path, _ct));

        Assert.Equal(StorageFailureKind.Other, ex.Kind);
        Assert.Equal("keep", await ReadAll(await _fake.OpenReadAsync(T, "Docs/report.pdf", 0, null, _ct)));
    }

    [Fact]
    public async Task HasLanded_Blob_FirstCommittedBlockIdentifiesSession()
    {
        var blob = T with { Id = Guid.NewGuid(), Kind = StorageKind.BlobContainer };
        var mine = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3 };
        var block = CloudSelfService.Services.Transfers.ChunkMath.BlockId(mine, 0);
        await _fake.BeginUploadAsync(blob, "f", 3, _ct);
        await _fake.WriteChunkAsync(blob, "f", "f", block, 0, new MemoryStream(bytes), 3, MD5.HashData(bytes), _ct);
        Assert.False(await _fake.HasLandedAsync(blob, "f", "f", mine, 3, _ct));
        await _fake.CompleteUploadAsync(blob, "f", "f", new[] { block }, false, _ct, mine);

        Assert.True(await _fake.HasLandedAsync(blob, "f", "f", mine, 3, _ct));
        Assert.False(await _fake.HasLandedAsync(blob, "f", "f", Guid.NewGuid(), 3, _ct));
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task List_FoldersFirstThenFilesIgnoringCase()
    {
        await PutAsync("b.txt", "x");
        await PutAsync("A.txt", "x");
        await _fake.CreateFolderAsync(T, "Zed", _ct);
        await _fake.CreateFolderAsync(T, "alpha", _ct);
        var names = (await _fake.ListAsync(T, "", null, 50, _ct)).Items.Select(i => i.Name).ToArray();
        Assert.Equal(new[] { "alpha", "Zed", "A.txt", "b.txt" }, names);
    }

    [Fact]
    public async Task MoveFolder_OntoExistingFolderWithSameSizeDifferentFile_AlreadyExists_SourceKept()
    {
        await PutAsync("a/x.txt", "AAAA");
        await PutAsync("b/x.txt", "BBBB");
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.MoveAsync(T, "a", "b", true, _ct));
        Assert.Equal(StorageFailureKind.AlreadyExists, ex.Kind);
        Assert.Equal("AAAA", await ReadAll(await _fake.OpenReadAsync(T, "a/x.txt", 0, null, _ct)));
        Assert.Equal("BBBB", await ReadAll(await _fake.OpenReadAsync(T, "b/x.txt", 0, null, _ct)));
    }

    [Fact]
    public async Task MoveFolder_ResumeAfterPartialMove_Completes()
    {
        await PutAsync("a/1.txt", "one");
        await PutAsync("a/2.txt", "two");
        await PutAsync("a/3.txt", "three");

        _fake.FailFolderMoveAfterFiles = 2;
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.MoveAsync(T, "a", "b", true, _ct));
        Assert.Equal(StorageFailureKind.Unreachable, ex.Kind);

        _fake.FailFolderMoveAfterFiles = null;
        await _fake.MoveAsync(T, "a", "b", true, _ct);

        Assert.Null(await _fake.GetItemAsync(T, "a", _ct));
        Assert.Equal("three", await ReadAll(await _fake.OpenReadAsync(T, "b/3.txt", 0, null, _ct)));
        Assert.Equal(3, (await _fake.ListAsync(T, "b", null, 50, _ct)).Items.Count);
    }

    [Fact]
    public async Task MoveFolder_DestinationIsFile_AlreadyExists()
    {
        await PutAsync("a/x.txt", "x");
        await PutAsync("b", "i am a file");
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.MoveAsync(T, "a", "b", true, _ct));
        Assert.Equal(StorageFailureKind.AlreadyExists, ex.Kind);
        Assert.NotNull(await _fake.GetItemAsync(T, "a/x.txt", _ct));
    }

    [Fact]
    public async Task MoveFile_SecondMoveOfRecreatedSameSizeSource_AlreadyExists_SourceKept()
    {
        await PutAsync("a.txt", "AAAA");
        await _fake.CreateFolderAsync(T, "archive", _ct);
        await _fake.MoveAsync(T, "a.txt", "archive/a.txt", false, _ct);

        await PutAsync("a.txt", "NEW!");
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.MoveAsync(T, "a.txt", "archive/a.txt", false, _ct));
        Assert.Equal(StorageFailureKind.AlreadyExists, ex.Kind);
        Assert.Equal("NEW!", await ReadAll(await _fake.OpenReadAsync(T, "a.txt", 0, null, _ct)));
        Assert.Equal("AAAA", await ReadAll(await _fake.OpenReadAsync(T, "archive/a.txt", 0, null, _ct)));
    }

    [Fact]
    public async Task MoveFolder_SecondMoveOfRecreatedSameSizeSource_AlreadyExists_SourceKept()
    {
        await PutAsync("a/x.txt", "AAAA");
        await _fake.MoveAsync(T, "a", "b", true, _ct);

        await PutAsync("a/x.txt", "NEW!");
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() => _fake.MoveAsync(T, "a", "b", true, _ct));
        Assert.Equal(StorageFailureKind.AlreadyExists, ex.Kind);
        Assert.Equal("NEW!", await ReadAll(await _fake.OpenReadAsync(T, "a/x.txt", 0, null, _ct)));
        Assert.Equal("AAAA", await ReadAll(await _fake.OpenReadAsync(T, "b/x.txt", 0, null, _ct)));
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
