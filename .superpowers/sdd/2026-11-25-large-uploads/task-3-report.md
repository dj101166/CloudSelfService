# Task 3 report: byte budget for buffered pieces; throughput kept at today's rate

**Status:** DONE. Commit `5ac0719` "Bound buffered upload pieces by bytes and keep per-user throughput" on `feat/large-uploads` (base 76fce71). No Co-Authored-By trailer. Only task files were staged; the `.claude/memory/` changes and `.superpowers/` were left alone.

## What was implemented

- **`Services/Transfers/PieceMemoryBudget.cs`** (new singleton). Ctor `(IConfiguration)` reads `Transfers:MaxInFlightPieceBytes` (default 268,435,456) and `Transfers:MaxInFlightPieceBytesPerUser` (default 67,108,864), each clamped to at least 1. `bool TryReserve(Guid userId, long bytes, [NotNullWhen(true)] out IDisposable? lease)` does not block and takes one lock. It reserves against both budgets or neither. A budget that is otherwise empty admits any piece (`used == 0 || used + bytes <= max`). The lease releases once (Interlocked flag). A user's entry is removed when it reaches zero, so the dictionary only holds users with pieces in flight.
- **`UploadErrorCodes.Wait = "wait"`**, and `UploadSessionService.WaitMessage` holds the exact text from the brief.
- **`UploadSessionService`**:
  - The ctor is now `(access, sessions, backends, audit, time, config, budget, logger)`, as R1 requires.
  - `PutChunkAsync` calls `TryReserve(userId, expected, ...)` after the format checks, the session load, the length check and `AuthorizeAsync`, and before `ArrayPool.Rent`. On refusal it returns `AccessResult(Busy, WaitMessage, Wait)`, so no buffer is rented and no storage call is made.
  - The lease is released in the same `finally` that returns the buffer. It covers success, read failure or timeout, MD5 mismatch, grow timeout, storage failure, unexpected exceptions and cancellation. It is released before `MarkChunkAsync` (a DB call), so the budget is held only while the buffer exists.
  - Folded in: the named constant `InFlightTargetBytes` (16 MiB, with a WHY comment) replaces `16 * 1024 * 1024` in `ParallelFor`.
  - Folded in: the static helper `MinChunkBytesPerSecondFrom(IConfiguration)` is now the only place that parses `Transfers:MinChunkBytesPerSecond`. The service and `UploadEndpoints.ApplyMinDataRate` both call it. `UploadEndpoints.DefaultMinChunkBytesPerSecond` was removed; nothing else referenced it.
- **`UploadEndpoints`**:
  - `ErrorFor`: `Busy` now passes `code ?? UploadErrorCodes.Busy` through, still with `Retry-After: 5`.
  - `GuardAsync` is now `internal` so it can be tested.
  - For chunk PUTs, after the user-id check, `GuardAsync` charges `ChunkThroughputLimiter`. A refusal returns 429 `busy` with `Retry-After: 5` and the JSON body `{error: RejectedMessage, code: "busy"}`, the same body the rate-limiter middleware sends.
- **`RateLimits.cs`**:
  - The token bucket was removed from the `Chunks` policy. Both concurrency limiters stay (the global shared one, then per user).
  - New `public sealed class ChunkThroughputLimiter` (singleton). It is a `PartitionedRateLimiter.Create` keyed by `RateLimits.PartitionKey`, holding one `TokenBucketRateLimiter` per user (`ChunksPerMinute` tokens a minute, no queue). The built-in partitioned limiter disposes idle, full buckets, so idle partitions do not leak memory.
  - `PermitsFor(len) = max(1, ceil(len / 4 MiB))`. The charge is capped at the bucket's `TokenLimit`: otherwise a 32 MiB piece (8 permits) could never be admitted when `ChunksPerMinute < 8`, because `AttemptAcquire` with permits greater than `TokenLimit` throws or always fails. The cap has a WHY comment.
  - The doc comment was rewritten as the brief asks: memory is bounded by `PieceMemoryBudget`, and the chunk bucket bounds throughput in 4 MiB units.
- **`Program.cs`**: `AddSingleton<PieceMemoryBudget>()` and `AddSingleton<ChunkThroughputLimiter>()`.
- **`appsettings.json`**: both new keys under `Transfers`.
- **`uploader.js`**:
  - `toError` maps `'wait'` to the kind `'wait'`.
  - In `withRetry`, a `wait` sets the status to 'waiting', sleeps `Retry-After` through the signal-aware `sleep`, and reruns. It never touches `busyWaits` or `attempt`. An aborted signal rejects `sleep` with `UploadError('aborted')`, which ends the loop.

## Tests (TDD)

New or changed tests:
- `PieceMemoryBudgetTests` (new):
  - The five tests the brief names: `TryReserve_WithinBothBudgets_Succeeds`, `TryReserve_OverUserBudget_FailsAndReservesNothing`, `TryReserve_OverGlobalBudget_FailsForAnyUser`, `Lease_DisposedTwice_ReleasesOnce`, `TryReserve_PieceLargerThanBudget_AdmittedOnlyWhenEmpty`.
  - Extra: `Defaults_AreTheAgreedValues`, `TryReserve_PieceLargerThanGlobalBudget_AdmittedOnlyWhenEmpty`, and `TryReserve_Concurrent_NeverExceedsBudget` (16 threads; the peak never exceeds the budget and the budget is fully released at the end).
- `UploadErrorCodes_WireValues_Pinned` now also pins `"wait"`.
- `UploaderScriptTests.Uploader_HandlesWaitCode` checks for `'wait'` and the `e.kind === 'wait'` branch.
- `UploadEndpointTests`:
  - `PutChunk_BudgetExhausted_Returns429Wait`: 429, `code == "wait"`, the WaitMessage, `Retry-After: 5`, `BytesWritten == 0`, and the piece succeeds once the lease is released.
  - `PutChunk_EightMiBPiece_ChargesTwoTokens`: an 8 MiB Blob session row is created directly; with `ChunksPerMinute=2`, the first full PUT returns 204 and the second returns 429 `busy` with Retry-After.
  - `ChunkPermits_OnePerStartedFourMiB`: a Theory over null, 0, 1, 4 MiB, 4 MiB+1, 8 MiB and 32 MiB.
  - `ChunkThroughputLimiter_PieceCostingMoreThanTheBucket_StillAdmittedWhenFull`.
  - `Guard_SetsMaxRequestBodySizeBeforeAnyCheck`: a Theory that runs the real `GuardAsync` with a stub `IHttpMaxRequestBodySizeFeature`. PUT gets `ChunkMath.MaxBlobPieceSize`; POST, GET and DELETE get `ControlBodyLimit`. The handler is never reached because the request is cross-site.
- `UploadSessionServiceTests`:
  - `PutChunk_OverBudget_WaitBeforeAnyStorageCall`: no WriteChunk or GrowStaging call, nothing marked, and the piece succeeds after the lease is released.
  - `PutChunk_OtherUserHoldsBudget_OnlyAppWideBudgetApplies`.
  - `PutChunk_UnauthorisedOrMissingSession_TakesNoBudget`.
  - `PutChunk_AnyOutcome_ReleasesBudget`: a Theory over ok, bad-md5, read-fails, storage-fails, unexpected and cancelled.
  - The `Service(...)` helper and the `Begin_BlobLockMinutesFromConfig` ctor call now pass a `PieceMemoryBudget`.

**RED:**
- Compile RED first: `CS0246 PieceMemoryBudget could not be found` in PieceMemoryBudgetTests.cs and UploadSessionServiceTests.cs.
- Behavioural RED by mutating the finished implementation (every file restored afterwards):
  - M1, reserving 0 bytes instead of `expected`: 3 failures (BudgetExhausted_Returns429Wait, OverBudget_WaitBeforeAnyStorageCall, OtherUserHoldsBudget).
  - M2, lease never released: 7 failures (all six AnyOutcome_ReleasesBudget paths and OtherUserHoldsBudget).
  - M3, ErrorFor ignoring the code: BudgetExhausted_Returns429Wait fails.
  - M4, one permit per PUT: EightMiBPiece_ChargesTwoTokens and PieceCostingMoreThanTheBucket fail.
  - M5, guard setting 4 MiB for PUT: Guard_SetsMaxRequestBodySize(PUT) fails.
  - M6, JS without wait handling: Uploader_HandlesWaitCode fails.

**GREEN:** `dotnet build` gives 0 warnings and 0 errors (app and test project, `--no-incremental`). `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` gives Passed 1222, Failed 0, Skipped 16 (PostgreSQL tests without `CSSP_TEST_PG`).

The existing chunk-bucket tests still pass unchanged: `PutChunks_PerUserBucket_429BusyAfterChunksPerMinute`, `PutChunks_PerUserConcurrencyCap_429_AndRejectionDoesNotSpendToken`, `PutChunks_GlobalConcurrencyCap_AppliesAcrossUsers` and `PutChunks_DoNotSpendTransfersBucket`.

## Files changed

- New: `Services/Transfers/PieceMemoryBudget.cs`, `CloudSelfService.Tests/PieceMemoryBudgetTests.cs`
- Modified: `Services/Transfers/UploadSessionService.cs`, `Services/Transfers/UploadErrorCodes.cs`, `Endpoints/UploadEndpoints.cs`, `Endpoints/RateLimits.cs` (CRLF kept), `Program.cs`, `appsettings.json`, `wwwroot/js/uploader.js`, `CloudSelfService.Tests/{UploadEndpointTests,UploadErrorCodesTests,UploaderScriptTests,UploadSessionServiceTests}.cs`

Line endings were preserved: RateLimits.cs is CRLF, everything else LF, as before.

## Self-review findings

- **Fixed during review:** the lease was first scoped with `using var` to the end of the method, so it was also held across `MarkChunkAsync`. It is now released in the `finally` next to the buffer return, so it is held exactly while the buffer exists.
- The order matches the brief: the budget is checked after the session and authorisation checks and before the buffer is rented or storage is called. Unauthorised, missing or wrong-length requests take no budget (tested).
- A rejection from the concurrency limiters still spends no bucket token, because the rate-limiter middleware runs before the endpoint filter. Requests refused by the guard (cross-site, antiforgery, oversize, wrong content type) also no longer spend a token. That is slightly more lenient than before, and harmless.
- `StorageGateArchitectureTests` allow-list is unchanged: neither new type touches the storage backend.
- No change to the CSP, auth, routes or the Dockerfile.

## Concerns / notes for the controller

1. **Security review not run.** The project checklist requires the `security-auditor` agent for upload endpoint, rate-limit and `Program.cs` changes, and this task does not dispatch subagents. The controller should run it (or fold it into the final review). Points to check: the bucket is charged in the endpoint filter instead of the middleware, the cap at `TokenLimit`, and the budget's admit-when-empty rule.
2. **A budget `wait` 429 has already spent bucket tokens**, because the guard charges before the service checks the budget. A waiting 32 MiB piece retried every 5 s costs 8 tokens per try, which is about 96 tokens a minute per waiting piece against 600. That throttles a waiting user somewhat further but cannot lock them out, because a refill always comes. The alternative is to charge after the budget, which would need the limiter inside the service.
3. **ArrayPool retention** (carried from Task 2): `ArrayPool<byte>.Shared` can keep returned 32 MiB arrays on the LOH beyond the in-flight budget, in per-core and per-size buckets. The budget bounds in-flight bytes, not retained pool memory. If that matters in production, a dedicated `ArrayPool.Create(maxArrayLength, maxArraysPerBucket)` sized to the budget would cap it. Not changed here.
4. **Changelog:** no entry was written. The plan puts the 1.1.0 changelog in Task 8.
5. **Docs:** `docs/operations.md` (the new keys, and the figure of about four 390 GB uploads running at once) also belongs to Task 8.

---

# Fix round 1 (review Important #1, ruling R7)

**Commit:** `f35c2c1` "Charge upload throughput only once a piece holds its memory budget". No Co-Authored-By. Files were staged explicitly; `.claude/memory` was left alone.

## What changed

- **A piece refused for budget spends no throughput (R7).**
  - `GuardAsync` no longer charges the bucket. Its cheap pre-checks are unchanged.
  - `UploadSessionService.PutChunkAsync` takes a new optional last parameter, `Func<long, bool>? chargeThroughput`. The ctor is unchanged, so R1 still holds.
  - The callback runs right after `TryReserve` succeeds and before the buffer is rented. If it refuses, the lease is released and the method returns `Busy` / `busy`, which `ErrorFor` turns into 429 with `Retry-After: 5`, as before.
  - The `PutChunkAsync` endpoint injects `ChunkThroughputLimiter` and passes `bytes => throughput.TryAcquire(ctx, bytes)`.
  - So a `wait` costs nothing, and a `busy` happens only when the budget was available.
  - Side effect: requests refused before the budget check (missing session, wrong length, no access, malformed) no longer spend bucket tokens. The `Chunks` concurrency limiters still bound them.
- **`uploader.js`**: a `wait` response sets `busyWaits = 0`, so busy answers met during a long wait never add up to `interrupted`. An aborted signal still stops the loop, because `sleep` rejects with `aborted`.
- **`PieceMemoryBudget.Fits`** now starts with `bytes == 0 ||`, so a zero-byte reserve is always admitted.
- **`ChunkThroughputLimiter`** moved to its own file, `Endpoints/ChunkThroughputLimiter.cs` (LF, new). `RateLimits.cs` keeps CRLF, and its doc comment adds "A piece refused for memory is charged nothing."

## Covering tests (new)

- `UploadEndpointTests.PutChunk_WaitingOnBudget_SpendsNoTokens_AndStaysWaitWhileBucketDrained`, run with `ChunksPerMinute=2` and `MaxInFlightPieceBytes=1`:
  1. While another user holds the budget, five PUTs all get 429 `wait`.
  2. Once the budget is released, two PUTs succeed. Both tokens were still in the bucket, which proves the waits spent none; the bucket is now empty.
  3. With the budget held again, the PUT gets `wait`, not `busy`, even though the bucket is empty.
  4. With the budget free, the next PUT gets `busy` with `Retry-After: 5`.
  5. Exactly 2 pieces were written.
- `UploadSessionServiceTests.PutChunk_OverBudget_NeverChargesThroughput`: the callback is not called when the piece is over budget, and is called once with the piece length after the budget frees up.
- `UploadSessionServiceTests.PutChunk_ThroughputRefused_BusyReleasesBudgetAndTouchesNoStorage`.
- `PieceMemoryBudgetTests.TryReserve_ZeroBytes_AdmittedEvenOverBudget`.
- `UploaderScriptTests.Uploader_HandlesWaitCode` now also asserts that `busyWaits = 0;` is the first statement of the `wait` branch.

## RED evidence (reverting each fix)

- Charging restored in the guard and the callback removed: `PutChunk_WaitingOnBudget_SpendsNoTokens_AndStaysWaitWhileBucketDrained` FAILED (1 of 30 in the filtered run).
- `Fits` without `bytes == 0`: `TryReserve_ZeroBytes_AdmittedEvenOverBudget` FAILED. My first version of this test reserved exactly 16 of 16 MiB and passed even without the fix. It now uses an oversized 32 MiB admission, so the budget is over its limit, and the test fails without the fix.
- JS without `busyWaits = 0`: `Uploader_HandlesWaitCode` FAILED.

Each file was restored after its check, and the tests went green again.

## Commands and output

```
dotnet build --no-incremental                                   -> 0 Warning(s), 0 Error(s)
dotnet build CloudSelfService.Tests/CloudSelfService.Tests.csproj --no-incremental -> 0 Warning(s), 0 Error(s)
dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj --filter "FullyQualifiedName~PieceMemoryBudgetTests|FullyQualifiedName~UploadEndpointTests|FullyQualifiedName~UploadSessionServiceTests|FullyQualifiedName~UploaderScriptTests"
  Passed!  - Failed:     0, Passed:   267, Skipped:     0, Total:   267
dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj
  Passed!  - Failed:     0, Passed:  1226, Skipped:    16, Total:  1242
```

## Remaining concerns

- The security-auditor still needs to run on Task 3 (both commits). The throughput charge now happens in the service through an endpoint callback. Other callers of `PutChunkAsync` (none exist today) would skip the bucket unless they pass one.
- Concern 2 from the first report (a `wait` spends tokens) is resolved by this fix.
- ArrayPool retention and the Task 8 docs and changelog are unchanged from the first report.
