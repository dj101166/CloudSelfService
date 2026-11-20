using Azure;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using CloudSelfService.Models;

namespace CloudSelfService.Services.Storage;

public sealed class FileShareBackend(StorageClientFactory clients) : IStorageBackend
{
    private static ShareDirectoryClient Dir(ShareClient share, string path) =>
        path.Length == 0 ? share.GetRootDirectoryClient() : share.GetDirectoryClient(path);

    private static ShareFileClient File(ShareClient share, string path) => share.GetRootDirectoryClient().GetFileClient(path);

    private static async Task EnsureFoldersAsync(ShareClient share, string folder, CancellationToken ct)
    {
        foreach (var p in StoragePaths.Ancestors(folder))
            await share.GetDirectoryClient(p).CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    public Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var dir = Dir(clients.Share(t), folder);
            var options = new ShareDirectoryGetFilesAndDirectoriesOptions { Traits = ShareFileTraits.Timestamps };
            await foreach (var page in dir.GetFilesAndDirectoriesAsync(options, ct)
                               .AsPages(continuation, pageSize).ConfigureAwait(false))
            {
                var items = StoragePaths.SortPage(page.Values.Where(i => !StoragePaths.IsHiddenName(i.Name)).Select(i =>
                    new StorageItem(i.Name, StoragePaths.Join(folder, i.Name), i.IsDirectory,
                        i.IsDirectory ? null : i.FileSize, i.Properties?.LastModified)));
                return new StorageListing(items, string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
            }
            return new StorageListing(Array.Empty<StorageItem>(), null);
        }, ct);

    public Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct) =>
        StorageErrorMapper.RunAsync<StorageItem?>(async () =>
        {
            var share = clients.Share(t);
            if (path.Length == 0) return new StorageItem("", "", true, null, null);
            if (StoragePaths.IsHiddenName(StoragePaths.Leaf(path))) return null;
            try
            {
                var props = await File(share, path).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
                return new StorageItem(StoragePaths.Leaf(path), path, false, props.Value.ContentLength, props.Value.LastModified,
                    props.Value.ETag.ToString("H"));
            }
            catch (RequestFailedException ex) when (ex.Status == 404 || ex.ErrorCode == "ResourceTypeMismatch") { }

            try
            {
                var props = await share.GetDirectoryClient(path).GetPropertiesAsync(ct).ConfigureAwait(false);
                return new StorageItem(StoragePaths.Leaf(path), path, true, null, props.Value.LastModified);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
        }, ct);

    public Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct, string? expectedETag = null) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            if (length == 0) return (Stream)new MemoryStream(Array.Empty<byte>(), writable: false);
            // A Range header on an empty file is a 416, so a whole-file read must not send one.
            var options = offset == 0 && length is null
                ? null
                : new ShareFileDownloadOptions { Range = length is null ? new HttpRange(offset) : new HttpRange(offset, length.Value) };
            var resp = await File(clients.Share(t), path).DownloadAsync(options, ct).ConfigureAwait(false);
            // Azure Files has no If-Match on download, so a file replaced between the check and the read is caught here.
            if (expectedETag is not null && resp.Value.Details.ETag.ToString("H") != expectedETag)
            {
                await resp.Value.Content.DisposeAsync().ConfigureAwait(false);
                throw new StorageFailureException(StorageFailureKind.Conflict, StorageErrorMapper.ChangedDuringReadMessage);
            }
            return resp.Value.Content;
        }, ct);

    public Task CreateFolderAsync(StorageTarget t, string path, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var share = clients.Share(t);
            await EnsureFoldersAsync(share, StoragePaths.Parent(path), ct).ConfigureAwait(false);
            await share.GetDirectoryClient(path).CreateAsync(cancellationToken: ct).ConfigureAwait(false);
        }, ct);

    public Task MoveAsync(StorageTarget t, string from, string to, bool isFolder, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            if (isFolder && StoragePaths.IsMoveIntoItself(from, to)) throw StorageErrorMapper.MoveIntoItself();
            var share = clients.Share(t);
            await EnsureFoldersAsync(share, StoragePaths.Parent(to), ct).ConfigureAwait(false);
            // Renames are atomic per call, so Files moves need no resume marker.
            var options = new ShareFileRenameOptions { ReplaceIfExists = false };
            if (isFolder) await share.GetDirectoryClient(from).RenameAsync(to, options, ct).ConfigureAwait(false);
            else await File(share, from).RenameAsync(to, options, ct).ConfigureAwait(false);
        }, ct);

    public Task DeleteAsync(StorageTarget t, string path, bool isFolder, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var share = clients.Share(t);
            if (!isFolder)
            {
                await File(share, path).DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            await RecursiveDelete.RunAsync(path,
                async folder =>
                {
                    var files = new List<string>();
                    var folders = new List<string>();
                    await foreach (var i in share.GetDirectoryClient(folder).GetFilesAndDirectoriesAsync(cancellationToken: ct).ConfigureAwait(false))
                        (i.IsDirectory ? folders : files).Add(i.Name);
                    return ((IReadOnlyList<string>)files, (IReadOnlyList<string>)folders);
                },
                file => File(share, file).DeleteAsync(cancellationToken: ct),
                folder => share.GetDirectoryClient(folder).DeleteAsync(ct)).ConfigureAwait(false);
        }, ct);

    public Task BeginUploadAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct, Guid? uploadSessionId = null) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            RequireStagingName(stagingPath);
            var share = clients.Share(t);
            await EnsureFoldersAsync(share, StoragePaths.Parent(stagingPath), ct).ConfigureAwait(false);
            await File(share, stagingPath).CreateAsync(0, new ShareFileCreateOptions { Metadata = UploadMetadata.For(uploadSessionId) },
                conditions: null, ct).ConfigureAwait(false);
        }, ct);

    public Task GrowStagingAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            RequireStagingName(stagingPath);
            await File(clients.Share(t), stagingPath).SetHttpHeadersAsync(new ShareFileSetHttpHeadersOptions { NewSize = size },
                conditions: null, ct).ConfigureAwait(false);
        }, ct);

    // Every staging write and delete goes through this, so a corrupted session row can never touch a user's file.
    private static void RequireStagingName(string stagingPath)
    {
        if (!StoragePaths.IsStagingName(StoragePaths.Leaf(stagingPath)))
            throw new StorageFailureException(StorageFailureKind.Other, StorageErrorMapper.OtherMessage);
    }

    public Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset,
        Stream data, long length, byte[] md5, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            RequireStagingName(stagingPath);
            try
            {
                await File(clients.Share(t), stagingPath).UploadRangeAsync(new HttpRange(offset, length), data,
                    new ShareFileUploadRangeOptions { TransactionalContentHash = md5 }, ct).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 416)
            {
                // The staging file is shorter than this range: it was truncated or replaced outside the portal.
                throw new StorageFailureException(StorageFailureKind.Conflict, StorageErrorMapper.StagingIncompleteMessage, ex);
            }
        }, ct);

    public Task CompleteUploadAsync(StorageTarget t, string stagingPath, string finalPath, IReadOnlyList<string> blockIds,
        bool overwrite, CancellationToken ct, Guid? uploadSessionId = null, long? expectedSize = null) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            RequireStagingName(stagingPath);
            var share = clients.Share(t);
            if (expectedSize is { } size)
            {
                var props = await File(share, stagingPath).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
                if (props.Value.ContentLength != size)
                    throw new StorageFailureException(StorageFailureKind.Conflict, StorageErrorMapper.StagingIncompleteMessage);
            }
            await EnsureFoldersAsync(share, StoragePaths.Parent(finalPath), ct).ConfigureAwait(false);
            // The stamp is passed explicitly rather than relying on Rename carrying the staging file's metadata across.
            await File(share, stagingPath).RenameAsync(finalPath,
                new ShareFileRenameOptions { ReplaceIfExists = overwrite, Metadata = UploadMetadata.For(uploadSessionId) }, ct)
                .ConfigureAwait(false);
        }, ct);

    public Task<bool> HasLandedAsync(StorageTarget t, string stagingPath, string finalPath, Guid sessionId, long size,
        CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var share = clients.Share(t);
            if ((await File(share, stagingPath).ExistsAsync(ct).ConfigureAwait(false)).Value) return false;
            try
            {
                var props = await File(share, finalPath).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
                return UploadMetadata.Matches(props.Value.Metadata, sessionId);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }, ct);

    public Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            RequireStagingName(stagingPath);
            await File(clients.Share(t), stagingPath).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }, ct);

    public Task ProbeAsync(StorageTarget t, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            await clients.Share(t).GetRootDirectoryClient().GetPropertiesAsync(ct).ConfigureAwait(false);
        }, ct);
}
