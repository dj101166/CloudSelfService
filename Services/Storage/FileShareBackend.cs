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
            await foreach (var page in dir.GetFilesAndDirectoriesAsync(cancellationToken: ct)
                               .AsPages(continuation, pageSize).ConfigureAwait(false))
            {
                var items = page.Values.Select(i => new StorageItem(i.Name, StoragePaths.Join(folder, i.Name), i.IsDirectory,
                    i.IsDirectory ? null : i.FileSize, null)).ToList();
                return new StorageListing(items, string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
            }
            return new StorageListing(Array.Empty<StorageItem>(), null);
        }, ct);

    public Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct) =>
        StorageErrorMapper.RunAsync<StorageItem?>(async () =>
        {
            var share = clients.Share(t);
            if (path.Length == 0) return new StorageItem("", "", true, null, null);
            try
            {
                var props = await File(share, path).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
                return new StorageItem(StoragePaths.Leaf(path), path, false, props.Value.ContentLength, props.Value.LastModified);
            }
            catch (RequestFailedException ex) when (ex.Status == 404 || ex.ErrorCode == "ResourceTypeMismatch") { }

            try
            {
                var props = await share.GetDirectoryClient(path).GetPropertiesAsync(ct).ConfigureAwait(false);
                return new StorageItem(StoragePaths.Leaf(path), path, true, null, props.Value.LastModified);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
        }, ct);

    public Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var range = length is null ? new HttpRange(offset) : new HttpRange(offset, length.Value);
            var resp = await File(clients.Share(t), path)
                .DownloadAsync(new ShareFileDownloadOptions { Range = range }, ct).ConfigureAwait(false);
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
            var share = clients.Share(t);
            await EnsureFoldersAsync(share, StoragePaths.Parent(to), ct).ConfigureAwait(false);
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

    public Task BeginUploadAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var share = clients.Share(t);
            await EnsureFoldersAsync(share, StoragePaths.Parent(stagingPath), ct).ConfigureAwait(false);
            await File(share, stagingPath).CreateAsync(size, cancellationToken: ct).ConfigureAwait(false);
        }, ct);

    public Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset,
        Stream data, long length, byte[] md5, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            await File(clients.Share(t), stagingPath).UploadRangeAsync(new HttpRange(offset, length), data,
                new ShareFileUploadRangeOptions { TransactionalContentHash = md5 }, ct).ConfigureAwait(false);
        }, ct);

    public Task CompleteUploadAsync(StorageTarget t, string stagingPath, string finalPath, IReadOnlyList<string> blockIds,
        bool overwrite, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            var share = clients.Share(t);
            await EnsureFoldersAsync(share, StoragePaths.Parent(finalPath), ct).ConfigureAwait(false);
            await File(share, stagingPath).RenameAsync(finalPath,
                new ShareFileRenameOptions { ReplaceIfExists = overwrite }, ct).ConfigureAwait(false);
        }, ct);

    public Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            await File(clients.Share(t), stagingPath).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }, ct);

    public Task ProbeAsync(StorageTarget t, CancellationToken ct) =>
        StorageErrorMapper.RunAsync(async () =>
        {
            await clients.Share(t).GetRootDirectoryClient().GetPropertiesAsync(ct).ConfigureAwait(false);
        }, ct);
}
