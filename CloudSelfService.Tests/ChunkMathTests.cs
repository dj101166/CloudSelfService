using System;
using System.Text;
using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

public class ChunkMathTests
{
    private const long EightMiB = 8L * 1024 * 1024;

    [Fact]
    public void ChunkCount_Zero_IsZero() => Assert.Equal(0, ChunkMath.ChunkCount(0));

    [Fact]
    public void ChunkCount_ExactMultiple() => Assert.Equal(2, ChunkMath.ChunkCount(EightMiB));

    [Fact]
    public void ChunkCount_Remainder() => Assert.Equal(3, ChunkMath.ChunkCount(EightMiB + 1));

    [Fact]
    public void ChunkCount_OneByte_IsOne() => Assert.Equal(1, ChunkMath.ChunkCount(1));

    [Fact]
    public void ExpectedLength_LastChunk_IsRemainder() => Assert.Equal(1, ChunkMath.ExpectedLength(EightMiB + 1, 2));

    [Fact]
    public void ExpectedLength_FullChunk_IsChunkSize() =>
        Assert.Equal(ChunkMath.ChunkSize, ChunkMath.ExpectedLength(EightMiB + 1, 1));

    [Fact]
    public void ExpectedLength_ExactMultipleLastChunk_IsChunkSize() =>
        Assert.Equal(ChunkMath.ChunkSize, ChunkMath.ExpectedLength(EightMiB, 1));

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void ExpectedLength_IndexOutOfRange_Throws(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.ExpectedLength(EightMiB + 1, index));

    [Fact]
    public void ExpectedLength_ZeroSize_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.ExpectedLength(0, 0));

    [Fact]
    public void BlockId_AllIndicesSameLength()
    {
        var id = Guid.NewGuid();
        Assert.Equal(ChunkMath.BlockId(id, 0).Length, ChunkMath.BlockId(id, 999_999).Length);
    }

    [Fact]
    public void BlockId_DecodesToSessionAndPaddedIndex()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(ChunkMath.BlockId(id, 42)));
        Assert.Equal("11111111222233334444555555555555-000042", decoded);
    }

    [Fact]
    public void ChunkCount_EffectiveMaxUpload_WithinBlobBlockLimit()
    {
        Assert.Equal(50_000, ChunkMath.ChunkCount(ChunkMath.MaxUploadBytes));
        Assert.Equal(50_001, ChunkMath.ChunkCount(ChunkMath.MaxUploadBytes + 1));
        Assert.Equal(ChunkMath.BlockId(Guid.Empty, 0).Length, ChunkMath.BlockId(Guid.Empty, ChunkMath.MaxBlocksPerBlob - 1).Length);
    }
}
