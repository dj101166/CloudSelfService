### Task 2: Sessions carry their own piece size

**Files:**
- Modify: `Services/Transfers/UploadSessionService.cs`, `Endpoints/UploadEndpoints.cs`, `wwwroot/js/uploader.js`
- Test: `CloudSelfService.Tests/UploadSessionServiceTests.cs`, `CloudSelfService.Tests/UploadEndpointTests.cs`

**Interfaces:**
- Consumes: Task 1 `ChunkMath`.
- Produces:
  - `BeginAsync` stores `ChunkMath.PieceSizeFor(target.Kind, req.Size)` in `UploadSession.ChunkSize` and returns it in `BeginUploadResponse.ChunkSize`; `ChunkCount` in the response uses it. `ParallelChunks` = `_parallel` for 4 MiB pieces, else `Math.Clamp(16 MiB / pieceSize, 2, _parallel)`. A resumed session returns its **stored** `ChunkSize`.
  - `CheckChunkFormat(int index, long? contentLength, string? md5)` caps at `ChunkMath.MaxBlobPieceSize`. `PutChunkAsync` uses `ChunkCount(session.Size, session.ChunkSize)`, `ExpectedLength(session.Size, session.ChunkSize, index)` and writes at `ChunkMath.Offset(index, session.ChunkSize)`. `CompleteAsync` counts with `session.ChunkSize`.
  - Piece read timeout per piece: `max(PieceReadTimeout, TimeSpan.FromSeconds((double)expected / minRate))`, `minRate` = `Transfers:MinChunkBytesPerSecond` (default 65,536). `PieceReadTimeout` stays the settable floor for tests.
  - `UploadEndpoints.GuardAsync`: chunk body limit and the 413 check use `ChunkMath.MaxBlobPieceSize`; `TooLargeMessage` = `"An upload piece is larger than the 32 MiB maximum piece size."`

- [ ] **Step 1: Write failing tests** (`UploadSessionServiceTests` unless noted):
  - `Begin_LargeBlob_ReturnsLargerPieceSize`: Blob target, size `50_000L * 4 MiB + 1`, `maxBytes` above it → `ChunkSize == 8 MiB`, `ChunkCount == 25_001`, `ParallelChunks == 2`.
  - `Begin_LargeFile_StaysFourMiB`: Files target, same size → `ChunkSize == 4 MiB`, `ChunkCount == 50_001`.
  - `PutChunk_LargePieceSession_AcceptsFullPieceAndWritesAtOffset`: Blob session row created directly in the store with `ChunkSize: 8 MiB`, `Size: 16 MiB + 10`. PUT index 1 with 8 MiB → Ok and the fake backend's block for index 1 holds those bytes. PUT index 1 with 4 MiB → `Invalid`, message `"Piece 1 must be exactly 8388608 bytes."`
  - `PutChunk_LegacySession_UsesStoredPieceSize`: Blob session row with `Size = 50_000L * 4 MiB + 1` and stored `ChunkSize: 4 MiB` (where `PieceSizeFor` would now pick 8 MiB). PUT index 50,000 with length 1 → Ok. Complete after all pieces → committed block list has 50,001 IDs. Use a fake backend that doesn't store bytes for this case, or mark chunks received directly in the store and PUT only the last.
  - `UploadEndpointTests.PutChunk_OverMaxPiece_Returns413`: `Content-Length: 32 MiB + 1` → 413, code `too_large`.
- [ ] **Step 2: Run** the filtered tests. Expected: failures.
- [ ] **Step 3: Implement** the service and endpoint changes. In `uploader.js`, rename `CHUNK_SIZE` to `FINGERPRINT_BYTES` (value 4194304), used only by `firstPieceMd5`; its comment says the fingerprint is always the first 4 MiB whatever the piece size. Every other piece calculation already reads `u.chunkSize` from Begin.
- [ ] **Step 4: Run** the full test project. Expected: all pass.
- [ ] **Step 5: Commit** `Use each upload session's own piece size end to end`.

