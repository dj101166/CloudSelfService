using CloudSelfService.Models;

namespace CloudSelfService.Services.Storage;

/// <summary>Raw storage access. Does no authorization; paths arrive already normalised ("" = root).</summary>
public interface IStorageBackend
{
    Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct);
    Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct);
    /// <summary>When <paramref name="expectedETag"/> is set and the stored item has changed, fails with a Conflict instead of reading it.</summary>
    Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct, string? expectedETag = null);
    Task CreateFolderAsync(StorageTarget t, string path, CancellationToken ct);
    Task MoveAsync(StorageTarget t, string from, string to, bool isFolder, CancellationToken ct);
    Task DeleteAsync(StorageTarget t, string path, bool isFolder, CancellationToken ct);
    Task BeginUploadAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct);
    Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset, Stream data, long length, byte[] md5, CancellationToken ct);
    /// <summary>Blob stamps <paramref name="uploadSessionId"/> into the committed blob's metadata so a zero-byte commit can be recognised later.</summary>
    Task CompleteUploadAsync(StorageTarget t, string stagingPath, string finalPath, IReadOnlyList<string> blockIds, bool overwrite,
        CancellationToken ct, Guid? uploadSessionId = null);

    /// <summary>
    /// Whether this session's commit definitely landed. Files: the staging file is gone and the final file exists (the
    /// rename is atomic). Blob: the final blob's committed block list starts with this session's block 0, or, for a
    /// zero-byte session, it has no blocks and carries this session's metadata stamp.
    /// </summary>
    Task<bool> HasLandedAsync(StorageTarget t, string stagingPath, string finalPath, Guid sessionId, long size, CancellationToken ct);
    Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct);
    Task ProbeAsync(StorageTarget t, CancellationToken ct);
}

public interface IStorageBackendResolver { IStorageBackend For(StorageTarget t); }

public sealed class StorageBackendResolver(FileShareBackend files, BlobBackend blobs) : IStorageBackendResolver
{
    public IStorageBackend For(StorageTarget t) => t.Kind switch
    {
        StorageKind.FileShare => files,
        StorageKind.BlobContainer => blobs,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t.Kind, "Unknown storage kind"),
    };
}

public sealed class FixedBackendResolver(IStorageBackend backend) : IStorageBackendResolver
{
    public IStorageBackend For(StorageTarget t) => backend;
}
