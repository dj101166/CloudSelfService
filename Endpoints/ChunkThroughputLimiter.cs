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

    public const int MaxRetryAfterSeconds = 60;
    public const int FallbackRetryAfterSeconds = 5;

    public bool TryAcquire(HttpContext ctx, long? contentLength) => TryAcquire(ctx, contentLength, out _);

    /// <param name="retryAfterSeconds">On refusal, how long until the bucket holds enough for this piece; 0 when charged.</param>
    public bool TryAcquire(HttpContext ctx, long? contentLength, out int retryAfterSeconds)
    {
        var key = RateLimits.PartitionKey(ctx);
        // Capped at the bucket size: a piece costing more than a whole minute's allowance (a low ChunksPerMinute) would
        // otherwise be refused forever; it empties the bucket instead.
        var permits = Math.Min(PermitsFor(contentLength), _tokenLimit);
        using var lease = _buckets.AttemptAcquire(key, permits);
        if (lease.IsAcquired)
        {
            retryAfterSeconds = 0;
            return true;
        }
        lease.TryGetMetadata(MetadataName.RetryAfter, out var leaseRetryAfter);
        retryAfterSeconds = RetryAfterSeconds(permits, _buckets.GetStatistics(key)?.CurrentAvailablePermits, _tokenLimit,
            leaseRetryAfter);
        return false;
    }

    /// <summary>
    /// Seconds until a bucket refilling <paramref name="tokensPerMinute"/> evenly holds <paramref name="permits"/>, rounded
    /// up and kept to 1..60. The lease's own RetryAfter is a whole replenishment period (a minute here), so it is only the
    /// fallback when the bucket's level is unknown, and 5 seconds when neither is.
    /// </summary>
    public static int RetryAfterSeconds(int permits, long? available, int tokensPerMinute, TimeSpan? leaseRetryAfter)
    {
        double seconds;
        if (available is { } have)
            seconds = Math.Max(1, permits - have) * 60.0 / Math.Max(1, tokensPerMinute);
        else if (leaseRetryAfter is { } fromLease)
            seconds = fromLease.TotalSeconds;
        else
            return FallbackRetryAfterSeconds;
        return (int)Math.Clamp(Math.Ceiling(seconds), 1, MaxRetryAfterSeconds);
    }

    public void Dispose() => _buckets.Dispose();
}
