using System.Threading.RateLimiting;
using CloudSelfService.Services.Transfers;

namespace CloudSelfService.Endpoints;

/// <summary>
/// The per-user chunk bucket, charged one permit per started 4 MiB of a piece. A rate-limiting policy takes one permit
/// per request and runs before the handler, so the bucket is charged by the service instead, once the piece holds its
/// memory budget: a piece refused for budget spends nothing, however long it waits. The partitioned limiter disposes
/// idle, full buckets, so users who stop uploading leave nothing behind.
/// </summary>
public sealed class ChunkThroughputLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _buckets;
    private readonly int _tokenLimit;

    public ChunkThroughputLimiter(IConfiguration config)
    {
        var tokenLimit = Math.Max(1, config.GetValue<int?>("Transfers:ChunksPerMinute") ?? RateLimits.DefaultChunksPerMinute);
        _tokenLimit = tokenLimit;
        _buckets = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetTokenBucketLimiter(key, _ =>
            new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokenLimit,
                TokensPerPeriod = tokenLimit,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    public static int PermitsFor(long? contentLength) =>
        contentLength is { } length && length > 0
            ? (int)Math.Min(int.MaxValue, (length + ChunkMath.MinPieceSize - 1) / ChunkMath.MinPieceSize)
            : 1;

    // Capped at the bucket size: a piece costing more than a whole minute's allowance (a low ChunksPerMinute) would
    // otherwise be refused forever; it empties the bucket instead.
    public bool TryAcquire(HttpContext ctx, long? contentLength)
    {
        using var lease = _buckets.AttemptAcquire(RateLimits.PartitionKey(ctx), Math.Min(PermitsFor(contentLength), _tokenLimit));
        return lease.IsAcquired;
    }

    public void Dispose() => _buckets.Dispose();
}
