using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using CloudSelfService.Services.Identity;

namespace CloudSelfService.Endpoints;

/// <summary>
/// Per-user limits for the transfer endpoints. Downloads and the upload control calls share <see cref="Transfers"/>;
/// chunk PUTs use <see cref="Chunks"/>, concurrency only, because one large file is thousands of chunks
/// (a 10 GB file is 2,560) and a per-minute bucket would throttle a legitimate upload to a crawl.
/// Each in-flight chunk PUT buffers one piece in memory, so the chunk limit is also the memory bound: worst case
/// ChunkSize (4 MiB) x MaxConcurrentChunksPerUser (default 16) = 64 MiB per signed-in user.
/// </summary>
public static class RateLimits
{
    public const string Transfers = "transfers";
    public const string Chunks = "upload-chunks";
    public const string RejectedMessage = "Too many requests. Please wait a moment and try again.";
    public const int DefaultRequestsPerMinute = 120;
    public const int DefaultMaxConcurrentPerUser = 4;
    public const int DefaultMaxConcurrentChunksPerUser = 16;

    public static string PartitionKey(HttpContext ctx) =>
        AppClaims.UserIdOf(ctx.User) is { } id
            ? "user:" + id
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    public static void Configure(RateLimiterOptions options, IConfiguration config)
    {
        var perMinute = Math.Max(1, config.GetValue<int?>("Transfers:RequestsPerMinute") ?? DefaultRequestsPerMinute);
        var concurrent = Math.Max(1, config.GetValue<int?>("Transfers:MaxConcurrentPerUser") ?? DefaultMaxConcurrentPerUser);
        var chunks = Math.Max(1, config.GetValue<int?>("Transfers:MaxConcurrentChunksPerUser") ?? DefaultMaxConcurrentChunksPerUser);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.RetryAfter = "5";
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
        options.AddPolicy<string>(Chunks, ctx => RateLimitPartition.GetConcurrencyLimiter(PartitionKey(ctx),
            _ => new ConcurrencyLimiterOptions { PermitLimit = chunks, QueueLimit = 0 }));
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
