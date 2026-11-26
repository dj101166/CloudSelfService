### Task 3: Byte budget for buffered pieces; throughput kept at today's rate

**Files:**
- Create: `Services/Transfers/PieceMemoryBudget.cs`, `CloudSelfService.Tests/PieceMemoryBudgetTests.cs`
- Modify: `Services/Transfers/UploadSessionService.cs`, `Services/Transfers/UploadErrorCodes.cs`, `Endpoints/UploadEndpoints.cs`, `Endpoints/RateLimits.cs`, `Program.cs`, `appsettings.json`, `wwwroot/js/uploader.js`
- Test: `UploadEndpointTests.cs`, `UploadErrorCodesTests.cs`, `UploaderScriptTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class PieceMemoryBudget` (singleton): ctor `(IConfiguration config)` reads `Transfers:MaxInFlightPieceBytes` (default 268,435,456) and `Transfers:MaxInFlightPieceBytesPerUser` (default 67,108,864). `bool TryReserve(Guid userId, long bytes, out IDisposable? lease)` is non-blocking and thread-safe, and reserves against both totals or neither. Disposing the lease releases exactly once. A piece larger than a budget is admitted when that budget is otherwise empty, so a lowered setting can never starve a 32 MiB piece forever.
  - `UploadErrorCodes.Wait = "wait"` (429: the server is busy with other large uploads; wait and send this piece again). `UploadSessionService.WaitMessage = "The server is busy with other large uploads. This piece will be sent again shortly."`
  - `PutChunkAsync` calls `TryReserve(userId, expected, ...)` after the session and authorisation checks and **before** renting the buffer, and holds the lease until the piece is written or fails. On refusal it returns `new AccessResult(AccessOutcome.Busy, WaitMessage, UploadErrorCodes.Wait)`.
  - `UploadEndpoints.ErrorFor`: `Busy` passes `code ?? UploadErrorCodes.Busy` through, still with `Retry-After: 5`.
  - The `UploadSessionService` ctor gains `PieceMemoryBudget budget` before `ILogger`. Register it as a singleton in `Program.cs` and update the test helpers.
  - Throughput: the per-user chunk token bucket is charged `max(1, ceil(Content-Length / 4 MiB))` permits per PUT. The concurrency limiters (`MaxConcurrentChunksPerUser`, `MaxConcurrentChunksTotal`) stay. Implementation is the builder's choice (for example, a per-user `TokenBucketRateLimiter` keyed like `RateLimits.PartitionKey`, acquired in `GuardAsync` for PUTs with that permit count, replacing the bucket inside the `Chunks` policy). Refusal stays 429 `busy` with `Retry-After`.
  - `uploader.js`: a 429 with code `wait` sleeps `Retry-After` seconds and retries without incrementing `busyWaits` or `attempt`.

- [ ] **Step 1: Write failing tests:**
  - `PieceMemoryBudgetTests`: `TryReserve_WithinBothBudgets_Succeeds`; `TryReserve_OverUserBudget_FailsAndReservesNothing` (a failed reserve leaves the global total unchanged: a different user can still reserve the full remainder); `TryReserve_OverGlobalBudget_FailsForAnyUser`; `Lease_DisposedTwice_ReleasesOnce`; `TryReserve_PieceLargerThanBudget_AdmittedOnlyWhenEmpty` (per-user budget 16 MiB: reserve 32 MiB on empty → true; then 4 MiB → false).
  - `UploadErrorCodes_WireValues_Pinned` adds `Assert.Equal("wait", UploadErrorCodes.Wait)`.
  - `UploadEndpointTests.PutChunk_BudgetExhausted_Returns429Wait`: factory with `Transfers:MaxInFlightPieceBytes=1`; take a lease from the resolved singleton for another user id; PUT → 429, JSON `code == "wait"`, `Retry-After` header present, `BytesWritten == 0`.
  - `UploadEndpointTests.PutChunk_EightMiBPiece_ChargesTwoTokens`: `Transfers:ChunksPerMinute=2`; on an 8 MiB-piece session one full-piece PUT succeeds, then the next PUT is 429 `busy`.
  - `UploaderScriptTests.Uploader_HandlesWaitCode`: `Assert.Contains("'wait'", Script())`.
- [ ] **Step 2: Run.** Expected: failures.
- [ ] **Step 3: Implement.** Add both keys to `appsettings.json` under `Transfers`. Rewrite the `RateLimits.cs` doc comment: memory is bounded by `PieceMemoryBudget`, and the chunk token bucket bounds throughput in 4 MiB units.
- [ ] **Step 4: Run** the full test project. Expected: all pass.
- [ ] **Step 5: Commit** `Bound buffered upload pieces by bytes and keep per-user throughput`.

