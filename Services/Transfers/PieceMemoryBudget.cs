using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace CloudSelfService.Services.Transfers;

/// <summary>
/// Bounds the bytes held in upload piece buffers, app-wide (Transfers:MaxInFlightPieceBytes) and per user
/// (Transfers:MaxInFlightPieceBytesPerUser). A piece is reserved before its buffer is rented and released once it has
/// been written or has failed. Never blocks: a refused piece is answered with a wait and sent again. Also owns the pool
/// piece buffers are rented from, sized so the arrays it keeps between pieces stay near the same budget.
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
        MaxArraysPerBucket = ArraysPerBucketFor(MaxBytes);
        Buffers = ArrayPool<byte>.Create(ChunkMath.MaxBlobPieceSize, MaxArraysPerBucket);
    }

    public long MaxBytes { get; }
    public long MaxBytesPerUser { get; }

    /// <summary>
    /// Piece buffers. Not ArrayPool.Shared: it keeps arrays per core with no overall bound, so 32 MiB pieces could leave
    /// far more than the budget retained after a busy spell. Return with clearArray: true, since a buffer holds file data.
    /// </summary>
    public ArrayPool<byte> Buffers { get; }

    /// <summary>
    /// How many arrays the pool keeps per size: the budget divided by the largest piece, at least 1. The 32 MiB size then
    /// keeps at most the budget, each smaller size half the one above, so the pool never holds twice the budget however
    /// sizes mix. Pieces beyond what a size keeps are allocated and collected as before.
    /// </summary>
    public int MaxArraysPerBucket { get; }

    public static int ArraysPerBucketFor(long maxBytes) =>
        (int)Math.Clamp(maxBytes / ChunkMath.MaxBlobPieceSize, 1, 1024);

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

    private static bool Fits(long used, long bytes, long max) => bytes == 0 || used == 0 || used + bytes <= max;

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
