using CloudSelfService.Services.Data;

namespace CloudSelfService.Tests.Infrastructure;

/// <summary>
/// An upload session store whose database is down: every request-path call throws. The sweep's list calls return
/// nothing so the hosted cleanup service stays quiet when the app is started with this store.
/// </summary>
internal sealed class ThrowingSessionStore : IUploadSessionStore
{
    private static Exception Down() => new InvalidOperationException("db down");
    private static Task<IReadOnlyList<UploadSession>> None() => Task.FromResult<IReadOnlyList<UploadSession>>(Array.Empty<UploadSession>());

    public Task CreateAsync(UploadSession s, CancellationToken ct = default) => throw Down();
    public Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default) => throw Down();
    public Task<UploadSession?> FindResumableAsync(Guid userId, Guid targetId, string finalPath, long size, DateTimeOffset now,
        CancellationToken ct = default) => throw Down();
    public Task MarkChunkAsync(Guid id, int index, DateTimeOffset at, CancellationToken ct = default) => throw Down();
    public Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default) => throw Down();
    public Task SetStatusAsync(Guid id, string status, CancellationToken ct = default) => throw Down();
    public Task<bool> TrySetStatusAsync(Guid id, string expected, string next, DateTimeOffset? claimedAt = null,
        CancellationToken ct = default, bool requireClaim = false, DateTimeOffset? expectedClaim = null) => throw Down();
    public Task<IReadOnlyList<UploadSession>> ListActiveForPathAsync(Guid targetId, string finalPath, DateTimeOffset now,
        CancellationToken ct = default) => throw Down();
    public Task<IReadOnlyList<UploadSession>> ListOpenForUserPathAsync(Guid userId, Guid targetId, string finalPath,
        CancellationToken ct = default) => throw Down();
    public Task<IReadOnlyList<UploadSession>> ListStuckCompletingAsync(DateTimeOffset claimedBefore, CancellationToken ct = default) => None();
    public Task SetOverwriteAsync(Guid id, bool overwrite, bool existedAtBegin, CancellationToken ct = default) => throw Down();
    public Task<IReadOnlyList<UploadSession>> ListSweepableOpenAsync(DateTimeOffset now, DateTimeOffset idleBefore,
        CancellationToken ct = default) => None();
    public Task<IReadOnlyList<UploadSession>> ListAbortedWithStagingAsync(CancellationToken ct = default) => None();
    public Task SetStagingRemovedAsync(Guid id, CancellationToken ct = default) => throw Down();
    public Task SetCleanupRefusedAsync(Guid id, CancellationToken ct = default) => throw Down();
    public Task<int> CountOpenForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default) => throw Down();
    public Task<long> SumOpenSizeForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default) => throw Down();
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw Down();
    public Task EnsureStagingSizeAsync(Guid id, long size, Func<CancellationToken, Task> grow, CancellationToken ct = default) => throw Down();
    public Task<(int Deleted, IReadOnlyList<AbandonedStaging> Abandoned)> PurgeAsync(DateTimeOffset cutoff, DateTimeOffset hardCutoff,
        CancellationToken ct = default) => Task.FromResult<(int, IReadOnlyList<AbandonedStaging>)>((0, Array.Empty<AbandonedStaging>()));
}
