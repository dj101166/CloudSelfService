### Task 1: Piece-size selection in `ChunkMath`

**Files:**
- Modify: `Services/Transfers/ChunkMath.cs`
- Test: `CloudSelfService.Tests/ChunkMathTests.cs`

**Interfaces:**
- Produces (all `public` on static class `ChunkMath`):
  - `const int MinPieceSize = 4 * 1024 * 1024`, `const int MaxBlobPieceSize = 32 * 1024 * 1024`, `const int FingerprintBytes = MinPieceSize`, `const int MaxBlocksPerBlob = 50_000`
  - `const long MaxBlobBytes = (long)MaxBlobPieceSize * MaxBlocksPerBlob` (1,677,721,600,000)
  - `int PieceSizeFor(StorageKind kind, long size)`: Files → `MinPieceSize`; Blob → smallest of 4/8/16/32 MiB with `ChunkCount(size, p) <= 50_000`; throws `ArgumentOutOfRangeException` when size < 0 or Blob size > `MaxBlobBytes`.
  - `int ChunkCount(long size, int pieceSize)`, `long ExpectedLength(long size, int pieceSize, int index)`, `long Offset(int index, int pieceSize)`
  - `string BlockId(Guid sessionId, int index)` unchanged.
- Removes `ChunkSize`, `MaxUploadBytes` and the one-argument overloads. This task leaves the build green by mechanical replacement in every caller (including tests): `ChunkSize` → `MinPieceSize`, `MaxUploadBytes` → `MaxBlobBytes`, one-argument calls → pass `ChunkMath.MinPieceSize`. Behaviour changes in callers belong to Tasks 2 and 4.

- [ ] **Step 1: Write failing tests** in `ChunkMathTests.cs`:

```csharp
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
```

Update the existing `ChunkMathTests` cases to the two-argument forms with `ChunkMath.MinPieceSize`.

- [ ] **Step 2: Run** `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj --filter ChunkMathTests`. Expected: compile failure (missing members).
- [ ] **Step 3: Implement** the members in `ChunkMath.cs` and the mechanical caller replacements.
- [ ] **Step 4: Run** the full test project. Expected: all pass.
- [ ] **Step 5: Commit** the changed files: `Choose upload piece size per file size and storage kind`.

