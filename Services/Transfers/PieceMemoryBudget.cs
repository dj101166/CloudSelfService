using System.Diagnostics.CodeAnalysis;

namespace CloudSelfService.Services.Transfers;

/// <summary>
/// Bounds the bytes held in upload piece buffers, app-wide (Transfers:MaxInFlightPieceBytes) and per user
/// (Transfers:MaxInFlightPieceBytesPerUser). A piece is reserved before its buffer is rented and released once it has
/// been written or has failed. Never blocks: a refused piece is answered with a wait and sent again.
/// </summary>
public sealed class PieceMemoryBudget
{
    public const long DefaultMaxInFlightPieceBytes = 268_435_456;
    public const long DefaultMaxInFlightPieceBytesPerUser = 67_108_864;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, long> _perUser = new();
    private long _total;

    public PieceMemoryBudget(IConfiguration config)
    {
        MaxBytes = Math.Max(1, config.GetValue<long?>("Transfers:MaxInFlightPieceBytes") ?? DefaultMaxInFlightPieceBytes);
        MaxBytesPerUser = Math.Max(1, config.GetValue<long?>("Transfers:MaxInFlightPieceBytesPerUser") ?? DefaultMaxInFlightPieceBytesPerUser);
    }

    public long MaxBytes { get; }
    public long MaxBytesPerUser { get; }

    /// <summary>
    /// Reserves <paramref name="bytes"/> against both budgets, or neither. A piece larger than a budget is admitted when
    /// that budget is otherwise empty, so a lowered setting slows the largest pieces down instead of refusing them forever.
    /// </summary>
    public bool TryReserve(Guid userId, long bytes, [NotNullWhen(true)] out IDisposable? lease)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            var mine = _perUser.GetValueOrDefault(userId);
            if (!Fits(_total, bytes, MaxBytes) || !Fits(mine, bytes, MaxBytesPerUser))
            {
                lease = null;
                return false;
            }
            _total += bytes;
            _perUser[userId] = mine + bytes;
        }
        lease = new Lease(this, userId, bytes);
        return true;
    }

    private static bool Fits(long used, long bytes, long max) => used == 0 || used + bytes <= max;

    private void Release(Guid userId, long bytes)
    {
        lock (_gate)
        {
            _total -= bytes;
            var left = _perUser.GetValueOrDefault(userId) - bytes;
            // Removed at zero so users who have finished uploading do not accumulate entries.
            if (left <= 0) _perUser.Remove(userId);
            else _perUser[userId] = left;
        }
    }

    private sealed class Lease(PieceMemoryBudget budget, Guid userId, long bytes) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                budget.Release(userId, bytes);
        }
    }
}
