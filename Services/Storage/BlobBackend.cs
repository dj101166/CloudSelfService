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

    public Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var container = clients.Container(t);
            await foreach (var page in container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/",
                                   StoragePaths.BlobPrefix(folder), ct).AsPages(continuation, pageSize).ConfigureAwait(false))
            {
                var items = new List<StorageItem>();
                foreach (var v in page.Values)
                {
                    if (v.IsPrefix)
                    {
                        var path = StoragePaths.TrimPrefix(v.Prefix);
                        items.Add(new StorageItem(StoragePaths.Leaf(path), path, true, null, null));
                    }
                    else if (!StoragePaths.IsMarker(v.Blob.Name))
                    {
                        items.Add(new StorageItem(StoragePaths.Leaf(v.Blob.Name), v.Blob.Name, false,
                            v.Blob.Properties.ContentLength, v.Blob.Properties.LastModified));
                    }
                }
                return new StorageListing(items, string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
            }
            return new StorageListing(Array.Empty<StorageItem>(), null);
        }, ct);

    public Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct) =>
        StorageErrorMapper.RunAsync<StorageItem?>(async () =>
        {
            if (path.Length == 0) return new StorageItem("", "", true, null, null);
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
            var range = length is null ? new HttpRange(offset) : new HttpRange(offset, length.Value);
            var resp = await clients.Container(t).GetBlobClient(path)
                .DownloadStreamingAsync(new BlobDownloadOptions { Range = range }, ct).ConfigureAwait(false);
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

    public Task MoveAsync(StorageTarget t, string from, string to, bool isFolder, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var container = clients.Container(t);
            var token = await clients.Credential.GetTokenAsync(new TokenRequestContext(StorageScope), ct).ConfigureAwait(false);
            var auth = new HttpAuthorization("Bearer", token.Token);

            if (!isFolder)
            {
                await CopyThenDeleteAsync(container, from, to, auth, ct).ConfigureAwait(false);
                return;
            }

            if (await GetItemAsync(t, to, ct).ConfigureAwait(false) is not null)
                throw new StorageFailureException(StorageFailureKind.AlreadyExists, StorageErrorMapper.AlreadyExistsMessage);

            var names = new List<string>();
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, from + "/", ct).ConfigureAwait(false))
                names.Add(b.Name);
            if (names.Count == 0)
                throw new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);

            foreach (var name in names)
                await CopyThenDeleteAsync(container, name, StoragePaths.Rebase(name, from, to), auth, ct).ConfigureAwait(false);
        }, ct);

    private static async Task CopyThenDeleteAsync(BlobContainerClient container, string from, string to,
        HttpAuthorization auth, CancellationToken ct)
    {
        var source = container.GetBlobClient(from);
        var dest = container.GetBlockBlobClient(to);
        var noOverwrite = new BlobRequestConditions { IfNoneMatch = ETag.All };

        var props = await source.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        if (props.Value.ContentLength > SyncCopyLimit)
        {
            var op = await dest.StartCopyFromUriAsync(source.Uri,
                new BlobCopyFromUriOptions { DestinationConditions = noOverwrite }, ct).ConfigureAwait(false);
            await op.WaitForCompletionAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await dest.SyncCopyFromUriAsync(source.Uri,
                new BlobCopyFromUriOptions { SourceAuthentication = auth, DestinationConditions = noOverwrite }, ct).ConfigureAwait(false);
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
