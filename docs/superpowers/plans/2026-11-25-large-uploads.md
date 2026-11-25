# Large Uploads, Admin Upload Limit and Resumable Sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let uploads go past 195 GB (Blob up to 1,562.5 GiB, Files up to the limit), make the upload limit an audited Admin/Owner setting with a 100 GiB default, and keep an upload's server session alive across a sign-in lapse so the user can resume after signing in again.

**Architecture:** Begin picks a per-session piece size (4 MiB for Files; 4/8/16/32 MiB for Blob so the piece count stays ≤ 50,000) and stores it in the existing `upload_sessions.chunk_size`; every later step reads it from the session. Buffered piece bytes are bounded by a byte budget (`PieceMemoryBudget`). The effective limit comes from `UploadLimitService` (config default and ceiling + one stored override with optional expiry). Session expiry slides on each landed piece. The Files home and Browse page list the caller's unfinished uploads so they can be resumed by selecting the file.

**Tech Stack:** .NET 10 Blazor Web App (InteractiveServer), Npgsql, Azure.Storage.Files.Shares / Blobs, xUnit, vanilla JS (`uploader.js`), Python Playwright web tests.

**Spec:** `docs/superpowers/specs/2026-11-25-large-uploads-design.md`

## Global Constraints

- Work on branch `feat/large-uploads` in `D:\SC\Personal\CloudSelfService`. Commit steps run only once Dan has authorised commits for this execution. **Never add a `Co-Authored-By` trailer.** Preserve each file's existing line endings.
- Every storage read/write stays behind `StorageAccessService`; `StorageGateArchitectureTests` allow-list unchanged. No storage URL, key or SAS to the browser.
- Piece sizes: `MinPieceSize` = 4 MiB (4,194,304), `MaxBlobPieceSize` = 32 MiB (33,554,432), Files always 4 MiB, `MaxBlocksPerBlob` = 50,000. Blob size steps 4 → 8 → 16 → 32 MiB, smallest that keeps count ≤ 50,000.
- `firstPieceMd5` is always the MD5 of the file's first 4 MiB (or the whole file if smaller), independent of piece size.
- Limit defaults: `Transfers:MaxUploadBytes` **107,374,182,400** (100 GiB); `Transfers:MaxUploadBytesCeiling` **1,099,511,627,776** (1 TiB), clamped to **1,677,721,600,000** bytes (32 MiB × 50,000 = 1,562.5 GiB) with a startup warning.
- Memory budget: `Transfers:MaxInFlightPieceBytes` **268,435,456** (256 MiB) app-wide; `Transfers:MaxInFlightPieceBytesPerUser` **67,108,864** (64 MiB). Over budget → 429 with code `wait` and `Retry-After`.
- Per-user throughput unchanged: the chunk token bucket is charged one token per started 4 MiB of the piece (a 32 MiB piece costs 8).
- Parallel pieces returned by Begin: `clamp(16 MiB / pieceSize, 2, ParallelChunks)` (4 MiB → 4, larger → 2).
- Session lifetime: `expires_at = min(created_at + Transfers:MaxSessionDays, lastPieceAt + Transfers:SessionHours)`; defaults `SessionHours` 24, `MaxSessionDays` 7. Begin sets `expires_at = created_at + SessionHours` as today.
- Admin settings page `/admin/settings`, `[Authorize(Policy = Policies.StaffAdmin)]`, title `Settings — Advanced Cloud Self Service`. Audit operation `Admin.SetUploadLimit`.
- sumi CSS tokens only; `data-testid` selectors; comments only for a non-obvious WHY.
- Tests: `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` (name the project). Names `Method_Condition_Expected`. PostgreSQL integration tests run when `CSSP_TEST_PG` is set.
- Changelog: `CHANGELOG.md`, new top entry `## [1.1.0] - TBD`, glyph sections in order 🐛 🔄 ✨ 🗑️ 🛡️; no em-dashes, no customer names.
- Never run `pulumi`, `az` auth or podman auth commands.

## Review Focus

1. **A 4 MiB session created before the upgrade, resumed after it.** It must keep its stored 4 MiB piece size, block IDs and expiry; a mixed-size session would corrupt a blob. Pinned in Task 2 (`PutChunk_LegacySession_UsesStoredPieceSize`).
2. **The limit lowered while a large upload is open.** The open session must still accept pieces and complete; only a new Begin sees the new limit. Pinned in Task 4 (`Begin_ResumeAfterLimitLowered_StillResumes`).
3. **An override whose expiry passes while the app runs.** Begin after the expiry must use the default without any job or restart. Pinned in Task 4 (`Effective_OverrideExpired_FallsBackToDefault`).
4. **A file whose size is exactly a piece-size boundary** (50,000 × 4 MiB, 50,000 × 4 MiB + 1, 50,000 × 32 MiB, + 1). It must pick the right size or be refused, never produce 50,001 blocks. Pinned in Task 1 (`PieceSizeFor_Boundaries`).
5. **A sign-in that ends mid-upload, followed by a sign-in as a different user on the same browser.** Signing in is a full page navigation, so no in-page upload state survives it; the second user must not see or resume the first user's server sessions, and a Begin by the second user for the same file must start a new session of their own. Pinned in Task 6 (`ListUnfinished_OtherUsersSessions_NotReturned`) and Task 5 (`Begin_SameFileOtherUser_NewSession`, add it alongside `Begin_ResumeAfterSignInLapse_SameSession`).

---

## File Structure

```
Services/Transfers/
  ChunkMath.cs                 piece-size selection; count/length/offset take a piece size        (Task 1)
  PieceMemoryBudget.cs         NEW: app-wide + per-user byte budget for buffered pieces            (Task 3)
  UploadLimitService.cs        NEW: effective limit = override (unexpired) or default, ≤ ceiling   (Task 4)
  UploadSessionService.cs      uses session piece size, budget, limit service, sliding expiry,
                               ListUnfinishedAsync                                                  (Tasks 2-6)
  UploadErrorCodes.cs          + Wait = "wait"                                                      (Task 3)
Services/Data/
  UploadSessionStore.cs        MarkChunkAsync slides expiry; ListOpenForUserAsync                   (Tasks 5, 6)
  SettingsStore.cs             NEW: IUploadLimitStore (Pg + InMemory), table upload_limit_override  (Task 4)
  PgsqlContext.cs              CREATE TABLE upload_limit_override                                   (Task 4)
Services/AdminService.cs       SetUploadLimitAsync / ClearUploadLimitAsync, audited                 (Task 4)
Endpoints/
  UploadEndpoints.cs           chunk body cap = MaxBlobPieceSize; budget wait result                (Tasks 2, 3)
  RateLimits.cs                chunk tokens charged per 4 MiB                                       (Task 3)
Pages/Admin/Settings.razor     NEW admin page (+ .razor.css)                                        (Task 4)
Shared/NavMenu.razor           Settings link in Admin group                                         (Task 4)
Pages/Index.razor              Unfinished uploads card                                              (Task 6)
Pages/Files/Browse.razor       passes folder unfinished sessions to UploadPanel                     (Task 7)
Shared/UploadPanel.razor       "Waiting for the file" rows, limit text, sign-in-ending warning      (Task 7)
wwwroot/js/uploader.js         session piece size, wait code, pause on expiry, 4 MiB fingerprint    (Task 7)
App.razor                      exposes sign-in end time to the page                                 (Task 7)
Pages/Help.razor, docs/operations.md, CHANGELOG.md, appsettings.json                                (Task 8)
webapp-tests/test_cloudselfservice.py                                                               (Task 8)
```

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

### Task 4: Effective upload limit, the override store, and the Settings admin page

**Files:**
- Create: `Services/Transfers/UploadLimitService.cs`, `Services/Data/SettingsStore.cs`, `Pages/Admin/Settings.razor`, `Pages/Admin/Settings.razor.css`, `CloudSelfService.Tests/UploadLimitServiceTests.cs`
- Modify: `Services/Data/PgsqlContext.cs`, `Services/AdminService.cs`, `Services/Transfers/UploadSessionService.cs`, `Program.cs`, `Shared/NavMenu.razor`, `appsettings.json`
- Test: `AdminServiceTests.cs`, `UploadSessionServiceTests.cs`, `StartupChecksTests.cs`, `PgSchemaTests.cs`, `PageAuthorizationAttributeTests.cs` (if it lists pages explicitly), `InMemoryStoresTests.cs`, `PgStoresIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record UploadLimitOverride(long Bytes, DateTimeOffset? ExpiresAt, Guid SetBy, string SetByUpn, DateTimeOffset SetAt, string Note)`.
  - `public interface IUploadLimitStore { Task<UploadLimitOverride?> GetAsync(CancellationToken ct = default); Task SetAsync(UploadLimitOverride o, CancellationToken ct = default); Task ClearAsync(CancellationToken ct = default); }`, with `PgUploadLimitStore` and `InMemoryUploadLimitStore`, registered in both branches of `Program.cs`.
  - Table, added to `PgsqlContext` schema: `CREATE TABLE IF NOT EXISTS upload_limit_override (id SMALLINT PRIMARY KEY CHECK (id = 1), bytes BIGINT NOT NULL CHECK (bytes > 0), expires_at TIMESTAMPTZ NULL, set_by UUID NOT NULL, set_by_upn TEXT NOT NULL, set_at TIMESTAMPTZ NOT NULL, note TEXT NOT NULL DEFAULT '')`. `SetAsync` upserts row 1; `ClearAsync` deletes it.
  - `public sealed record UploadLimitState(long Effective, long Default, long Ceiling, UploadLimitOverride? Override, bool OverrideActive)`.
  - `public sealed class UploadLimitService` (singleton): ctor `(IUploadLimitStore store, TimeProvider time, IConfiguration config)`.
    - `public static (long Ceiling, long ConfiguredCeiling, long Default, long ConfiguredDefault) LimitsFrom(IConfiguration config)` replaces `UploadSessionService.MaxUploadBytesFrom`. Ceiling = `min(Transfers:MaxUploadBytesCeiling ?? 1,099,511,627,776, ChunkMath.MaxBlobBytes)`. Default = `min(Transfers:MaxUploadBytes ?? 107,374,182,400, Ceiling)`.
    - `Task<UploadLimitState> GetStateAsync(CancellationToken ct = default)`: the override counts when it exists and `ExpiresAt is null || ExpiresAt > now`. Effective = `min(override.Bytes, Ceiling)` if active, else Default. The store read is cached for 30 s (`TimeProvider`-based); `Invalidate()` drops the cache and is called by `AdminService` after a set or clear.
    - `public const long DefaultMaxUploadBytes = 107_374_182_400; public const long DefaultCeilingBytes = 1_099_511_627_776;`
  - `UploadSessionService`: ctor takes `UploadLimitService limits` instead of reading `_maxBytes` from config. `BeginAsync` reads `(await _limits.GetStateAsync(ct)).Effective` once and uses it for the size check (before authorisation, as today), the per-user reserved-bytes cap, and `TooLargeMessage`. **Change from today:** resolve the resumable session (`FindResumableAsync` plus the existing first-piece match) before the size check, and skip the size and reserved-bytes checks when one exists, because its size was accepted at its own Begin. The input validation (negative size, MD5 format, names) and the authorisation call still run first.
  - `AdminService`: `public const string SetUploadLimitOp = "Admin.SetUploadLimit";`, added to `AuditOperationNames`. Ctor gains `UploadLimitService limits` (update `Program.cs` and the test helpers).
    - `Task<AccessResult> SetUploadLimitAsync(Guid actorId, long bytes, DateTimeOffset? expiresAt, string? note, AuditContext? ctx = null, CancellationToken ct = default)`: `RequireAsync(actorId, StaffRole.StaffAdmin, SetUploadLimitOp, ...)`. Validation: `bytes` in `[1 GiB, Ceiling]` → else `Invalid` with `"The limit must be between 1 GB and {ceiling} GB."`; `expiresAt` null or `> now + 1 minute` → else `Invalid` with `"The end time must be in the future."`; note ≤ 500 chars after trim. Audit path text: `"limit {old GB} GB -> {new GB} GB; until {ISO-8601 UTC or 'permanent'}; note: {note}"`, `Bytes` = new bytes.
    - `Task<AccessResult> ClearUploadLimitAsync(Guid actorId, AuditContext? ctx = null, CancellationToken ct = default)`: same gate; audit path `"limit {old} GB -> default {default} GB"`.
    - GB figures use the same formatting as `UploadSessionService.TooLargeMessage` (binary GB, up to 2 decimals). Extract that into `public static string UploadLimitService.FormatGb(long bytes)` and use it in both places.
  - `Program.cs` startup warnings: one for a clamped ceiling (`"Transfers:MaxUploadBytesCeiling {Configured} exceeds the {Limit}-byte Azure block-blob maximum; using the maximum."`), one for a clamped default (the existing text, now citing the ceiling).
  - `Pages/Admin/Settings.razor`: `@page "/admin/settings"`, `@attribute [Authorize(Policy = Policies.StaffAdmin)]`, `@inherits AdminPageBase`, `<PageTitle>Settings — Advanced Cloud Self Service</PageTitle>`, inside `AdminGate` and `AdminAlertBox` like `Targets.razor`.
    - Card "Upload size limit" showing the current limit `[data-testid=upload-limit-effective]`, its source `[data-testid=upload-limit-source]` ("Default from configuration" or "Set by {upn} on {local time}, until {local time}" / ", permanent"), the default and the ceiling.
    - Form: number input in GB `[data-testid=upload-limit-gb]` (min 1, max ceiling GB, step 1); radio Permanent / Until `[data-testid=upload-limit-permanent]`, `[data-testid=upload-limit-until]`; `datetime-local` input `[data-testid=upload-limit-until-at]` enabled only for Until, labelled with the browser's zone name; note `[data-testid=upload-limit-note]`; Save `[data-testid=upload-limit-save]`; **Revert to default** `[data-testid=upload-limit-revert]`, shown only when an override exists.
    - The browser zone comes from the existing `cssp.timeZone` interop (`BrowserTimeZone`, `FileDisplay.ResolveZone`) as in `Browse.razor`. The entered local time converts to UTC with that zone; when the zone can't be resolved, the label says UTC and the value is taken as UTC.
    - Every action re-checks with `StillAuthorizedAsync()` and catches store failures into `Alert`, like the other admin pages.
  - `NavMenu.razor`: Settings link after Storage targets, `href="admin/settings"`, `data-testid="nav-admin-settings"`, icon `monitor`, label "Settings".
  - `appsettings.json`: `MaxUploadBytes: 107374182400`, add `MaxUploadBytesCeiling: 1099511627776`.

- [ ] **Step 1: Write failing tests:**
  - `UploadLimitServiceTests`: `Effective_NoOverride_IsDefault` (default 100 GiB); `Effective_ActiveOverride_IsOverride`; `Effective_OverrideExpired_FallsBackToDefault` (move `MutableTime` past `ExpiresAt` and call `Invalidate()`, or advance past the 30 s cache, → Default); `Effective_OverrideAboveCeiling_ClampedToCeiling`; `LimitsFrom_CeilingAboveBlobMax_Clamped` (configured 2 TiB → `ChunkMath.MaxBlobBytes`); `LimitsFrom_DefaultAboveCeiling_Clamped`; `GetState_CachesForThirtySeconds` (a store that counts reads: two calls within 30 s → 1 read; after 31 s → 2).
  - `AdminServiceTests`: `SetUploadLimit_StaffAdmin_SavesAndAudits` (audit operation `Admin.SetUploadLimit`, `Bytes` = new value, path contains `"-> 150 GB"`); `SetUploadLimit_Staff_Denied` (role Staff → `Denied`, denied audit row, store unchanged); `SetUploadLimit_AboveCeiling_Invalid`; `SetUploadLimit_PastExpiry_Invalid`; `ClearUploadLimit_RevertsToDefault_Audited`; `SetUploadLimit_Owner_FromConfig_Allowed` (a stored role of None with the UPN in `OwnerConfig` → Ok).
  - `UploadSessionServiceTests`: `Begin_AboveEffectiveLimit_TooLarge` (override 2 GiB, size 2 GiB + 1 → `TooLargeMessage(2 GiB)`); `Begin_ResumeAfterLimitLowered_StillResumes` (Begin 3 GiB under a 4 GiB override, set the override to 2 GiB, Begin the same file again → Ok, same session id).
  - `StartupChecksTests`: replace the `MaxUploadBytesFrom` cases with `LimitsFrom` cases (null config → 100 GiB default and 1 TiB ceiling) and keep the source-text assertion pointing at `UploadLimitService.LimitsFrom(`.
  - `PgSchemaTests`: the schema text contains `upload_limit_override`.
  - Store contract (in-memory always, Pg under `CSSP_TEST_PG`): `UploadLimitStore_SetGetClear_RoundTrips`, `UploadLimitStore_SetTwice_KeepsOneRow`.
  - `PageAuthorizationAttributeTests` (if it enumerates pages): `/admin/settings` carries the StaffAdmin policy.
- [ ] **Step 2: Run.** Expected: failures.
- [ ] **Step 3: Implement** the service, stores, schema, `AdminService` methods, DI, startup warnings, page, nav link and `appsettings.json`. Remove `UploadSessionService.MaxUploadBytesFrom` and `DefaultMaxUploadBytes`.
- [ ] **Step 4: Run** `dotnet build` (0 errors) and the full test project. Expected: all pass.
- [ ] **Step 5: Commit** `Add an audited, expiring upload limit that Admins and Owners can change`.

### Task 5: Upload sessions slide their expiry on each landed piece

**Files:**
- Modify: `Services/Data/UploadSessionStore.cs` (interface, in-memory and Pg), `Services/Transfers/UploadSessionService.cs`, `appsettings.json`
- Test: `UploadSessionStoreContractTests.cs`, `UploadSessionServiceTests.cs`

**Interfaces:**
- Produces:
  - `IUploadSessionStore.MarkChunkAsync(Guid id, int index, DateTimeOffset at, DateTimeOffset expiresAt, CancellationToken ct = default)`: the same statement also sets `expires_at = GREATEST(expires_at, @expires)` for a row whose `status = 'Open'`. The expiry never moves backwards.
  - `UploadSessionService`: `public const int DefaultMaxSessionDays = 7;`, reading `Transfers:MaxSessionDays` (min 1). In `PutChunkAsync`, after the piece is written: `expiresAt = Min(session.CreatedAt + maxSessionDays, now + _lifetime)`.
  - `appsettings.json`: add `"MaxSessionDays": 7`.
  - The 7-day cap stays inside Blob's one-week retention of uncommitted blocks: the sweep aborts the session at `created_at + 7 days`, at most just before Azure drops the blocks. Document this in the `DefaultMaxSessionDays` xmldoc (it's a non-obvious WHY).

- [ ] **Step 1: Write failing tests:**
  - Contract: `MarkChunk_LaterExpiry_Extends`; `MarkChunk_EarlierExpiry_DoesNotShorten`; `MarkChunk_NonOpenSession_ExpiryUnchanged` (Completing).
  - Service: `PutChunk_Day3_ExtendsExpiryToLastPiecePlus24h` (create at T, PUT at T+3d → `ExpiresAt == T+3d+24h`); `PutChunk_Day6point5_CapsAtSevenDays` (PUT at T+6.5d → `ExpiresAt == T+7d`); `Sweep_SessionWithRecentPiece_NotAbortedAfter24h` (create at T, piece at T+20h, sweep at T+30h → still Open); `Begin_ResumeAfterSignInLapse_SameSession` (Begin, PUT piece 0, advance 13 h, Begin the same file again → Ok, same session id, `Received == [0]`).
- [ ] **Step 2: Run.** Expected: failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full test project; with `CSSP_TEST_PG` set, also the Pg contract tests. Expected: all pass.
- [ ] **Step 5: Commit** `Keep an upload alive while its pieces keep arriving, up to seven days`.

### Task 6: List a user's unfinished uploads; Files home card

**Files:**
- Modify: `Services/Data/UploadSessionStore.cs`, `Services/Transfers/UploadSessionService.cs`, `Pages/Index.razor`, `Pages/Index.razor.css`
- Test: `UploadSessionStoreContractTests.cs`, `UploadSessionServiceTests.cs`

**Interfaces:**
- Produces:
  - `IUploadSessionStore.ListOpenForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)`: this user's `Open`, unexpired sessions, newest activity first (`COALESCE(last_activity_at, created_at) DESC`).
  - `public sealed record UnfinishedUpload(Guid SessionId, Guid TargetId, string TargetName, string Folder, string FileName, long Size, int ChunkSize, long ReceivedBytes, DateTimeOffset LastActivity, DateTimeOffset ExpiresAt, bool Overwrite)` in `UploadSessionService.cs`.
  - `UploadSessionService.ListUnfinishedAsync(Guid userId, Guid? targetId = null, string? folder = null, CancellationToken ct = default) → Task<IReadOnlyList<UnfinishedUpload>>`. For each session, `await _access.GetLevelAsync(userId, session.TargetId, ct)` must allow `OperationOf(session)`, else the session is left out. `TargetName` comes from `_access.ListReachableTargetsAsync(userId)` (one call per list, not per row). `ReceivedBytes` = sum of `ExpectedLength` over received indices. Filtered to `targetId`/`folder` when given (folder compared to `StoragePath.Parent(FinalPath)`). No storage call.
  - `Pages/Index.razor`: when the list is non-empty, an **Unfinished uploads** card above the targets (`data-testid="unfinished-uploads"`). Each row (`data-testid="unfinished-upload"`, `data-session-id`) shows the file name (`StoragePath.DisplayName`), the target name and folder, `"{percent}% received"`, last activity in local time, and `"Discarded after {local time}"`. It has an **Open folder** link (`FileDisplay.BrowseHref(TargetId, Folder)`, `data-testid="unfinished-open"`) and **Discard** (`data-testid="unfinished-discard"`), which calls `UploadSessionService.AbortAsync` with the page's audit context after a `ConfirmDialog` ("Discard the unfinished upload of {name}? What was sent so far is deleted.") and reloads the list. A failure shows the page's existing unavailable message and doesn't end the circuit.

- [ ] **Step 1: Write failing tests:**
  - Contract: `ListOpenForUser_ReturnsOnlyOwnOpenUnexpired` (own Open, own expired, own Completed, other user's Open → only the first).
  - Service: `ListUnfinished_OtherUsersSessions_NotReturned`; `ListUnfinished_GrantRevoked_Omitted` (remove the grant → empty); `ListUnfinished_DowngradedToRead_Omitted`; `ListUnfinished_ReportsReceivedBytes` (8 MiB-piece session with pieces 0 and 2 of 3 received, size 20 MiB → `ReceivedBytes == 8 MiB + 4 MiB`); `ListUnfinished_FolderFilter_OnlyThatFolder`; `ListUnfinished_MakesNoStorageCalls` (fake backend call counter unchanged).
- [ ] **Step 2: Run.** Expected: failures.
- [ ] **Step 3: Implement** the store method (in-memory and Pg), the service method and the Files home card (sumi tokens; reuse `card`, `table-wrap` and `btn` classes).
- [ ] **Step 4: Run** `dotnet build` and the full test project. Expected: all pass.
- [ ] **Step 5: Commit** `Show a user's unfinished uploads on the Files home`.

### Task 7: Resume after sign-in in the file browser; pause instead of fail on expiry

**Files:**
- Modify: `wwwroot/js/uploader.js`, `Shared/UploadPanel.razor`, `Shared/UploadPanel.razor.css`, `Services/Files/UploadItem.cs`, `Pages/Files/Browse.razor`, `App.razor`
- Test: `UploaderScriptTests.cs`, `UploadItemTests.cs`, new `AppShellContractTests.cs` (static source-text checks, in the style of `ComponentStyleContractTests.cs`)

**Interfaces:**
- Consumes: Task 6 `UploadSessionService.ListUnfinishedAsync(userId, targetId, folder)` and `UnfinishedUpload`; Task 4 `UploadLimitService.GetStateAsync`.
- Produces:
  - **Expiry pauses.** In `uploader.js`, `expireAll()` sets each active upload to a new status `'signed-out'` with message `SIGNED_OUT = 'You were signed out. Sign in again, then select the same file to carry on where it stopped.'`, calls `notify(u, 'OnProgress', …)`, and does **not** call `finish(u, false, …)`. It aborts in-flight requests and sends no DELETE. `UploadStatusNames.SignedOut = "signed-out"` in `UploadItem.cs`, with `CanResume == false` and `IsFinished == false`. The row shows the message and a **Sign in again** link to `/login`.
  - **Panel rows for server-side unfinished uploads.** `UploadPanel` gets `[Parameter] public IReadOnlyList<UnfinishedUpload> Unfinished { get; set; }`. Each one not already matched by a live item (same name and size) renders as status `waiting-file` (`UploadStatusNames.WaitingForFile = "waiting-file"`, `data-status="waiting-file"`), with the text "Waiting for the file: {percent}% received" and a **Select file** button (`data-testid="upload-select-file"`). The button is a `<label for=…>` bound to a per-row hidden `<input type=file>` whose `accept` is unset, so a user click opens the picker without script. On change, the page calls `StartAsync` as for the normal input, with the row's stored `Overwrite` passed to `csspUploader.start` so the exists prompt isn't repeated. Begin resumes by the existing name, size and fingerprint match. If the picked file differs, Begin starts a new upload as today, and the panel says "That is a different file; it was started as a new upload." when the returned session id differs from the row's.
  - **Browse page:** after loading the folder, calls `ListUnfinishedAsync(user.Id, TargetId, _folder)` and passes the result to `UploadPanel.Unfinished`. A failure logs and passes an empty list.
  - **Limit text:** the panel heading area shows `"Files up to {UploadLimitService.FormatGb(Effective)} GB"` (`data-testid="upload-limit-text"`). The page reads `UploadLimitService.GetStateAsync` once on load.
  - **Sign-in ending warning:** `App.razor` writes the sign-in end time as `data-session-ends` (Unix seconds = `AppClaims.IssuedAt` claim + `Auth:AbsoluteSessionHours`) on the `<body>` element for an authenticated user. `uploader.js` reads it once on load. While any upload is active and the end is within 15 minutes, it calls `notify(u, 'OnSessionEnding', endsAtIso)` once per upload. `UploadPanel` shows a banner (`data-testid="upload-session-ending"`): "Your sign-in ends at {local time}. Uploads still running then will pause; sign in again and select the same files to carry on."
  - Help text and UploadPanel comment updates are in Task 8.

- [ ] **Step 1: Write failing tests:**
  - `UploaderScriptTests`: `Uploader_ExpiryPausesInsteadOfFailing` (script contains `'signed-out'`, and `expireAll`'s body has no `finish(`); `Uploader_FingerprintIsFirstFourMiB` (contains `FINGERPRINT_BYTES = 4194304` and no `CHUNK_SIZE`); `Uploader_ReadsSessionEnd` (contains `data-session-ends` or `dataset.sessionEnds`).
  - `UploadItemTests`: `SignedOut_IsNotFinishedAndNotResumable`; `WaitingForFile_IsNotActive`.
  - `AppShellContractTests.App_SessionEndsAttribute_OnlyForAuthenticatedUser`: `App.razor` contains `data-session-ends`, and its value comes from the issued-at claim (source contains `AppClaims.IssuedAt`) inside a branch guarded by `IsAuthenticated`.
- [ ] **Step 2: Run.** Expected: failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet build` and the full test project. Then run the app with `Auth__Provider=Dev`, `Storage__UseFake=true` and check by hand in a browser: start an upload as `customer-full`, sign out mid-way, sign in again, open the Files home → the unfinished card shows it; open the folder → the "Waiting for the file" row shows; select the same file → it resumes from the received percentage.
- [ ] **Step 5: Commit** `Resume unfinished uploads after signing in again`.

### Task 8: Docs, changelog, help text and web tests

**Files:**
- Modify: `Pages/Help.razor`, `Shared/UploadPanel.razor` (header comment), `docs/operations.md`, `CHANGELOG.md`, `webapp-tests/test_cloudselfservice.py`, `CLAUDE.md` (Configuration table row for `Transfers:*` and Architecture: `Pages/Admin/Settings`)

- [ ] **Step 1: Help** (`data-testid="help-uploads"` list):
  - Replace the 24-hour bullet with: "An unfinished upload is kept for 24 hours after its last piece arrived, and at most 7 days. If you are signed out part-way, sign in again, open the same folder and select the same file to carry on."
  - Add: "The largest file you can upload is shown above the upload list. For a database backup larger than that, ask Advanced support, or split the backup into several files."
- [ ] **Step 2: `docs/operations.md`** Transfers table:
  - `MaxUploadBytes` default 107374182400 (100 GiB), "the default limit; Admins can override it in Admin → Settings".
  - New `MaxUploadBytesCeiling` 1099511627776 (1 TiB), "the most an Admin can set; capped at 1,562.5 GiB, the Azure block-blob maximum at 32 MiB pieces".
  - `SessionHours` 24, "an unfinished upload expires this long after its last piece".
  - New `MaxSessionDays` 7, "and never later than this after it started".
  - New `MaxInFlightPieceBytes` / `MaxInFlightPieceBytesPerUser`.
  - `ParallelChunks` wording: "pieces in flight per upload for 4 MiB pieces; larger pieces use 2".
  - Add a short "Large uploads" operator note: piece sizes by file size; about four uploads over 390 GB at once per instance before others wait; the WAF exemption must allow 32 MiB bodies on `/api/uploads/*`; and the striping fallback (SQL Server `BACKUP ... TO DISK = 'a.bak', DISK = 'b.bak'`) for anything over the ceiling.
- [ ] **Step 3: `CHANGELOG.md`**: new top entry `## [1.1.0] - TBD`:
  - 🔄 Changed: "The default upload limit is now 100 GB."; "An unfinished upload is now kept for 24 hours after its last piece arrives (up to 7 days), instead of 24 hours after it started."; "Being signed out during an upload now pauses it instead of failing it."
  - ✨ Added: "Files larger than 195 GB can now be uploaded: large files are sent in bigger pieces, up to about 1.5 TB for blob containers."; "Admins and Owners can change the upload limit in Admin, Settings, permanently or until a set time. Every change is recorded in the audit log."; "The Files home lists your unfinished uploads, and the file browser shows each one as waiting for its file so you can carry on after signing in again."; "The upload panel shows the largest file you can upload, and warns before your sign-in ends while uploads are running."
  - No em-dashes, no customer names.
- [ ] **Step 4: Web tests** (`webapp-tests/test_cloudselfservice.py`, `data-testid` only):
  - `test_staff_admin_sets_and_reverts_upload_limit`: persona `staff-admin`; open `/admin/settings`; set 150 GB, Permanent, Save → `upload-limit-effective` contains "150"; Revert → contains "100".
  - `test_staff_cannot_open_settings`: persona `staff` → Access Denied content.
  - `test_upload_panel_shows_limit`: persona `customer-full`, open a share → `upload-limit-text` contains "100".
- [ ] **Step 5: CLAUDE.md** updates: the Architecture `Pages/` list gains `Admin/Settings`; the Configuration table row for `Transfers:*` mentions the limit ceiling, session days and piece-memory budget.
- [ ] **Step 6: Verify:** `dotnet build` (0 errors), the full test project (0 failures), then the web tests against the app in Development (`Auth__Provider=Dev`, `Storage__UseFake=true`). Expected: all pass.
- [ ] **Step 7: Commit** `Document large uploads, the upload limit setting and resuming after sign-in`.

### After all tasks

- Dispatch `security-auditor` (the new admin route, the upload endpoint body cap and budget, the `AdminService` gate, `App.razor` session-end attribute) and `unit-test-reviewer` (ChunkMath, `PieceMemoryBudget`, `UploadLimitService`, sliding expiry, `ListUnfinishedAsync`). Fix what they confirm.
- Before release: in the test environment, upload a file over 195 GB (or at least one above 50,000 × 4 MiB to a Blob target) through the App Gateway to confirm 8 MiB+ piece bodies pass the WAF exemption.

