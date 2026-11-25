using System.Text;
using CloudSelfService.Models;

namespace CloudSelfService.Services.Transfers;

public static class ChunkMath
{
    public const int MinPieceSize = 4 * 1024 * 1024;
    public const int MaxBlobPieceSize = 32 * 1024 * 1024;
    public const int FingerprintBytes = MinPieceSize;

    // Azure allows 50,000 committed blocks per blob; this is also what keeps the six-digit block-id index wide enough.
    public const int MaxBlocksPerBlob = 50_000;
    public const long MaxBlobBytes = (long)MaxBlobPieceSize * MaxBlocksPerBlob;

    public static int PieceSizeFor(StorageKind kind, long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (kind == StorageKind.FileShare) return MinPieceSize;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxBlobBytes);
        for (var piece = MinPieceSize; piece < MaxBlobPieceSize; piece *= 2)
            if (ChunkCount(size, piece) <= MaxBlocksPerBlob) return piece;
        return MaxBlobPieceSize;
    }

    public static int ChunkCount(long size, int pieceSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return (int)((size + pieceSize - 1) / pieceSize);
    }

    public static long ExpectedLength(long size, int pieceSize, int index)
    {
        var count = ChunkCount(size, pieceSize);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        return index < count - 1 ? pieceSize : size - (long)pieceSize * (count - 1);
    }

    public static long Offset(int index, int pieceSize) => (long)index * pieceSize;

    // Azure requires every block ID in a blob to be the same length.
    public static string BlockId(Guid sessionId, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sessionId:N}-{index:D6}"));
}
