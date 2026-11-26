# Final fix wave report: feat/large-uploads (base 2fb1338)

Status: DONE. Four commits, one per lettered group:

| SHA | Subject |
|---|---|
| 8696947 | Count every chunk PUT against a per-user request rate again |
| f0c8bc4 | Write the upload limit only through AdminService |
| 585a66c | Bound the piece buffer pool and spread retries from waiting uploads |
| 9175b7f | Show the real discard time, skip Files block IDs, and fix smaller review findings |

No Co-Authored-By trailer. Left unstaged as asked: `.claude/memory/MEMORY.md`, `.claude/memory/project-cost-analysis.md`, `.superpowers/`. Line endings were kept: `RateLimits.cs` and `AdminPageBaseTests.cs` are CRLF and stayed CRLF, and the new test file is LF like its neighbours. `git diff --stat` showed no whole-file rewrites.

## A. Per-user request rate on chunk PUTs

- `Endpoints/RateLimits.cs`: the `Chunks` policy is now `global concurrency → (per-user concurrency → per-user TokenBucket)`. Every chunk PUT costs one permit, under the new key `Transfers:ChunkRequestsPerMinute`. The order is the same as the `Transfers` policy, so a request refused for concurrency spends no token. Refusals go through the existing `OnRejected`: 429, `Retry-After: 5`, JSON `{error, code: "busy"}` under `/api/uploads`.
- **Default 1200** (`RateLimits.DefaultChunkRequestsPerMinute`), twice `ChunksPerMinute`. Honest work tops out at 600 accepted 4 MiB pieces a minute (the throughput bucket's limit) plus retries. At most 16 pieces are in flight per user, and each waiting piece asks again every ≥5 s (≥4 s with jitter), so that is under 200 a minute. A single waiting upload polls about 24 a minute. 1200 leaves room for all of that and still caps a flood of invalid requests at 20/s per user. Before this branch the cap was 600 on every request; 1200 avoids having waits eat into the throughput rate.
- The `RateLimits` doc comment is corrected: the concurrency caps bound requests in flight, and the request bucket bounds their rate.
- The byte-weighted bucket is untouched and is still charged only after session, length, authorisation and budget pass (R7).
- `appsettings.json` gained `"ChunkRequestsPerMinute": 1200`. The `docs/operations.md` Transfers table gained the new row, and the `ChunksPerMinute` row now says it is the 4 MiB-unit throughput limit.
- Test `UploadEndpointTests.PutChunks_InvalidRequests_CountTowardChunkRequestsPerMinute`: with `ChunkRequestsPerMinute=3`, three wrong-length PUTs on the user's own session each get 400 `invalid`, the fourth (a valid piece) gets 429 `busy` with `Retry-After: 5`, nothing is written, and another user is unaffected.

## B. Upload limit writable only through AdminService

- `UploadLimitService`: `SetOverrideAsync` and `ClearOverrideAsync` are removed, leaving the service read-only plus `Invalidate()`. Its doc comment says so.
- `AdminService` takes `IUploadLimitStore` as a new constructor parameter after `UploadLimitService`. It writes `_limitStore.SetAsync` / `ClearAsync` and then calls `_limits.Invalidate()`. Nothing else changed: the role check, the audit row and the cache invalidation before the read all work as before. DI needed no change because both are registered.
- New `CloudSelfService.Tests/UploadLimitWriteArchitectureTests.cs`, modelled on `StorageGateArchitectureTests`:
  - The allow-list is `AdminService`, `UploadLimitService`, `IUploadLimitStore`, the in-memory store, the Pg store, and `Program` (DI).
  - It flags fields, properties, constructor and method parameters of the store type. It also reads each method's IL for **constructor calls** of a store (`new InMemoryUploadLimitStore()`) and for **generic calls over the store type** (`GetRequiredService<IUploadLimitStore>()`, `AddSingleton<IUploadLimitStore, …>`). That catches a page resolving the store through `IServiceProvider`, not only by injection.
  - `Scanner_SeesTheKnownUsers` guards against a vacuous pass: AdminService and UploadLimitService must be found, and Program must be found through a call.
  - `UploadLimitService_HasNoWriteMethods` is a direct check that nothing named Set/Clear comes back.
- Updated constructions in `AdminServiceTests` (3) and `AdminPageBaseTests` (1).

## C. Large-piece behaviour under load

1. **Bounded pool.**
   - `PieceMemoryBudget` now owns `Buffers = ArrayPool<byte>.Create(ChunkMath.MaxBlobPieceSize, MaxArraysPerBucket)`, with `MaxArraysPerBucket = clamp(MaxBytes / 32 MiB, 1, 1024)`. That is 8 at the default 256 MiB budget.
   - `UploadSessionService.PutChunkAsync` rents and returns through `_budget.Buffers`, still with `clearArray: true`. `using System.Buffers` was dropped there.
   - The doc comment states the honest bound: the 32 MiB bucket keeps at most the budget, and each smaller bucket keeps half the one above, so total retained memory is under 2× the budget whatever the mix.
   - Tests in `PieceMemoryBudgetTests`:
     - `Buffers_KeepTheBudgetsWorthOfLargestPieces` (theory: 256 MiB→8, 64→2, 32→1, 1 byte→1; the pool is not `ArrayPool.Shared`).
     - `Buffers_LargestPieces_RetainAtMostTheBudget`: with a 32 MiB budget, two 32 MiB arrays are returned, and only one comes back on the next rents.
   - Judgement call: I first wrote a retention test with 1 KiB arrays and it failed. `ConfigurableArrayPool` gives the smallest (16-byte) bucket an extra slot, so that test was relying on framework internals. I replaced it with the 32 MiB case, which is what matters and does not depend on that detail.
2. **Client backoff.** In `wwwroot/js/uploader.js`:
   - `withRetry` keeps a `waits` counter. A `wait` sleeps `jittered(min(max(base, 30), base × 2^waits) × 1000)` with `base = max(1, Retry-After)`. So 5 → 10 → 20 → 30 → 30…, and a `Retry-After` above 30 is honoured rather than shortened.
   - `waits` lives in `withRetry`, which each `putChunk` call starts afresh, so it resets once a piece succeeds and the next piece begins at Retry-After.
   - A `wait` still resets `busyWaits`, never touches `attempt`, and `sleep` still rejects on an aborted signal.
   - `busy` sleeps are jittered too. `jittered(ms)` is ±20% (`JITTER = 0.2`). The transient backoff `[1000…16000]` is unchanged.
   - New test `UploaderScriptTests.Uploader_WaitBacksOffWithJitter_AndBusyIsJittered` checks this statically, as the existing tests do.
   - I also ran `withRetry` under a throwaway node harness (deleted afterwards). Seven waits slept 5153, 9400, 22197, 29714, 26772, 32971 and 35236 ms, all within ±20% of 5/10/20/30/30/30/30. A new piece started at 5022. 30 waits mixed with 10 busy answers did not fail. Eleven busy answers in a row did not fail. An aborted signal stopped the loop.
3. **Retry-After for a throughput `busy`.**
   - `ChunkThroughputLimiter.TryAcquire(ctx, length, out retryAfterSeconds)` computes, on refusal, `ceil(max(1, permits − available) × 60 / ChunksPerMinute)` clamped to 1..60. `available` comes from the partition's `GetStatistics`.
   - Judgement call: I checked the framework first, with a throwaway probe. `TokenBucketRateLimiter`'s `MetadataName.RetryAfter` is a whole replenishment period (always 1 minute here), and its auto-replenishment adds tokens in ~100 ms slices, so a minute bucket refills smoothly. The refill formula is therefore used whenever the level is known. The lease metadata is the fallback when it is not, and 5 is used when neither is available.
   - `UploadEndpoints.PutChunkAsync` captures the value and returns `Busy(message, "busy", seconds)` only for a throughput refusal. Every other `busy` (Begin caps, concurrency, request bucket, budget `wait`) keeps `Retry-After: 5`.
   - Tests:
     - `ThroughputRetryAfter_SecondsUntilTheBucketHoldsThePiece` (8 cases).
     - `ThroughputRetryAfter_UnknownLevel_UsesLeaseThenFallback`.
     - `ChunkThroughputLimiter_Refusal_ReportsRefillTime`: 6/min with 6 spent, an 8 MiB piece needs 2 tokens, so 20 s.
     - Two existing endpoint tests now assert the computed header: `PutChunk_EightMiBPiece_ChargesTwoTokens` → `60`, and `PutChunk_WaitingOnBudget_…` → `30`. The budget `wait` assertions still expect `5`.
   - No server-side blocking wait was added.

## D. Smaller fixes

1. `CLAUDE.md` and `.claude/rules/security.md` now say "bodies up to 32 MiB (the session's piece size)". The WAF wording is unchanged.
2. Spec §3 now says: Begin authorises first, then looks for a resumable session, and checks the limit and the per-user caps only for a new upload (ruling R2).
3. The `CHANGELOG.md` 1.1.0 line is now "Admins can now raise the upload limit beyond 195 GB; large files are sent in bigger pieces." 1.0.0 is untouched, and the line has no em-dashes.
4. **Discard time.**
   - I chose an already-computed `DiscardAt` on `UnfinishedUpload` (the last positional parameter) over exposing CreatedAt plus a flag, so the page stays free of sweep logic.
   - The rule is: no piece yet (`LastActivityAt` null), so `min(CreatedAt + IdleSessionMinutes, ExpiresAt)`; otherwise `ExpiresAt`.
   - The idle value is read by a new shared `UploadCleanupService.IdleFrom(config)`, which the sweep now uses too, so the two cannot drift.
   - `Pages/Index.razor` shows `u.DiscardAt`, and `ExpiresAt` is kept on the record.
   - Tests:
     - `ListUnfinished_NoPieceYet_DiscardAtIsCreatedPlusIdleMinutes`.
     - `ListUnfinished_NoPieceYet_DiscardAtMatchesTheSweep`: the store's sweep query excludes the session one minute before DiscardAt and includes it one minute after.
     - `ListUnfinished_ReportsReceivedBytes` now also asserts `DiscardAt == ExpiresAt` once a piece has arrived.
     - Prerender `BrowseRouteTests.Home_UnfinishedUploadWithNoPiece_ShowsIdleDiscardTime_NotExpiry`: one no-piece row shows created +2 h, and one row with a piece shows its extended expiry.
5. **Complete builds the block-ID list only for Blob**; Files passes an empty list.
   - Judgement call: the fake Files backend used to assemble the committed file from the block-ID list, so it would have committed an empty file. It now commits as the real Files backend does: the bytes written in place, up to the staging size. Where Azure would zero-fill a range lost to truncation, the fake fails instead, which keeps `Fake_ShrinkingStaging_DiscardsPiecesPastTheEnd` meaningful. That test briefly failed before this change.
   - Tests `Complete_FileShare_PassesNoBlockIds` (and the committed bytes equal the file) and `Complete_Blob_PassesEveryBlockIdInOrder`.
6. **Count-based received bytes.**
   - New `IUploadSessionStore.ReceivedSummaryAsync(id, chunkCount)` returns `(Received, LastReceived)` for indexes in `[0, chunkCount)`.
     - PostgreSQL: one query, `SELECT COUNT(*), COALESCE(BOOL_OR(idx = @count - 1), FALSE) FROM upload_chunks WHERE session_id = @id AND idx >= 0 AND idx < @count`.
     - In-memory: the same rule. `ThrowingSessionStore` updated.
   - `ListUnfinishedAsync` computes `count × chunkSize`, minus the short-piece shortfall when the last piece is in.
   - Contract test `ReceivedSummary_CountsInRangeAndFlagsTheLast` runs on the in-memory store and on Pg (Pg skips without `CSSP_TEST_PG`). It covers out-of-range indexes, a duplicate mark, another session's rows, a zero chunk count and an unknown session.
   - Service tests: the existing `ListUnfinished_ReportsReceivedBytes` passes unchanged. Added `ListUnfinished_LastShortPieceReceived_CountsItsRealLength` (it includes stray rows 3 and −1) and `ListUnfinished_EveryPieceReceived_ReceivedIsTheSize`.
7. **Select file disabled until interactive.**
   - `Shared/UploadPanel.razor` has `_ready`, set in `OnAfterRender(firstRender)`, the same pattern as Settings (sync, because there is no JS to call).
   - The hidden input gets `disabled="@(!_ready)"`. The label gets `aria-disabled="true"` until ready (the attribute is dropped once ready).
   - `wwwroot/css/app.css` gains `.btn[aria-disabled="true"] { opacity: 0.5; cursor: not-allowed; pointer-events: none; }`, matching `.btn:disabled`, which uses tokens only.
   - Prerender test `BrowseRouteTests.Browse_Prerender_RendersSelectFileDisabled_UntilInteractive`.
8. `docs/operations.md` gains a prerequisite bullet after "Large uploads". Before raising the limit above 195 GiB in production, upload a file that uses 8 MiB or larger pieces (a blob file over about 195 GiB) through the App Gateway, to confirm the WAF exemption passes those bodies.

## Build and tests

- `dotnet build` (app and test project): 0 warnings, 0 errors, at HEAD 9175b7f.
- `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj`, full project at HEAD: **Passed 1356, Failed 0, Skipped 25 (PostgreSQL and live storage), Total 1381.**
- Focused runs while working, using `--filter` per area. Group C's staged tree alone, with the other changes stashed, built clean and passed 307 focused tests before it was committed.

## Web suite

App started once on port 5310 (`Development`, `Auth__Provider=Dev`, `Storage__UseFake=true`, `dotnet run --no-launch-profile`). Ran `python -m pytest test_cloudselfservice.py -v` with `CSSP_BASE_URL=http://localhost:5310`: **11 passed in 9.13 s**. The app was stopped by pid straight after, and port 5310 was confirmed down. No Playwright browser was left running. Throwaway probe folders and logs in `D:\tmp` were deleted, along with `webapp-tests/__pycache__`.

## Files changed

- A: `Endpoints/RateLimits.cs`, `appsettings.json`, `docs/operations.md`, `CloudSelfService.Tests/UploadEndpointTests.cs`
- B: `Services/AdminService.cs`, `Services/Transfers/UploadLimitService.cs`, `CloudSelfService.Tests/AdminServiceTests.cs`, `CloudSelfService.Tests/AdminPageBaseTests.cs`, `CloudSelfService.Tests/UploadLimitWriteArchitectureTests.cs` (new)
- C: `Services/Transfers/PieceMemoryBudget.cs`, `Services/Transfers/UploadSessionService.cs`, `Endpoints/ChunkThroughputLimiter.cs`, `Endpoints/UploadEndpoints.cs`, `wwwroot/js/uploader.js`, `CloudSelfService.Tests/PieceMemoryBudgetTests.cs`, `CloudSelfService.Tests/UploaderScriptTests.cs`, `CloudSelfService.Tests/UploadEndpointTests.cs`
- D: `CLAUDE.md`, `.claude/rules/security.md`, `CHANGELOG.md`, `docs/operations.md`, `docs/superpowers/specs/2026-11-25-large-uploads-design.md`, `Pages/Index.razor`, `Shared/UploadPanel.razor`, `wwwroot/css/app.css`, `Services/Transfers/UploadSessionService.cs`, `Services/Transfers/UploadCleanupService.cs`, `Services/Data/UploadSessionStore.cs`, `Services/Storage/FakeStorageBackend.cs`, `CloudSelfService.Tests/UploadSessionServiceTests.cs`, `CloudSelfService.Tests/UploadSessionStoreContractTests.cs`, `CloudSelfService.Tests/BrowseRouteTests.cs`, `CloudSelfService.Tests/UploadItemTests.cs`, `CloudSelfService.Tests/Infrastructure/ThrowingSessionStore.cs`

## Concerns

- **Pg queries not run here.** `ReceivedSummaryAsync`'s SQL is covered by the Pg contract test, which skipped because `CSSP_TEST_PG` is not set on this machine. Run it against a throwaway database before release.
- **The request bucket shares the `OnRejected` path.** It answers `busy` with `Retry-After: 5`, as asked. A client rejected this way would retry under the busy allowance (10 waits, then counted attempts). At 1200 a minute that is reachable only by misbehaviour, not by an honest client.
- **The pool bound is per size bucket.** Total retention is bounded under 2× `MaxInFlightPieceBytes` across sizes, not exactly 1×. The doc comment says so. Arrays beyond what a bucket keeps are allocated and garbage-collected, as `ArrayPool.Shared` did for rents over its own size.
- **The fake Files commit changed.** It now mirrors the real backend: a rename with no block list, refusing a range lost to truncation. Tests that relied on the old block-list assembly for Files all still pass.
- The changelog 1.1.0 entry was not otherwise extended. The request-rate restore is a regression fix within the unreleased branch, and the backoff/pool changes are not user-visible, so no entry was added for them.
- Per the brief, the security-auditor and unit-test-reviewer were not run; the controller is to run them. This wave touches rate limits, upload endpoints, the admin write path and CSS.
