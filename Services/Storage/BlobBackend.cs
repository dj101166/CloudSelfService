using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using CloudSelfService.Models;

namespace CloudSelfService.Services.Storage;

public sealed class BlobBackend(StorageClientFactory clients) : IStorageBackend
{
    private const long SyncCopyLimit = 256L * 1024 * 1024;
    private static readonly string[] StorageScope = { "https://storage.azure.com/.default" };

    // Listing order is folders first then files by name within one page only; the service orders by blob name.
    public Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var container = clients.Container(t);
            var sawPage = false;
            await foreach (var page in container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/",
                                   StoragePaths.BlobPrefix(folder), ct).AsPages(continuation, pageSize).ConfigureAwait(false))
            {
                // An empty first page means nothing at all sits under the prefix, not even a .folder marker
                // (the marker is returned here before it is filtered), so the folder does not exist.
                var firstPage = continuation is null && !sawPage;
                sawPage = true;
                if (folder.Length > 0 && firstPage && page.Values.Count == 0)
                {
                    // The service may return an empty page with a continuation token; only a final empty page is "missing".
                    if (!string.IsNullOrEmpty(page.ContinuationToken)) { sawPage = false; continuation = page.ContinuationToken; }
                    else throw new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);
                    continue;
                }

                var items = new List<StorageItem>();
                foreach (var v in page.Values)
                {
                    if (v.IsPrefix)
                    {
                        var path = StoragePaths.TrimPrefix(v.Prefix);
                        items.Add(new StorageItem(StoragePaths.Leaf(path), path, true, null, null));
                    }
                    else if (!StoragePaths.IsHiddenName(StoragePaths.Leaf(v.Blob.Name)))
                    {
                        items.Add(new StorageItem(StoragePaths.Leaf(v.Blob.Name), v.Blob.Name, false,
                            v.Blob.Properties.ContentLength, v.Blob.Properties.LastModified));
                    }
                }
                return new StorageListing(StoragePaths.SortPage(items),
                    string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
            }
            if (folder.Length > 0 && continuation is null)
                throw new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);
            return new StorageListing(Array.Empty<StorageItem>(), null);
        }, ct);

    public Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct) =>
        StorageErrorMapper.RunAsync<StorageItem?>(async () =>
        {
            if (path.Length == 0) return new StorageItem("", "", true, null, null);
            if (StoragePaths.IsHiddenName(StoragePaths.Leaf(path))) return null;
            var container = clients.Container(t);
            try
            {
                var props = await container.GetBlobClient(path).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
                return new StorageItem(StoragePaths.Leaf(path), path, false, props.Value.ContentLength, props.Value.LastModified);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            await foreach (var _ in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, path + "/", ct).ConfigureAwait(false))
                return new StorageItem(StoragePaths.Leaf(path), path, true, null, null);
            return null;
        }, ct);

    public Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            if (length == 0) return (Stream)new MemoryStream(Array.Empty<byte>(), writable: false);
            // A Range header on an empty blob is a 416, so a whole-blob read must not send one.
            var options = offset == 0 && length is null
                ? null
                : new BlobDownloadOptions { Range = length is null ? new HttpRange(offset) : new HttpRange(offset, length.Value) };
            var resp = await clients.Container(t).GetBlobClient(path).DownloadStreamingAsync(options, ct).ConfigureAwait(false);
            return resp.Value.Content;
        }, ct);

    public Task CreateFolderAsync(StorageTarget t, string path, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            if (await GetItemAsync(t, path, ct).ConfigureAwait(false) is not null)
                throw new StorageFailureException(StorageFailureKind.AlreadyExists, StorageErrorMapper.AlreadyExistsMessage);
            await clients.Container(t).GetBlobClient(StoragePaths.MarkerPath(path))
                .UploadAsync(BinaryData.FromBytes(Array.Empty<byte>()), overwrite: false, ct).ConfigureAwait(false);
        }, ct);

    // Folder moves are resumable: a destination blob counts as "already copied" only if this mover wrote it
    // (metadata cssp_move_src equals the source path) and the length matches. Any other existing destination
    // is AlreadyExists and the source is kept, so a move can never delete a user's file it did not copy.
    public Task MoveAsync(StorageTarget t, string from, string to, bool isFolder, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            if (isFolder && StoragePaths.IsMoveIntoItself(from, to)) throw StorageErrorMapper.MoveIntoItself();
            var container = clients.Container(t);

            if (!isFolder)
            {
                await CopyThenDeleteAsync(container, from, to, ct).ConfigureAwait(false);
                return;
            }

            if (await container.GetBlobClient(to).ExistsAsync(ct).ConfigureAwait(false))
                throw new StorageFailureException(StorageFailureKind.AlreadyExists, StorageErrorMapper.AlreadyExistsMessage);

            var sources = new List<(string Name, long Length)>();
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, from + "/", ct).ConfigureAwait(false))
                sources.Add((b.Name, b.Properties.ContentLength ?? 0));
            if (sources.Count == 0)
                throw new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);

            foreach (var (name, length) in sources)
                await CopyThenDeleteAsync(container, name, StoragePaths.Rebase(name, from, to), ct).ConfigureAwait(false);
        }, ct);

    private async Task<HttpAuthorization> BearerAsync(CancellationToken ct)
    {
        // The credential caches tokens, so asking per copy is cheap and never hands out an expired one.
        var token = await clients.Credential.GetTokenAsync(new TokenRequestContext(StorageScope), ct).ConfigureAwait(false);
        return new HttpAuthorization("Bearer", token.Token);
    }

    private async Task CopyThenDeleteAsync(BlobContainerClient container, string from, string to, CancellationToken ct)
    {
        var source = container.GetBlobClient(from);
        var dest = container.GetBlockBlobClient(to);

        var sourceProps = await source.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        var size = sourceProps.Value.ContentLength;

        var stamp = new Dictionary<string, string>(sourceProps.Value.Metadata)
        {
            [StoragePaths.MoveSourceKey] = StoragePaths.EncodeMoveSource(from),
        };

        BlobProperties? existing = null;
        try { existing = (await dest.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false)).Value; }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        if (existing is not null)
        {
            var ours = existing.ContentLength == size
                && existing.Metadata.TryGetValue(StoragePaths.MoveSourceKey, out var tag)
                && tag == StoragePaths.EncodeMoveSource(from);
            if (!ours)
                throw new StorageFailureException(StorageFailureKind.AlreadyExists, StorageErrorMapper.AlreadyExistsMessage);
            try { await source.DeleteAsync(cancellationToken: ct).ConfigureAwait(false); }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
            return;
        }

        var noOverwrite = new BlobRequestConditions { IfNoneMatch = ETag.All };
        if (size <= SyncCopyLimit)
        {
            await dest.SyncCopyFromUriAsync(source.Uri,
                new BlobCopyFromUriOptions { Metadata = stamp, SourceAuthentication = await BearerAsync(ct).ConfigureAwait(false), DestinationConditions = noOverwrite },
                ct).ConfigureAwait(false);
        }
        else
        {
            // StartCopyFromUri cannot carry a bearer token for a private same-account source, so large blobs
            // are copied server-side block by block.
            var blocks = BlockPlanner.Plan(size);
            foreach (var block in blocks)
            {
                await dest.StageBlockFromUriAsync(source.Uri, block.BlockId, new StageBlockFromUriOptions
                {
                    SourceRange = new HttpRange(block.Offset, block.Length),
                    SourceAuthentication = await BearerAsync(ct).ConfigureAwait(false),
                }, ct).ConfigureAwait(false);
            }
            await dest.CommitBlockListAsync(blocks.Select(b => b.BlockId),
                new CommitBlockListOptions { Conditions = noOverwrite, Metadata = stamp }, ct).ConfigureAwait(false);
        }
        await source.DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(StorageTarget t, string path, bool isFolder, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var container = clients.Container(t);
            if (!isFolder)
            {
                await container.GetBlobClient(path).DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            var any = false;
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, path + "/", ct).ConfigureAwait(false))
            {
                any = true;
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            if (!any) throw new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);
        }, ct);

    public Task BeginUploadAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct) => Task.CompletedTask;

    public Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset,
        Stream data, long length, byte[] md5, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            await clients.Container(t).GetBlockBlobClient(finalPath).StageBlockAsync(blockId, data,
                new BlockBlobStageBlockOptions
                {
                    TransferValidation = new UploadTransferValidationOptions
                    {
                        ChecksumAlgorithm = StorageChecksumAlgorithm.MD5,
                        PrecalculatedChecksum = md5,
                    },
                }, ct).ConfigureAwait(false);
        }, ct);

    public Task CompleteUploadAsync(StorageTarget t, string stagingPath, string finalPath, IReadOnlyList<string> blockIds,
        bool overwrite, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var options = new CommitBlockListOptions();
            if (!overwrite) options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            await clients.Container(t).GetBlockBlobClient(finalPath).CommitBlockListAsync(blockIds, options, ct).ConfigureAwait(false);
        }, ct);

    public Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct) => Task.CompletedTask;

    public Task ProbeAsync(StorageTarget t, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            await clients.Container(t).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        }, ct);
}
