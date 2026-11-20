using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Transfers;

namespace CloudSelfService.Endpoints;

/// <summary>
/// Limits for the transfer endpoints. Downloads and the upload control calls share <see cref="Transfers"/> (per user:
/// concurrency, then a 120/min bucket). Chunk PUTs use <see cref="Chunks"/>, because one large file is thousands of
/// chunks (a 10 GB file is 2,560): a global concurrency cap, then per-user concurrency, then a per-user bucket of
/// Transfers:ChunksPerMinute (default 600, about 40 MB/s of 4 MiB pieces). Each in-flight chunk PUT buffers one piece,
/// so the concurrency caps are the memory bound: ChunkSize (4 MiB) x MaxConcurrentChunksPerUser (16) = 64 MiB per user,
/// and ChunkSize x MaxConcurrentChunksTotal (64) = 256 MiB for the whole app.
/// </summary>
public static class RateLimits
{
    public const string Transfers = "transfers";
    public const string Chunks = "upload-chunks";
    public const string RejectedMessage = "Too many requests. Please wait a moment and try again.";
    public const int DefaultRequestsPerMinute = 120;
    public const int DefaultMaxConcurrentPerUser = 4;
    public const int DefaultMaxConcurrentChunksPerUser = 16;
    public const int DefaultChunksPerMinute = 600;
    // Below Npgsql's default MaxPoolSize (100): every chunk PUT can hold a pooled connection while its staging grows.
    public const int DefaultMaxConcurrentChunksTotal = 64;

    public static string PartitionKey(HttpContext ctx) =>
        AppClaims.UserIdOf(ctx.User) is { } id
            ? "user:" + id
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    public static void Configure(RateLimiterOptions options, IConfiguration config)
    {
        var perMinute = Math.Max(1, config.GetValue<int?>("Transfers:RequestsPerMinute") ?? DefaultRequestsPerMinute);
        var concurrent = Math.Max(1, config.GetValue<int?>("Transfers:MaxConcurrentPerUser") ?? DefaultMaxConcurrentPerUser);
        var chunks = Math.Max(1, config.GetValue<int?>("Transfers:MaxConcurrentChunksPerUser") ?? DefaultMaxConcurrentChunksPerUser);
        var chunksPerMinute = Math.Max(1, config.GetValue<int?>("Transfers:ChunksPerMinute") ?? DefaultChunksPerMinute);
        var chunksTotal = Math.Max(1, config.GetValue<int?>("Transfers:MaxConcurrentChunksTotal") ?? DefaultMaxConcurrentChunksTotal);
        // One limiter instance shared by every partition, so it bounds the whole app rather than each user.
        var globalChunks = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = chunksTotal, QueueLimit = 0 });

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.RetryAfter = "5";
            // uploader.js branches on a JSON code; downloads are plain browser navigations and keep text.
            if (context.HttpContext.Request.Path.StartsWithSegments("/api/uploads"))
            {
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = RejectedMessage, code = UploadErrorCodes.Busy }, ct);
                return;
            }
            context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
            await context.HttpContext.Response.WriteAsync(RejectedMessage, ct);
        };
        options.AddPolicy<string>(Transfers, ctx => RateLimitPartition.Get(PartitionKey(ctx), _ => new ChainedLimiter(
            new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = concurrent, QueueLimit = 0 }),
            new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = perMinute,
                TokensPerPeriod = perMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }))));
        options.AddPolicy<string>(Chunks, ctx => RateLimitPartition.Get(PartitionKey(ctx), _ => new ChainedLimiter(
            new SharedLimiter(globalChunks),
            new ChainedLimiter(
                new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = chunks, QueueLimit = 0 }),
                new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = chunksPerMinute,
                    TokensPerPeriod = chunksPerMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })))));
    }

    /// <summary>
    /// Wraps a limiter shared across partitions. Disposing a partition's limiter (idle cleanup) must not dispose the
    /// shared one, and the shared limiter must never keep a partition alive: it reports itself as idle forever, so a
    /// chain's idle time is its per-user limiters' idle time.
    /// </summary>
    private sealed class SharedLimiter(RateLimiter inner) : RateLimiter
    {
        public override TimeSpan? IdleDuration => TimeSpan.MaxValue;
        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();
        protected override RateLimitLease AttemptAcquireCore(int permitCount) => inner.AttemptAcquire(permitCount);
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken ct) =>
            inner.AcquireAsync(permitCount, ct);
    }

    /// <summary>Both limiters must grant a permit; concurrency is tried first so a request it rejects does not spend a bucket token.</summary>
    private sealed class ChainedLimiter(RateLimiter first, RateLimiter second) : RateLimiter
    {
        public override TimeSpan? IdleDuration => Min(first.IdleDuration, second.IdleDuration);

        public override RateLimiterStatistics? GetStatistics() => first.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            var a = first.AttemptAcquire(permitCount);
            if (!a.IsAcquired) return a;
            var b = second.AttemptAcquire(permitCount);
            if (!b.IsAcquired)
            {
                a.Dispose();
                return b;
            }
            return new ChainedLease(a, b);
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken ct) =>
            ValueTask.FromResult(AttemptAcquireCore(permitCount));

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            first.Dispose();
            second.Dispose();
        }

        private static TimeSpan? Min(TimeSpan? a, TimeSpan? b) =>
            a is null || b is null ? null : a < b ? a : b;

        private sealed class ChainedLease(RateLimitLease a, RateLimitLease b) : RateLimitLease
        {
            public override bool IsAcquired => true;
            public override IEnumerable<string> MetadataNames => Array.Empty<string>();
            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                metadata = null;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (!disposing) return;
                a.Dispose();
                b.Dispose();
            }
        }
    }
}
