using System;
using System.Text;
using CloudSelfService.Models;
using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

public class ChunkMathTests
{
    private const long EightMiB = 8L * 1024 * 1024;

    [Fact]
    public void ChunkCount_Zero_IsZero() => Assert.Equal(0, ChunkMath.ChunkCount(0, ChunkMath.MinPieceSize));

    [Fact]
    public void ChunkCount_ExactMultiple() => Assert.Equal(2, ChunkMath.ChunkCount(EightMiB, ChunkMath.MinPieceSize));

    [Fact]
    public void ChunkCount_Remainder() => Assert.Equal(3, ChunkMath.ChunkCount(EightMiB + 1, ChunkMath.MinPieceSize));

    [Fact]
    public void ChunkCount_OneByte_IsOne() => Assert.Equal(1, ChunkMath.ChunkCount(1, ChunkMath.MinPieceSize));

    [Fact]
    public void ExpectedLength_LastChunk_IsRemainder() => Assert.Equal(1, ChunkMath.ExpectedLength(EightMiB + 1, ChunkMath.MinPieceSize, 2));

    [Fact]
    public void ExpectedLength_FullChunk_IsChunkSize() =>
        Assert.Equal(ChunkMath.MinPieceSize, ChunkMath.ExpectedLength(EightMiB + 1, ChunkMath.MinPieceSize, 1));

    [Fact]
    public void ExpectedLength_ExactMultipleLastChunk_IsChunkSize() =>
        Assert.Equal(ChunkMath.MinPieceSize, ChunkMath.ExpectedLength(EightMiB, ChunkMath.MinPieceSize, 1));

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void ExpectedLength_IndexOutOfRange_Throws(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.ExpectedLength(EightMiB + 1, ChunkMath.MinPieceSize, index));

    [Fact]
    public void ExpectedLength_ZeroSize_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.ExpectedLength(0, ChunkMath.MinPieceSize, 0));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PieceArithmetic_NonPositivePieceSize_Throws(int pieceSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.ChunkCount(EightMiB, pieceSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.ExpectedLength(EightMiB, pieceSize, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.Offset(0, pieceSize));
    }

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
    public void BlockId_LastIndexSameLength() =>
        Assert.Equal(ChunkMath.BlockId(Guid.Empty, 0).Length, ChunkMath.BlockId(Guid.Empty, ChunkMath.MaxBlocksPerBlob - 1).Length);

    const long MiB = 1024 * 1024;

    [Theory]
    [InlineData(0L, 4)]
    [InlineData(50_000L * 4 * MiB, 4)]
    [InlineData(50_000L * 4 * MiB + 1, 8)]
    [InlineData(50_000L * 8 * MiB + 1, 16)]
    [InlineData(50_000L * 16 * MiB + 1, 32)]
    [InlineData(50_000L * 32 * MiB, 32)]
    public void PieceSizeFor_Boundaries(long size, int expectedMiB) =>
        Assert.Equal(expectedMiB * (int)MiB, ChunkMath.PieceSizeFor(StorageKind.BlobContainer, size));

    [Fact]
    public void PieceSizeFor_BlobOverMax_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.PieceSizeFor(StorageKind.BlobContainer, ChunkMath.MaxBlobBytes + 1));

    [Fact]
    public void PieceSizeFor_FilesAnySize_IsFourMiB() =>
        Assert.Equal(ChunkMath.MinPieceSize, ChunkMath.PieceSizeFor(StorageKind.FileShare, 2L * 1024 * 1024 * MiB));

    [Fact]
    public void ChunkCount_MaxBlob_IsFiftyThousand() =>
        Assert.Equal(50_000, ChunkMath.ChunkCount(ChunkMath.MaxBlobBytes, ChunkMath.MaxBlobPieceSize));

    [Fact]
    public void ExpectedLength_LastPieceOfLargeBlob_IsRemainder() =>
        Assert.Equal(1, ChunkMath.ExpectedLength(32 * MiB * 3 + 1, 32 * (int)MiB, 3));

    [Fact]
    public void Offset_UsesPieceSize() => Assert.Equal(3 * 16 * MiB, ChunkMath.Offset(3, 16 * (int)MiB));
}
