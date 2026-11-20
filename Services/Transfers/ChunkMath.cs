using System.Text;

namespace CloudSelfService.Services.Transfers;

public static class ChunkMath
{
    public const int ChunkSize = 4 * 1024 * 1024;

    public static int ChunkCount(long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return (int)((size + ChunkSize - 1) / ChunkSize);
    }

    public static long ExpectedLength(long size, int index)
    {
        var count = ChunkCount(size);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        return index < count - 1 ? ChunkSize : size - (long)ChunkSize * (count - 1);
    }

    // Azure requires every block ID in a blob to be the same length.
    public static string BlockId(Guid sessionId, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sessionId:N}-{index:D6}"));
}
