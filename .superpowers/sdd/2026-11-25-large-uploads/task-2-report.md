# Task 2 report: Sessions carry their own piece size

Status: DONE. Commit `76fce71` "Use each upload session's own piece size end to end" on `feat/large-uploads` (no trailer).

## What was implemented

- `UploadSessionService`
  - Begin computes `ChunkMath.PieceSizeFor(target.Kind, req.Size)` only on the new-session path, after the size limit and per-user caps have accepted the size (R3, no try/catch). It stores the result in `UploadSession.ChunkSize` and returns it in the response.
  - A resumed session returns its **stored** `ChunkSize`. `ChunkCount` and `ParallelChunks` come from that stored size.
  - `ParallelChunks`: `_parallel` for 4 MiB pieces. Otherwise `min(_parallel, max(2, 16 MiB / pieceSize))`. This is the brief's `Math.Clamp(16 MiB / pieceSize, 2, _parallel)`, but written so it does not throw when `Transfers:ParallelChunks` = 1: `Math.Clamp` throws when min > max. A test covers this.
  - `CheckChunkFormat` caps `Content-Length` at `ChunkMath.MaxBlobPieceSize`.
  - `PutChunkAsync` uses `ChunkCount`/`ExpectedLength(session.Size, session.ChunkSize, ...)` and writes at `ChunkMath.Offset(index, session.ChunkSize)`. `CompleteAsync` counts with `session.ChunkSize`.
  - Piece read timeout: new `internal PieceReadTimeoutFor(long length)` = `max(PieceReadTimeout, length / minRate)`. `minRate` = `Transfers:MinChunkBytesPerSecond`, default 65,536; the new constant is `UploadSessionService.DefaultMinChunkBytesPerSecond`, which `UploadEndpoints.DefaultMinChunkBytesPerSecond` now aliases. `PieceReadTimeout` stays the settable floor.
  - `MaxUploadBytesFrom` clamps at `ChunkMath.MaxBlobBytes` (R5), so a configured limit above 195 GiB now takes effect.
  - Stale buffer comment fixed: it now says the session's piece size, at most `MaxBlobPieceSize`.
  - The `BeginUploadRequest.FirstPieceMd5` doc said "MD5 of piece 0". It now says "first 4 MiB (`ChunkMath.FingerprintBytes`) whatever the piece size". The same fix went into the `UploadSession` record doc in `Services/Data/UploadSessionStore.cs`; that file had a comment-only change.
- `ChunkMath`: added `ThrowIfNegativeOrZero(pieceSize)` to `ChunkCount`, `ExpectedLength` and `Offset`.
- `UploadEndpoints.GuardAsync`: the per-request `MaxRequestBodySize` and the 413 check use `ChunkMath.MaxBlobPieceSize`. `TooLargeMessage` = "An upload piece is larger than the 32 MiB maximum piece size."
- `Program.cs`: the startup warning now reads "...exceeds the {Limit}-byte ceiling (Azure's 50,000 blocks per blob at the 32 MiB largest piece size); using the ceiling."
- `uploader.js`: `CHUNK_SIZE` was renamed to `FINGERPRINT_BYTES = 4194304`, and only `firstPieceMd5` uses it. Its comment says the fingerprint is always the first 4 MiB whatever piece size Begin returns. All other piece math already used `u.chunkSize`, and no other 4 MiB constant remains in the script.

## Tests

New tests:
- `UploadSessionServiceTests`:
  - `Begin_LargeBlob_ReturnsLargerPieceSize`: 8 MiB, 25,001 pieces, parallel 2, stored 8 MiB.
  - `Begin_LargeFile_StaysFourMiB`: 4 MiB, 50,001 pieces, parallel 6.
  - `Begin_LargePieceWithSingleParallelConfigured_DoesNotRaiseParallelism`
  - `Begin_ResumesLegacySession_ReturnsStoredPieceSize`: a stored 4 MiB row at the 195 GiB+1 size resumes with 4 MiB and 50,001 pieces.
  - `PutChunk_LargePieceSession_AcceptsFullPieceAndWritesAtOffset`: a recording backend shows block id 1 written at offset 8 MiB with the same bytes; a 4 MiB piece gets "Piece 1 must be exactly 8388608 bytes."
  - `PutChunk_LegacySession_UsesStoredPieceSize`: 50,000 chunks are marked in the store, only the last 1-byte piece is PUT (it lands at offset 50,000 × 4 MiB), and Complete commits 50,001 IDs through a backend that does not store blocks.
  - `PieceReadTimeout_ScalesWithPieceLengthAboveTheFloor`: 4 MiB → 120 s floor, 32 MiB → 512 s, and the configured rate is honoured.
- `UploadEndpointTests.PutChunk_OverMaxPiece_Returns413`: 32 MiB + 1 gets 413 `too_large` with the new message.
- `ChunkMathTests.PieceArithmetic_NonPositivePieceSize_Throws`: 0 and -1.

Updated tests:
- `StartupChecksTests`: the clamp is now at `MaxBlobBytes`, and two new within-ceiling cases (195 GiB+1 and exactly `MaxBlobBytes`) are passed through.
- `Begin_ConfiguredMaxAboveBlobBlockLimit_ClampedWithoutPerScopeWarning`: now on Blob at `MaxBlobBytes` / +1. It also asserts the 32 MiB piece size and 50,000 pieces at the ceiling.
- `PutChunk_BodyLargerThanChunk_413` was renamed `PutChunk_BodyLargerThanSessionPiece_400`. A 4 MiB + 1 body on a 4 MiB session now passes the guard, and the service rejects it as invalid (400).
- `ErrorCodes_Chunk`: the 413 case now uses 32 MiB + 1.
- The `PutChunk_MalformedRequest_InvalidWithoutTouchingTheStore` row moved from 4 MiB + 1 to 32 MiB + 1.

### TDD evidence

RED: `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj --filter "<new and changed tests>"`
- First run: compile failure, CS1061 `PieceReadTimeoutFor` does not exist.
- With a floor-only stub: `Failed!  - Failed: 14, Passed: 6, Total: 20`. The failures were every Begin large/legacy test, LargePiece PutChunk, OverMaxPiece, BodyLargerThanSessionPiece, the timeout scaling test, the ChunkMath guards, and the clamp tests.
- `PutChunk_LegacySession_UsesStoredPieceSize` passed before the change, as expected. It is a regression guard: the old code was 4 MiB everywhere.

GREEN, same filter: `Passed 19, Failed 1`. The failure was the ParallelChunks=1 case, where `Math.Clamp` threw. After fixing that, the full run was:
`dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` → `Passed!  - Failed: 0, Passed: 1190, Skipped: 16, Total: 1206`. The 16 skipped need PostgreSQL or live storage. `dotnet build`: 0 warnings, 0 errors.

## Files changed

- D:\SC\Personal\CloudSelfService\Services\Transfers\UploadSessionService.cs
- D:\SC\Personal\CloudSelfService\Services\Transfers\ChunkMath.cs
- D:\SC\Personal\CloudSelfService\Services\Data\UploadSessionStore.cs (doc comment only)
- D:\SC\Personal\CloudSelfService\Endpoints\UploadEndpoints.cs
- D:\SC\Personal\CloudSelfService\Program.cs (warning text)
- D:\SC\Personal\CloudSelfService\wwwroot\js\uploader.js
- D:\SC\Personal\CloudSelfService\CloudSelfService.Tests\{UploadSessionServiceTests,UploadEndpointTests,StartupChecksTests,ChunkMathTests}.cs

All files kept LF line endings. CRLF count is 0 both before and after.

## Backend and body-limit findings

- **BlobBackend**: no 4 MiB assumption.
  - `WriteChunkAsync` stages one block from a seekable stream of any length; Azure allows blocks up to 4000 MiB.
  - `CompleteUploadAsync` commits whatever ID list it gets. The count now comes from `session.ChunkSize`.
  - `HasLandedAsync` checks only that committed block 0 is `BlockId(sessionId, 0)`, which does not depend on piece size.
  - `BlockId`'s six-digit index still covers 50,000.
- **FileShareBackend**: `WriteChunkAsync` sends one Put Range (`UploadRangeAsync(new HttpRange(offset, length))`), and Azure caps Put Range at 4 MiB. Files sessions always get 4 MiB: `PieceSizeFor(FileShare, …)` and legacy rows both store 4 MiB. Nothing else in Files depends on piece size: grow goes to the full size, and complete compares lengths. The backend has no guard of its own against a range over 4 MiB. Today only a hand-edited DB row could hit that; adding a guard would be defence in depth, and I did not add one (out of scope).
- **FakeStorageBackend**: no piece-size assumption. Blocks are keyed by ID with their offset. Files enforces `offset + length <= staging size`, the same as Azure's 416.
- **Kestrel**: `Program.cs` has no `ConfigureKestrel` or global `Limits.MaxRequestBodySize`, so the global default applies: 30,000,000 bytes (about 28.6 MiB), which is **below 32 MiB**. `GuardAsync` raises it for each chunk request through `IHttpMaxRequestBodySizeFeature` (to `MaxBlobPieceSize`) before anything reads the body, and that per-request value overrides the global default. So nothing else caps chunk PUTs lower. The automated tests exercise only the Content-Length 413 path, not a real 32 MiB body through Kestrel, because TestServer does not apply Kestrel limits.
- **App Gateway WAF**: outside the repo. `docs/operations.md` says the route needs a body-inspection exemption for "4 MiB bodies". Dan should confirm the exemption has no size cap of its own (for example a raised max-request-body or file-upload limit near 4 MiB) and lets 32 MiB bodies through. The docs wording is Task 8.
- **Kestrel MinDataRate** (64 KiB/s with a 5 s grace) does not depend on size, so it works with 32 MiB pieces. The service's read timeout now scales to match: up to 512 s for 32 MiB.

## Self-review

- Every read after Begin (PutChunk, Complete, resumed Begin) uses `session.ChunkSize`, so existing 4 MiB rows behave as before (Review Focus #1). Status, Abort and cleanup do no piece arithmetic.
- `PieceSizeFor` is called only after the `_maxBytes` check (≤ `MaxBlobBytes`) and only for a new session (R3).
- `StorageGateArchitectureTests` allow-list is unchanged. The test-only `RecordingBackend` lives in the test assembly, which that test does not scan.
- `ArrayPool<byte>.Shared` pools arrays up to 1 GiB on .NET 6+, so 32 MiB buffers are pooled; they sit on the large object heap.

## Concerns

1. **Memory bound is looser until Task 3.** The existing concurrency caps (16 per user, 64 app-wide) multiplied by 32 MiB pieces allow up to 512 MiB per user and 2 GiB app-wide in buffered pieces, against 64 MiB and 256 MiB today. Task 3's byte budget closes this. Do not deploy between Task 2 and Task 3. `RateLimits.cs`'s doc comment still describes 4 MiB pieces; Task 3 rewrites it.
2. **WAF exemption size** (see above) needs checking outside the repo before deployment.
3. **`ParallelChunks` formula** differs slightly from the brief (min/max rather than `Math.Clamp`) to avoid an exception at `ParallelChunks=1`. It gives the same values whenever `_parallel >= 2`.
