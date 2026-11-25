using System.Globalization;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Files;

namespace CloudSelfService.Services.Transfers;

/// <summary>The upload limit in force, what it would be without an override, the most an Admin may set, and the override itself.</summary>
public sealed record UploadLimitState(long Effective, long Default, long Ceiling, UploadLimitOverride? Override, bool OverrideActive);

/// <summary>
/// The upload size limit: the configured default, or an Admin's override while it is unexpired, never above the configured
/// ceiling. Expiry is evaluated on every read, so nothing has to run when an override ends.
/// </summary>
public sealed class UploadLimitService
{
    public const long DefaultMaxUploadBytes = 107_374_182_400;
    public const long DefaultCeilingBytes = 1_099_511_627_776;

    // Other instances learn of a change only when their cache lapses, so this bounds how long they lag.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly IUploadLimitStore _store;
    private readonly TimeProvider _time;
    private readonly long _ceiling;
    private readonly long _default;
    private volatile Cached? _cached;
    private long _generation;

    private sealed record Cached(UploadLimitOverride? Override, DateTimeOffset ReadAt);

    public UploadLimitService(IUploadLimitStore store, TimeProvider time, IConfiguration config)
    {
        _store = store;
        _time = time;
        (_ceiling, _, _default, _) = LimitsFrom(config);
    }

    /// <summary>
    /// The ceiling (Transfers:MaxUploadBytesCeiling, at most the largest blob the portal can stage) and the default
    /// (Transfers:MaxUploadBytes, at most the ceiling), each with the configured value so Program can warn once when clamped.
    /// </summary>
    public static (long Ceiling, long ConfiguredCeiling, long Default, long ConfiguredDefault) LimitsFrom(IConfiguration config)
    {
        var configuredCeiling = Math.Max(0, config.GetValue<long?>("Transfers:MaxUploadBytesCeiling") ?? DefaultCeilingBytes);
        var ceiling = Math.Min(configuredCeiling, ChunkMath.MaxBlobBytes);
        var configuredDefault = Math.Max(0, config.GetValue<long?>("Transfers:MaxUploadBytes") ?? DefaultMaxUploadBytes);
        return (ceiling, configuredCeiling, Math.Min(configuredDefault, ceiling), configuredDefault);
    }

    /// <summary>Binary gigabytes with up to two decimals, as every limit is shown and audited.</summary>
    public static string FormatGb(long bytes) =>
        (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Whole gigabytes as bytes, saturating rather than overflowing so an absurd entry is refused as out of range.</summary>
    public static long BytesFromGb(long gb) => gb > long.MaxValue >> 30 ? long.MaxValue : gb < 0 ? -1 : gb << 30;

    public async Task<UploadLimitState> GetStateAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var cached = _cached;
        if (cached is null || now - cached.ReadAt >= CacheLifetime || now < cached.ReadAt)
        {
            var generation = Interlocked.Read(ref _generation);
            cached = new Cached(await _store.GetAsync(ct), now);
            // An Invalidate that ran during the read means this value may predate the change; use it once, don't keep it.
            if (Interlocked.Read(ref _generation) == generation)
                _cached = cached;
        }

        var o = cached.Override;
        var active = o is not null && (o.ExpiresAt is null || o.ExpiresAt > now);
        return new UploadLimitState(active ? Math.Min(o!.Bytes, _ceiling) : _default, _default, _ceiling, o, active);
    }

    /// <summary>Drops the cached override, so a change made on this instance is seen by the very next read.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _cached = null;
    }

    public Task SetOverrideAsync(UploadLimitOverride o, CancellationToken ct = default) => _store.SetAsync(o, ct);

    public Task ClearOverrideAsync(CancellationToken ct = default) => _store.ClearAsync(ct);

    /// <summary>Where the limit in force comes from, with times in the viewer's zone (UTC, labelled, when it is unknown).</summary>
    public static string Describe(UploadLimitState state, TimeZoneInfo? zone)
    {
        if (!state.OverrideActive || state.Override is not { } o)
            return "Default from configuration";
        var end = o.ExpiresAt is { } until ? $"until {FileDisplay.Modified(until, zone)}" : "permanent";
        return $"Set by {o.SetByUpn} on {FileDisplay.Modified(o.SetAt, zone)}, {end}";
    }

    /// <summary>
    /// A wall-clock time the viewer entered, in their zone (UTC when it is unknown). A time skipped or repeated by a
    /// daylight-saving change takes the zone's standard offset rather than failing.
    /// </summary>
    public static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo? zone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = zone is null ? TimeSpan.Zero : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    /// <summary>The wall-clock time in the viewer's zone (UTC when it is unknown), for a datetime-local input.</summary>
    public static DateTime UtcToLocal(DateTimeOffset at, TimeZoneInfo? zone) =>
        zone is null ? at.UtcDateTime : TimeZoneInfo.ConvertTime(at, zone).DateTime;
}
