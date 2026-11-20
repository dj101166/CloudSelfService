using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using CloudSelfService.Services.Identity;

namespace CloudSelfService.Endpoints;

/// <summary>Per-user limits for the transfer endpoints (downloads now, uploads in Task 9).</summary>
public static class RateLimits
{
    public const string Transfers = "transfers";
    public const string RejectedMessage = "Too many requests. Please wait a moment and try again.";
    public const int DefaultRequestsPerMinute = 120;
    public const int DefaultMaxConcurrentPerUser = 4;

    public static string PartitionKey(HttpContext ctx) =>
        AppClaims.UserIdOf(ctx.User) is { } id
            ? "user:" + id
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    public static void Configure(RateLimiterOptions options, IConfiguration config)
    {
        var perMinute = Math.Max(1, config.GetValue<int?>("Transfers:RequestsPerMinute") ?? DefaultRequestsPerMinute);
        var concurrent = Math.Max(1, config.GetValue<int?>("Transfers:MaxConcurrentPerUser") ?? DefaultMaxConcurrentPerUser);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
            await context.HttpContext.Response.WriteAsync(RejectedMessage, ct);
        };
        options.AddPolicy<string>(Transfers, ctx => RateLimitPartition.Get(PartitionKey(ctx), _ => new ChainedLimiter(
            new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = perMinute,
                TokensPerPeriod = perMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }),
            new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = concurrent, QueueLimit = 0 }))));
    }

    /// <summary>Both limiters must grant a permit; the concurrency permit is held until the request ends.</summary>
    private sealed class ChainedLimiter(RateLimiter first, RateLimiter second) : RateLimiter
    {
        public override TimeSpan? IdleDuration => Min(first.IdleDuration, second.IdleDuration);

        public override RateLimiterStatistics? GetStatistics() => second.GetStatistics();

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
