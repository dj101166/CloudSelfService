# Task 9 report: chunked, resumable upload API

**Status:** DONE_WITH_CONCERNS (the concerns are minor; see the last section)
**Commit:** `bf4e6fa` "Add chunked, resumable, MD5-verified upload API with expiry cleanup" (BASE 2e62d0a)
**Suite:** 642 passed, 7 skipped, 0 failed. `dotnet build` gives 0 warnings and 0 errors. The PG integration run passed 6 of 6.

## What was built

### Store: `Services/Data/UploadSessionStore.cs`
- **Contents.** It holds the `UploadStatus` constants, the `UploadSession` record, `IUploadSessionStore`, `InMemoryUploadSessionStore` and `PgUploadSessionStore`.
- **Interface additions beyond the brief.** None of these changes the brief's signatures.
  - `FindResumableAsync(user, target, finalPath, size, now)` does the resume lookup.
  - `TrySetStatusAsync(id, expected, next)` is a conditional update, done in PG as `UPDATE … WHERE status = @expected`.
  - `SetOverwriteAsync` is used when a resumed session is re-begun with overwrite.
  - `PurgeExpiredBeforeAsync(cutoff)` deletes rows 7 days after expiry. Chunk rows go with them through the existing `ON DELETE CASCADE`.
- **Lifecycle.** `Open → Completing → Completed`, or `Open → Aborted`. Every transition out of Open is conditional. A failed commit goes `Completing → Open`, so the user can retry.
- **Chunks.** `MarkChunkAsync` uses `ON CONFLICT DO NOTHING`, which makes it idempotent and safe when chunk PUTs run in parallel.

### Service: `Services/Transfers/UploadSessionService.cs`

**Begin**
- **Validation order:**
  1. Size must be ≥ 0. The limit is `Transfers:MaxUploadBytes`, with the message "File is larger than the {N} GB limit." ({N} is GiB, formatted `0.##`, so 50 GB and 1 GB).
  2. The folder goes through `TryNormalizePath` (lenient).
  3. The file name goes through `TryValidateName` (strict).
  4. Hidden or reserved names (`.folder`, staging pattern) are rejected.
  5. The combined path is re-normalised.
  6. `AuthorizeAsync(Upload)`.
  7. For Files, the staging leaf must fit the 255-character limit.
  8. A missing destination folder containing invisible characters gives Invalid, because storage would create it implicitly.
  9. The existence check: a folder there is always AlreadyExists; a file there is AlreadyExists unless Overwrite is set, in which case it calls `AuthorizeAsync(Overwrite)`.
  10. The resume lookup.
  11. Create the session row, then call `BeginUploadAsync`. If that fails, the session is marked Aborted and the result is Unavailable.
- **Staging path (R19).** Files use `{folder}/.{name}.cssupload-{id:N}`. For Blob, the staging path is the final path.
- **Response.** `ParallelChunks` comes from config (default 4, clamped 1–16). The session lifetime is `Transfers:SessionHours` (24).

**PutChunk**
1. Loads the caller's own session, which must be Open and unexpired. Anything else, including another user's session id, is NotFound, so the two are indistinguishable.
2. Checks the index range, then that `contentLength == ChunkMath.ExpectedLength`, then that the MD5 is 24 characters of base64 decoding to 16 bytes. A failure in any of these is Invalid.
3. Re-runs `AuthorizeAsync(Upload|Overwrite)` on every chunk (Review Focus 2).
4. Calls `WriteChunkAsync` at `index * ChunkSize` with `BlockId(id, index)`, then `MarkChunkAsync`.
- A storage MD5 mismatch maps to Unavailable (503), so the client retries. A non-storage exception is also Unavailable, never a 500.

**Complete**
1. Counts the chunks received in range. If any are missing the result is Invalid: "Upload is missing N pieces." (it says "piece" when N is 1).
2. Re-authorises.
3. Claims `Open → Completing`. If the claim fails the result is NotFound, which covers a double Complete, a sweep or an Abort racing it.
4. Calls `CompleteUploadAsync` with block ids in index order. This uses `CancellationToken.None`, so a client disconnect can't half-commit.
5. On success it sets Completed and writes one audit row: `Upload`, Success, `Bytes = Size`. On failure it reverts to Open and audits Failed.
- A zero-byte file has `ChunkCount` 0 and commits immediately (an empty block list for blobs).

**Abort**
1. Re-authorises.
2. Claims `Open → Aborted`.
3. Calls `AbortUploadAsync`. If that fails, the session goes back to Open and the result is Unavailable.
4. Audits `Upload` as Failed, with the error "Cancelled by user".

**Status**
- Returns the received list for the caller's own open session, after a `GetLevelAsync` check. That check writes no audit row.

**Store failures**
- Store failures propagate from the service. The endpoints map them to 503 "The service is temporarily unavailable." (R24).

### Background service: `Services/Transfers/UploadCleanupService.cs`
- Uses `PeriodicTimer(30 min, TimeProvider)`. `internal SweepAsync(now, ct)` is exposed for tests.
- **The sweep:**
  1. For each session from `ListExpiredOpenAsync`, it claims `Open → Aborted` conditionally. A session that is Completing or already claimed is skipped.
  2. It then calls `AbortUploadAsync`, logging and ignoring any error.
  3. Finally it purges rows that expired more than 7 days ago.

### Endpoints: `Endpoints/UploadEndpoints.cs`
- **Shared guard.** All routes live in the `/api/uploads` group with `RequireAuthorization` and one endpoint filter. The filter runs these steps in order:
  1. Sets `no-store`.
  2. On a PUT, sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = ChunkSize`. This applies to the chunk route only, and happens before antiforgery reads anything.
  3. Rejects `Sec-Fetch-Site` values `cross-site` and `same-site` with 403 and no audit row.
  4. Calls `IAntiforgery.ValidateRequestAsync` and returns 400 on failure. This deliberately uses `ValidateRequestAsync` and not `IsRequestValidAsync`, because the latter skips GET and the status route is a GET.
  5. Checks the user id claim.
- **Rate limits.** Every route uses `RateLimits.Transfers` except the chunk PUT, which uses the new `RateLimits.Chunks` (R26).
- **Responses.** Everything is JSON. Errors come back as `{ "error": "…" }`. Outcomes map as Denied → 403, NotFound → 404, Invalid → 400, AlreadyExists → 409, and anything else → 503.
- **Chunk size.** A chunk whose `Content-Length` is above ChunkSize gets 413 before the body is read. A `BadHttpRequestException` 413 from Kestrel also maps to 413.
- **Begin body.** Begin needs a JSON content type and uses Web JSON options. Malformed JSON gives 400.
- **New routes.** `GET /api/antiforgery` is in `AuthEndpoints.cs`. It requires authorization, sets `no-store`, and returns `{ token, headerName }`.
- **`RateLimits.Chunks`.** A per-user concurrency limiter set by `Transfers:MaxConcurrentChunksPerUser` (default 16), with no token bucket.
- **`Program.cs`.** Registers the store (PG or in-memory), the scoped `UploadSessionService`, the `UploadCleanupService` hosted service and `MapUploadEndpoints()`.

### Items carried from the Task 8 review
- `StoragePath.TryValidateSegment(segment, out error, bool strict)` now requires `strict`. Its callers were updated: `TryNormalizePath` passes `strict: false`, `TryValidateName` passes `strict: true`, and one test was updated.
- **New `StoragePath.NonStrictFolderPrefix(folder, existingFolder = "")`.** It returns the shortest prefix that contains every segment failing the strict check. Leading segments shared with an existing folder are skipped. It returns null when every segment passes.
- **`MoveAsync`.** If the destination's parent has a non-strict segment outside the part it shares with the source's parent, that prefix must already exist as a folder; otherwise the move is Invalid with `InvisibleCharactersMessage`. Tests: `Move_IntoNewFolderWithInvisibleCharacter_Invalid`, `Move_OutOfFolderWithInvisibleCharacter_KeepingSharedPrefix_Ok`. The existing `Move_ToFolderWithExistingInvisibleCharacterName_KeepingLeaf_Ok` still passes.
- **`CreateFolderAsync` (beyond the brief, the same hole).** The backends create missing parents, so the same existing-folder check is applied to the parent. Tests: `CreateFolder_UnderMissingParentWithInvisibleCharacter_Invalid` and `..._UnderExistingParent..._Ok`.
- **`AuthorizeAsync`.** It now sets `UserUpn` on Ok, and still never on Denied, which matches `AuthorizeDownloadAsync`. The upload audit rows use it.

### Fake backend: `FakeStorageBackend`
- **Blob behaviour now mirrors the real backend.** `BeginUpload` is a no-op for visibility, and Abort never deletes `Files[stagingPath]`. Before this change, aborting a blob overwrite session would have deleted the user's existing blob in the fake, and a blob upload would have shown an empty placeholder at the final name.
- **Commit ordering.** Commit removes the staging entry before writing the final file, so staging == final works.
- **New test hook.** `FailOnCall` injects a fault on any named call.

## Tests

### TDD
- **RED.** I moved the four new implementation files and the `Program.cs` change aside and built the test project. It failed with CS0246 for `UploadSession`, `UploadSessionService`, `IUploadSessionStore`, `UploadCleanupService`, `InMemoryUploadSessionStore` and `BeginUploadResponse`.
- **GREEN.** With the files restored, 632 passed and 7 skipped. That rose to 642 after I added failure-path, create-folder and blob-abort tests.

### `UploadSessionServiceTests` (all named tests, plus extras)

All the named tests are present:
- `Begin_ReadUser_Denied`
- `Begin_TooLarge_Invalid`
- `Begin_ExistingFileNoOverwrite_AlreadyExists`
- `Begin_ExistingFileOverwrite_RequiresContribute_Ok`
- `Begin_SameFileAgain_ReturnsSameSessionWithReceived`
- `PutChunk_WrongLength_Invalid`
- `PutChunk_MissingMd5_Invalid` (theory: null, empty, non-base64, short, long)
- `PutChunk_BadMd5_UnavailableAndChunkNotMarked`
- `PutChunk_OtherUsersSession_NotFound`
- `PutChunk_GrantRevokedAfterBegin_Denied`: asserts no `WriteChunk` call, `BytesWritten` 0, nothing marked, and one Denied audit row
- `PutChunk_SameIndexTwice_Idempotent`
- `Complete_MissingChunk_Invalid`
- `Complete_AllChunks_FileExistsWithExactBytes`: Files and Blob, 9 MiB + 1 byte, chunks sent in the order 2, 0, 1, SHA-256 compared
- `Complete_ZeroByteFile_CreatesEmptyFile`: Files and Blob
- `Complete_AuditsSingleUploadRow`
- `Abort_RemovesStaging_AuditsFailed`
- `Sweep_ExpiredOpenSession_Aborted`

Extra tests:
- **Begin:** default 50 GB message; negative size; folder name with overwrite; staging layout and parallel chunks; blob staging equals final path; bad or reserved names (theory of 6); a name too long for Files staging; new and existing invisible-character folders; other user or size gives a new session; expiry gives a new session; resume with overwrite; backend unreachable; `BeginUpload` failure aborts the session; unexpected exception; store failure propagates.
- **PutChunk:** index out of range; user deactivated; expired session; offset and block id.
- **Status:** own session versus another user's.
- **Complete:** twice; grant revoked; file appeared meanwhile (AlreadyExists, session reopened, Failed audit); backend failure then retry; unexpected exception; another user.
- **Abort:** another user; backend failure; blob overwrite abort leaves the existing blob untouched.
- **Sweep:** a Completing session is not touched; a backend failure still marks it Aborted; rows are purged at expiry + 7 days; an expired blob overwrite session leaves the existing blob untouched.

### `UploadEndpointTests` (all named tests, plus extras)

All the named tests are present:
- `Upload_EndToEnd_CustomerContribute_Succeeds`: the name contains `#` and a space, chunks are sent out of order, it checks the status list, downloads, compares SHA-256 and checks one audit row
- `PutChunk_WithoutAntiforgery_400`: missing and wrong token
- `PutChunk_BodyLargerThanChunk_413`
- `Begin_Anonymous_NotAuthorised`: also covers anonymous `/api/antiforgery`

Extra tests: zero-byte end to end; all routes without antiforgery give 400; cross-site and same-site on all 5 routes give 403 with no audit; read persona gets 403 with a JSON error; existing file gives 409; malformed JSON gives 400; another user's session gives 404 on status, chunk and abort; abort gives 204 with the audit row; bad MD5 gives 503; store failure gives 503 with the generic message; and chunk PUTs don't spend the Transfers bucket (`RequestsPerMinute` = 2: begin, 4 chunks and complete all succeed, and the next begin gets 429).

### Other tests
- `StorageAccessServiceTests`: 5 new tests (move, create-folder, `NonStrictFolderPrefix`).
- `PgStoresIntegrationTests.UploadSessionStore_RoundTripResumeStatusAndPurge`. It covers:
  - the round trip with a path containing `#` and a space and a 9 GiB size
  - resume matching on user, size and expiry
  - idempotent and concurrent (20 parallel) `MarkChunk`
  - `SetOverwrite`
  - conditional status and exclusion from `ListExpiredOpen`
  - purge cascading to chunks
  - delete

### PG integration run (R11)
- I ran `podman run … postgres:17-alpine` with port `55432`. `localhost` was refused, as in Task 4. Connecting through the WSL VM IP `172.21.108.241` passed 6 of 6, covering the 5 existing tests plus the new one.
- `podman stop cssp-pg-test` ran afterwards. I ran no other podman commands apart from a read-only `podman inspect` for the IP.

## Files
- **Created:**
  - `Services/Data/UploadSessionStore.cs`
  - `Services/Transfers/UploadSessionService.cs`
  - `Services/Transfers/UploadCleanupService.cs`
  - `Endpoints/UploadEndpoints.cs`
  - `CloudSelfService.Tests/UploadSessionServiceTests.cs`
  - `CloudSelfService.Tests/UploadEndpointTests.cs`
- **Modified:**
  - `Program.cs`
  - `Endpoints/AuthEndpoints.cs`
  - `Endpoints/RateLimits.cs`
  - `Services/Access/StorageAccessService.cs`
  - `Services/Access/StoragePath.cs`
  - `Services/Storage/FakeStorageBackend.cs`
  - `CHANGELOG.md` (one Added line under `[1.0.0] - TBD`)
  - `CloudSelfService.Tests/{DownloadHardeningTests,PgStoresIntegrationTests,StorageAccessServiceTests}.cs`
- Line endings were preserved: `RateLimits.cs` is CRLF and the rest are LF. There is no Co-Authored-By trailer.

## Self-review
- **Isolation.** A session is only reachable by its owner. Another user's id is indistinguishable from a missing one (404). The resume lookup is keyed on user id.
- **Re-authorisation.** Each chunk, Complete and Abort calls `AuthorizeAsync`, so a revoked grant, a deactivated user or customer, or an inactive target stops the next call before any storage write. This is asserted through the fake's call log.
- **Audit (R3).** Begin and chunks write no audit row, except the Denied row that comes from the gate. Complete writes exactly one Success or Failed row, and Abort writes one Failed "Cancelled by user" row.
- **Races.** The sweep, Complete and Abort all claim the session through conditional status updates, so they never collide. A double Complete gives 404 and still only one audit row.
- **Body limits.** `MaxRequestBodySize` is set on the PUT only. `Content-Length` is checked up front. A missing `Content-Length` (chunked transfer encoding) gives Invalid (400) because it cannot equal the expected length.
- **Paths.** The final name is checked strictly. The folder is checked leniently, but a new folder with invisible characters is refused. Hidden or staging names are reserved. The Files staging leaf is length-checked. Overall path length is enforced.

## Concerns / notes for review
1. **A session stuck in Completing after a crash.** If the process dies between claiming Completing and finishing, the session stays Completing. The sweep skips it by design, and its row is purged 7 days after expiry. For Files, the staging file is then orphaned (hidden from listings, but it uses quota). A follow-up could let the sweep reset Completing sessions older than N minutes back to Open, and then abort them.
2. **Concurrent Begin.** Two Begins for the same file at the same moment can create two sessions, and the resume lookup returns the newest. This is harmless, because the extra one expires and is swept.
3. **Blob abort is a no-op.** This was already true of the backend. Uncommitted blocks on the final blob name are dropped by Azure after about 7 days, and the existing committed blob is untouched, which tests now pin.
4. **Antiforgery cookie.** `/api/antiforgery` uses `GetAndStoreTokens`, so it sets the antiforgery cookie. The token is bound to the signed-in user. `UseAntiforgery()` middleware does not validate minimal API JSON or raw-body endpoints, so validation is explicit in the group filter.
5. **The GB message uses GiB.** "{N} GB" is computed in GiB (`53,687,091,200` gives "50 GB"), which matches the constant's 50 GiB.
6. **Fake semantics changed.** The FakeStorageBackend's blob semantics now differ by `StorageKind`. All existing fake tests use FileShare targets and still pass.
7. **Not run.** Per R5, I did not run `security-auditor` or `unit-test-reviewer`. The brief's Step 5 is left to the controller.

---

## Fix round 1 (commit `0976cc9`)

**Suite:** 662 passed, 7 skipped, 0 failed. Build is 0 warnings and 0 errors. The PG integration run passed 6 of 6 against podman postgres:17 at 172.21.108.241:55432, and the container was stopped afterwards.

### CRITICAL 1: chunks now reach storage as a seekable stream (R28)
- **(a) The chunk is buffered and checked before storage.** `PutChunkAsync` runs every existing check, then reads exactly `expected` bytes into an `ArrayPool<byte>` buffer, returned in `finally`. Storage receives `new MemoryStream(buffer, 0, len, writable:false)`.
  - A body shorter than expected is Invalid with "Upload piece was shorter than expected."
  - Any byte past `expected` is Invalid with "Upload piece was longer than expected."
  - The MD5 is computed locally over the buffer. A mismatch returns Invalid (400) with "Upload piece was corrupted in transit; it will be retried." and storage is not called.
  - The MD5 is still passed to storage for end-to-end verification. A storage-side Md5Mismatch stays retryable 503.
- **(b) Body read errors.**
  - An `IOException` while reading the body returns Invalid ("Upload piece did not arrive completely; it will be retried.") and is logged at Information. This covers `BadHttpRequestException`, which is an IOException, and a client disconnect.
  - `OperationCanceledException` on an aborted request is still `Results.Empty`.
  - I **removed** the endpoint's 413 `BadHttpRequestException` catch because it is now dead code. A declared `Content-Length` above 4 MiB is still a 413 before anything is read. A body with no declared length cannot pass the exact-length check. Kestrel's `MaxRequestBodySize = ChunkSize`, set in the group filter, stays as a backstop, and the service maps it to 400.
- **(c) The fake now enforces the SDK contract.** `FakeStorageBackend.WriteChunkAsync` throws `ArgumentException("stream must be seekable")` when `!CanSeek`, and also throws when `Length - Position != length`. Tests:
  - `PutChunk_FullSizePiece_ReachesStorageAsSeekableStream`: endpoint level, a full 4 MiB piece through the TestServer request body
  - `PutChunk_NonSeekableBody_BufferedAndWritten`: service level, using a Kestrel-like stream with no Length and 3-byte reads
  - `WriteChunk_NonSeekableStream_Throws` and `WriteChunk_StreamLengthMismatch_Throws`
  - **Regression check:** I temporarily passed the raw `body` to storage again, and 13 tests failed, including the end-to-end and seekable-stream tests. I then restored the fix.
  - The old fake test `WriteChunk_ShortStream_Conflict` was replaced, because a short stream is now a contract violation before the read.
  - Other new tests: `PutChunk_BadMd5_InvalidAndChunkNotMarked`, `PutChunk_StorageMd5Mismatch_UnavailableAndChunkNotMarked`, `PutChunk_BodyShorterThanDeclared_Invalid`, `PutChunk_BodyLongerThanDeclared_Invalid`, `PutChunk_BodyReadFails_InvalidNotUnavailable`, and `PutChunk_BadMd5_400_NoStorageCall` at the endpoint.
- **(d) Memory bound.** The `RateLimits` xmldoc now says the worst case is ChunkSize (4 MiB) × MaxConcurrentChunksPerUser (16) = 64 MiB per user. `MaxConcurrentChunksPerUser` was added to `appsettings.json`.

### IMPORTANT 2: lost staged data, and concurrent blob sessions
- **New outcome.** `AccessOutcome.Conflict` maps to 409. The download endpoint's `default` branch still covers it.
- **Error mapping.** `StorageErrorMapper` maps 400 `InvalidBlockList` to Conflict with "This upload was interrupted by another upload of the same file. Please start it again."
- **Complete when the staged data is gone.** If the commit fails with NotFound or Conflict, the service:
  1. moves the session from Completing to Aborted
  2. runs `AbortUploadAsync` best-effort
  3. writes the Failed audit row
  4. returns Conflict (409).
  - For NotFound the message is "This upload's data is no longer in storage. Please start it again."
  - Other failures still revert to Open, so the client can retry.
- **Blob Begin.** Begin is refused with 409 "Someone else is uploading this file right now." when another user's unexpired Open or Completing session exists for the same (target, final path). This uses the new store method `ListActiveForPathAsync`. The same user resumes as before. Files is unaffected, because each session has its own staging file.
- **Tests:**
  - `Begin_BlobPathAnotherUserIsUploading_Conflict` (covers same size and a different size)
  - `Begin_BlobPathOtherUsersSessionExpired_Ok`
  - `Begin_FileSharePathAnotherUserIsUploading_OwnSessionOk`
  - `Complete_BlobBlocksDiscardedByOtherSession_ConflictAndAborted`: the fake reproduces the shared block list, since the second commit clears staging
  - `Complete_InvalidBlockList_ConflictWithRestartMessage`
  - `Complete_FilesStagingRemovedExternally_ConflictAndAborted`
  - `Complete_StagedDataLost_409` (endpoint)
  - `Map_InvalidBlockList_ConflictWithRestartMessage`

### IMPORTANT 3: stuck Completing sessions
- **Claim time.** `UploadSession.ClaimedAt` and the `claimed_at` column record when a session entered Completing. The column is added with `ALTER TABLE upload_sessions ADD COLUMN IF NOT EXISTS claimed_at TIMESTAMPTZ NULL`. `TrySetStatusAsync` gained an optional `claimedAt`, and `CompleteAsync` passes `now`.
- **New store method.** `ListStuckCompletingAsync(claimedBefore)` lists Completing sessions claimed before the cutoff, or with no claim time.
- **The sweep settles stuck sessions.** It looks at sessions claimed more than `Transfers:CompletingTimeoutMinutes` ago (default 30, added to `appsettings.json`), then reads the final file:
  - **Final file present, not a folder, with the session's size:** the session goes from Completing to Completed and gets a Success row with `Bytes = Size`. The row is attributed to the session's user (UPN read from the user store) and to the target's customer, with correlation id `upload-cleanup:{id}` and an empty SourceIp.
  - **Otherwise:** `AbortUploadAsync` best-effort, Completing to Aborted, and a Failed row "The upload was interrupted while it was being finished."
  - **Storage unreachable during the check:** the session is left Completing for the next sweep, with no row written.
- **No double rows.** `CompleteAsync` now moves Completing to Completed with a conditional update, so it writes no second Success row if the sweep has already settled the session.
- **New cleanup dependencies.** `UploadCleanupService` now also takes `IUserStore`, `AuditService` and `IConfiguration`. DI resolves them, since all are singletons.
- **Tests:**
  - `Sweep_StuckCompletingWithFinalFile_CompletedAndAuditsSuccess`
  - `Sweep_StuckCompletingWithoutFinalFile_AbortedAndStagingRemoved`
  - `Sweep_StuckCompletingWrongSizeFile_Aborted`
  - `Sweep_StuckCompletingStorageUnreachable_LeftForNextSweep`
  - `Complete_RecordsClaimTime`
  - `Sweep_SessionBeingCompleted_NotTouched` now uses a fresh claim time

### Minors
- **PG indexes:**
  - `idx_upload_sessions_owner_path (user_id, target_id, final_path)`
  - `idx_upload_sessions_target_path (target_id, final_path)`, added for the Blob other-user check
  - `idx_upload_sessions_status_expiry (status, expires_at)`
- **Not done: Files Begin still creates missing parent folders.** I left `FileShareBackend.BeginUploadAsync` unchanged. Deferring folder creation is awkward: the staging file has to live somewhere at Begin, and Azure Files cannot create a file in a folder that doesn't exist. The fix would put staging in the deepest existing ancestor and make `StagingPath` backend-chosen, which conflicts with R19's fixed `{folder}/.{name}.cssupload-{id}` convention and with the fake/real parity. So an aborted or expired Files upload into a new folder still leaves that empty folder behind.
- **Recorded, not changed:** #5 (an Overwrite flag with no existing file) and #6 (several audit rows when Complete is retried).
- **PG integration test.** It now also covers `ListActiveForPathAsync` (Open, Completing and expired cases), the `claimed_at` round trip and `ListStuckCompletingAsync`.

### Remaining notes
- **Begin race.** Begin's other-user check and the session insert are not atomic. Two different users who call Begin on the same blob path within milliseconds can both get a session. The first Complete wins, and the loser now gets a clean 409 and restart instead of being stuck.
- **409 now has two meanings (relevant to Task 10).** `uploader.js` in the plan treats 409 from Begin as `OnConflict`, the overwrite prompt. Begin can now return 409 for two reasons: AlreadyExists, and "someone else is uploading". Both use status 409 with different `error` text. A 409 from Complete means "restart this file". A distinct status or error code could be added if Task 10 wants one.

---

## Fix round 2 (commit `9e89e65`)

**Suite:** 688 passed, 7 skipped, 0 failed. Build is 0 warnings and 0 errors. The PG integration run passed 6 of 6, using podman postgres:17 via 172.21.108.241. The container was stopped afterwards and no other podman commands were run.

### 1. R29: machine-readable error codes
- **Constants.** The codes live in `Endpoints/UploadErrorCodes.cs`: `exists`, `in_progress`, `restart`, `retry`, `invalid`, `denied`, `not_found`, `too_large`, `busy`, `unavailable`.
- **Body shape.** Every upload error is now `{ "error", "code" }`.
- **How codes are assigned.** `AccessResult` and `AccessResult<T>` gained an optional `Code`, which the service sets where the outcome alone is ambiguous:
  - `retry` for a corrupted or incomplete piece
  - `in_progress` when another user's blob upload holds the name
  - `restart` when the staged data is lost

  When the service sets no code, `ErrorFor` derives one from the outcome: Invalid → `invalid`, AlreadyExists → `exists`, Conflict → `restart`, Denied → `denied`, NotFound → `not_found`, anything else → `unavailable`. The guard's own errors carry codes too: cross-site → `denied`, antiforgery → `invalid`, bad body → `invalid`, Content-Length too large → `too_large`, store failure → `unavailable`.
- **429.** `RateLimits.OnRejected` now writes JSON `{ error, code: "busy" }` for `/api/uploads*` paths. Downloads keep `text/plain`. `Retry-After: 5` is unchanged.
- **Tests:**
  - `ErrorCodes_Begin`: exists, invalid name, invalid size, denied, missing antiforgery, cross-site
  - `ErrorCodes_Chunk`: retry, malformed MD5, wrong length, index, not_found, too_large, unavailable
  - `ErrorCodes_CompleteAndStatus`: missing pieces → invalid, and not_found
  - `ErrorCodes_CompleteLost_Restart`
  - `ErrorCodes_StoreFailure_Unavailable`
  - `ErrorCodes_RateLimited_BusyJsonOnUploads_TextOnDownloads`
  - `ErrorCodes_OtherUserUploadingBlob_InProgress`
  - Service tests also assert `Code` on the retry, in_progress and restart paths.

### 2. R30: a definitive "landed" check
- **New interface method.** `IStorageBackend.HasLandedAsync(t, stagingPath, finalPath, sessionId, size, ct)`.
  - **Files:** landed means the staging file does not exist and the final file does. Both checks use `ShareFileClient.ExistsAsync`.
  - **Blob, size > 0:** landed means the first block in `GetBlockListAsync(Committed)` has `Name == ChunkMath.BlockId(sessionId, 0)`. The SDK docs confirm `BlobBlock.Name` is the base64 id, the same form as `BlockId`.
  - **Blob, size 0:** landed means the committed block list is empty and the metadata `cssp_upload_session` equals `sessionId:N`. A 404 means not landed.
- **Metadata stamp.** `CompleteUploadAsync` gained an optional `uploadSessionId`. For blobs it stamps `cssp_upload_session` on every commit; the service always passes it.
- **Fake.** The fake records what each blob commit wrote (block ids, session, version) and mirrors both rules.
- **Complete.** If the commit fails with NotFound, Conflict or AlreadyExists, Complete asks `HasLandedAsync`:
  - **Landed:** Completed, one Success row, 204.
  - **Not landed:** the existing outcome. NotFound or Conflict give 409 `restart`; AlreadyExists reverts to Open and gives 409 `exists`.
  - **The check itself fails:** 503 and the session stays Open.

  AlreadyExists is included because a retried no-overwrite blob commit whose first attempt landed fails with 412, which maps to AlreadyExists.
- **Sweep.** The sweep's stuck-Completing settle uses the same check. Landed → Completed plus Success. Not landed → abort (for Files this removes the staging file) plus Failed. Storage unreachable → left for the next sweep.
- **Tests:**
  - `Complete_CommitAlreadyLanded_TreatedAsSuccess`: Files, Blob 10 bytes, Blob 0 bytes
  - `Complete_ZeroByteBlobFromOtherSession_NotTreatedAsLanded`
  - `Complete_BlobCommitStampsSessionMetadata`
  - `Complete_LandedCheckUnreachable_UnavailableAndStaysOpen`
  - `Sweep_StuckCompletingBlobLanded_Completed`: 10 bytes and 0 bytes
  - `Sweep_StuckCompletingBlobSameSizeFromOtherSession_Aborted`
  - `Sweep_StuckCompletingFilesStagingStillPresent_AbortedEvenIfFinalExists`: replaces the old size-based test
  - `HasLanded_Files_StagingGoneAndFinalPresent`
  - `HasLanded_Blob_FirstCommittedBlockIdentifiesSession`

### 3. Blob name lock window
- **Schema.** New column `last_activity_at`, added with `ALTER TABLE upload_sessions ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ NULL`. `UploadSession.LastActivityAt` maps to it.
- **Activity tracking.** `MarkChunkAsync(id, index, at)` now also stamps the activity time. PG uses `GREATEST`, so out-of-order calls never move it backwards.
- **Which sessions block Begin.** Another user's session blocks Begin only if one of these holds:
  - it is in Completing
  - it was created within `Transfers:BlobLockMinutes` (default 15, added to `appsettings.json`)
  - it received a chunk within that window

  A stale session doesn't block. Its owner's later Complete takes the restart path.
- **Tests:**
  - `Begin_BlobPathOtherUsersSessionStale_DoesNotBlock`
  - `Begin_BlobPathOtherUsersSessionRecentChunk_StillBlocks`: blocked at +10 min after the last chunk, allowed at +16
  - `Begin_BlobLockMinutesFromConfig`
  - `Complete_StaleSessionOverwrittenByOtherUser_Restart`

### 4. Race hygiene
- **Claim guard.** `TrySetStatusAsync` gained `requireClaim` and `expectedClaim`. In PG the guard is `AND claimed_at IS NOT DISTINCT FROM @expectedClaim`. Both sweep paths (expired Open and stuck Completing) pass the claim time they listed, so a session re-claimed since the listing is left alone. Test: `Sweep_SessionReclaimedSinceListed_LeftAlone`. The PG integration test also covers this.
- **Complete's move to Completed fails** (the sweep already settled it): a LogWarning, and no second row. Test: `Complete_SucceededButSweepAlreadySettled_NoSecondSuccessRow`.
- **Complete's "lost" move to Aborted fails:** a LogWarning and no Failed row. It returns Ok if the sweep marked the session Completed, otherwise 409 `restart`. Test: `Complete_LostButSweepAlreadySettled_NoSecondFailedRow`.
- **Timeout floor.** `CompletingTimeoutMinutes` now has a floor of 10 (`MinCompletingTimeoutMinutes`). Test: `Sweep_CompletingTimeoutFlooredAtTenMinutes`.

### 5. Buffers and the fake
- `ArrayPool<byte>.Shared.Return(buffer, clearArray: true)`.
- Removed the fake's unreachable `EndOfStreamException` branch. The fake still requires `Length - Position == length` up front, so a short stream can't reach the read.

### Deferred
- **Files empty-parent-folder minor.** Still deferred; it needs an amendment to R19.

### Notes
- **Blob landed check before any commit.** The first-block check reads only the committed list, so uncommitted blocks staged by other sessions don't affect it. If two sessions of the *same* user both commit with an identical first block, they would match, but block ids embed the session id, so they can't.
- **Blob landed check after an overwrite.** If another session later overwrites the blob, the first committed block changes and the earlier session reads as not landed. That only matters for a session still stuck in Completing, and there "aborted" plus a Failed row is the conservative answer.

---

## Fix round 3 (commit `9917347`): security-auditor findings, R32

**Suite:** 734 passed, 7 skipped, 0 failed. Build is 0 warnings and 0 errors. The PG integration run passed 6 of 6 (podman postgres:17 via 172.21.108.241). The container was stopped afterwards and no other podman commands were run.

**One commit, not two.** I kept this round as a single commit. The H1/M1 store and schema changes (new columns, `EnsureStagingSizeAsync`, `PurgeAsync`) and the L2/L3 audit and guard changes run through the same service, cleanup, backend and test files, so neither half builds or passes on its own.

### H1: quota exhaustion (R32)

**(a) Staging starts empty and grows only as pieces land**
- `BeginUploadAsync` creates the Files staging file at size 0.
- New `IStorageBackend.GrowStagingAsync` calls `SetHttpHeadersAsync(new ShareFileSetHttpHeadersOptions { NewSize })`. It is a no-op for Blob.
- **How the current size is tracked.** The session's highest-written end is stored in a new `staging_size` column. I did not read file properties, because that would cost an extra storage call per chunk.
- **Parallel chunks.** `IUploadSessionStore.EnsureStagingSizeAsync(id, size, grow)` serialises growth per session: PG takes `SELECT … FOR UPDATE` in a transaction around the grow call, and in-memory uses a per-session semaphore. Without this, a smaller resize landing after a larger one would shrink the file under a piece another chunk had just written. Growth happens only when the new end is beyond the recorded size.
- **Fake.** It tracks logical size and refuses `WriteChunk` past it, mirroring the 416 InvalidRange real Files returns.
- **Zero-byte.** A zero-byte upload is still a 0-length file, renamed at Complete.
- **Tests:**
  - `Begin_FileShare_StagingCreatedEmpty_NotAtDeclaredSize`: a 40 GiB declared size leaves 0 bytes staged
  - `PutChunk_FileShare_GrowsStagingOnlyToHighestWrittenEnd`
  - `PutChunk_FileShare_ParallelPiecesNeverShrinkStaging`
  - `PutChunk_Blob_NeverGrowsStaging`
  - `Begin_Files_CreatesEmptyStaging_GrowsOnlyWhenAsked` (fake)
  - The PG integration test covers concurrent `EnsureStagingSizeAsync` and rollback on failure.

**(b) Per-user caps in Begin, checked before any storage call**
- `Transfers:MaxOpenSessionsPerUser` (default 8) limits Open plus Completing, unexpired sessions.
- The reserved total must satisfy `SumOpenSize + new ≤ Transfers:MaxUploadBytes`.
- Over the cap, Begin returns `AccessOutcome.Busy`, which is new and maps to 429 with code `busy` and `Retry-After: 5`. The message is "You have too many uploads in progress. Finish or cancel one, then try again."
- A resume skips the caps. An older session of the same file that is about to be replaced is not counted.
- Store methods: `CountOpenForUserAsync` and `SumOpenSizeForUserAsync` (PG parameterised, plus in-memory), backed by a new index `(user_id, status)`.
- **Tests:**
  - `Begin_TooManyOpenSessions_Busy`: another user is unaffected, and a resume is still allowed
  - `Begin_ReservedBytesOverLimit_Busy_BeforeAnyStorageCall`
  - `Begin_SettledSessionsFreeTheirReservation`
  - `Begin_ReplacingOlderSessionOfSameFile_DoesNotCountAgainstCaps`
  - `Begin_TooManyUploads_429Busy` (endpoint)

**(c) Idle expiry**
- `ListSweepableOpenAsync(now, idleBefore)` returns Open sessions that are expired, or whose `COALESCE(last_activity_at, created_at)` is before `now - Transfers:IdleSessionMinutes` (default 120).
- **Tests:** `Sweep_IdleOpenSession_AbortedBeforeExpiry`, `Sweep_IdleSessionMinutesFromConfig`.

**(d) Replacing an older session of the same file**
- When the same user begins the same (target, final path) at a different size, each older Open session is aborted conditionally, its staging is removed (with the L3 guards), and a Failed audit row "Replaced by a new upload of the same file" is written. Only then is the new session created.
- **Tests:** `Begin_SameFileDifferentSize_ReplacesOlderSession` (Files and Blob), `Begin_SameFileOtherUserDifferentSize_DoesNotReplace`. These replace the old blob-discard test, whose setup is now impossible.

### M1: orphaned staging
- **New `staging_removed` column.**
  - Blob sessions are created with it true, because a blob abort is a no-op.
  - It is set true by a successful Abort, Complete, replacement, sweep abort, or Begin-failure cleanup.
  - Every other path (Complete lost, settle-as-aborted, sweep expiry, Begin failure) aborts best-effort and leaves the flag false if storage fails.
- **Retry.** The sweep retries `AbortUploadAsync` for every row from `ListAbortedWithStagingAsync` until it succeeds.
- **Purge.** `PurgeAsync(cutoff, hardCutoff)` deletes settled rows older than 7 days when nothing is left in storage (Completed, or Aborted with staging removed). After 30 days it deletes them regardless. For each Aborted row dropped while its staging was still unconfirmed, it logs an Error naming the session, target and staging path.
- **Begin failure.** The Begin storage-failure path now calls the guarded removal, because `CreateAsync` may have succeeded server-side.
- **Tests:**
  - `Sweep_AbortFailed_RetriedNextSweep_ThenRowPurgeable`: the row is kept past 7 days until removal succeeds
  - `Sweep_StagingNeverRemovable_RowDroppedAfter30Days`
  - `Begin_BeginUploadFails_StagingRemovedAndConfirmed`
  - `Begin_BeginUploadFails_AbortAlsoFails_SweepRetries`
  - `Complete_Lost_AbortFails_SweepRetries`
  - PG integration covers `ListAbortedWithStaging`, soft and hard purge, and the abandoned list.

### M2: chunk rate bucket
- **Limiter chain.** `RateLimits.Chunks` is now `Chained(Shared(global concurrency), Chained(per-user concurrency, per-user token bucket))`.
  - The bucket is `Transfers:ChunksPerMinute`, default 600.
  - Concurrency limiters run before the bucket, so a request they reject doesn't spend a token.
- **Shared global limiter.** It is wrapped so that idle cleanup of a partition never disposes it, and its `IdleDuration` is `MaxValue` so it never keeps a partition alive.
- **Checks before the database.** `UploadSessionService.CheckChunkFormat` (index ≥ 0, Content-Length present and within ChunkSize, MD5 is 16 bytes of base64) runs before `LoadAsync`.
- **Tests:**
  - `PutChunks_PerUserBucket_429BusyAfterChunksPerMinute`: another user is unaffected
  - `PutChunk_MalformedRequest_InvalidWithoutTouchingTheStore`: a theory of 5 cases against a store that throws
  - `PutChunk_MalformedMd5_RejectedBeforeSessionLookup` (endpoint)
  - The existing `PutChunks_DoNotSpendTransfersBucket` still passes.

### Lows
- **L1: slow and stalled bodies, and global memory.**
  - On the chunk PUT the guard sets `IHttpMinRequestBodyDataRateFeature.MinDataRate = new MinDataRate(Transfers:MinChunkBytesPerSecond (65536), 5 s grace)`.
  - `ReadPieceAsync` links a 120 s timeout. A timeout returns Invalid with code `retry`; a caller cancellation still propagates.
  - `Transfers:MaxConcurrentChunksTotal` (default 128) is a single global concurrency limiter for all users. The xmldoc gives the memory bound: 64 MiB per user, 512 MiB for the app.
  - **Tests:** `PutChunk_StalledBody_TimesOutAsRetry`, `PutChunks_GlobalConcurrencyCap_AppliesAcrossUsers`.
  - **Not covered:** TestServer has no Kestrel rate feature, so the min-data-rate setting itself isn't asserted. `PutChunk_SetsMinimumBodyDataRate` only pins that the guard tolerates its absence.
- **L2: audit.**
  - Begin writes an `UploadBegin` row with the path, `Bytes` = declared size, and `Error` = "overwrite" when set. It is written only when a new session is created; a resume writes none.
  - Sweep expiry and idle aborts write a Failed row "Upload expired".
  - The operation on Complete, Abort, replacement and sweep rows is `Overwrite` when `session.Overwrite && existed_at_begin`, otherwise `Upload`. The new `existed_at_begin` column is set at Begin and refreshed on resume.
  - **Tests:** `Begin_WritesUploadBeginAuditRow`, `Complete_OverwriteOfExistingFile_AuditedAsOverwrite`, `Begin_AuditsUploadBegin` (endpoint), and an extended `Sweep_ExpiredOpenSession_Aborted`.
- **L3: delete guards.**
  - **In the backend.** `FileShareBackend` refuses `BeginUpload`, `GrowStaging` and `AbortUpload` on any path whose leaf isn't a staging name; the fake mirrors this.
  - **In the service and the sweep.** `UploadSessionService.StagingIsOwnedBy(session)` requires `StagingPath == StagingPathFor(FinalPath, Id)` for Files (and staging == final for Blob). Before deleting, the service and sweep both also require the stored `target_kind` (a new column) to equal the current target's kind; otherwise they log an Error and skip storage.
  - **Tests:**
    - `Sweep_TamperedStagingPath_NotDeleted`
    - `Sweep_OtherSessionsStagingName_NotDeleted`
    - `Sweep_TargetChangedKind_SkipsStorageCleanup`
    - `StagingIsOwnedBy_OnlyTheDerivedName`
    - `Abort_Files_RefusesNonStagingName` (fake theory)
- **L4: request shape on control routes.**
  - The guard sets `MaxRequestBodySize` to 16 KiB for POST, GET and DELETE, and to ChunkSize for the PUT. It rejects a declared `Content-Length` above either limit with 413 `too_large`.
  - A POST that isn't `application/json` gets 415 with code `invalid`. The one exception is Complete with no body and no content type. This check runs before antiforgery.
  - Begin reads its body through its own 16 KiB cap. A chunked body has no Content-Length, and TestServer showed that `MaxRequestBodySize` isn't enforced by every host.
  - **Tests:** `Begin_NonJsonContentType_415` (text, form, multipart), `Complete_TextBody_415_EmptyBody_Allowed`, `Begin_BodyOver16KiB_413`.

### Carried minors
- **Files landed check and the session stamp.**
  - The Files staging file is created with `ShareFileCreateOptions.Metadata = { cssp_upload_session: id }`.
  - **Does Rename keep the metadata?** The SDK docs for `ShareFileRenameOptions.Metadata` say only "Optional custom metadata to set on the destination". They do not guarantee the source's metadata carries over when it is unset, so I pass the stamp explicitly on rename.
  - Files landed now means: staging gone, final file present, and `UploadMetadata.Matches(final metadata, sessionId)`.
  - The shared helper `UploadMetadata` in `IStorageBackend.cs` is used by both backends.
  - **Test:** `Complete_FilesStagingGoneAndSomeoneElsesFinalPresent_RestartNotSuccess`.
- **`UploadErrorCodes` moved** (via `git mv`) to `Services/Transfers` with namespace `CloudSelfService.Services.Transfers`. The service no longer references `Endpoints`.
- **A storage NotFound during a chunk** now returns Conflict with code `restart`. Test: `PutChunk_StagingGone_ConflictRestart`.
- **`StorageErrorMapper.ShortChunkMessage` removed.** The text now lives in `UploadSessionService.ShortChunkMessage`.

### Supporting changes
- **Test helpers.**
  - A shared `CloudSelfService.Tests/Infrastructure/ThrowingSessionStore.cs` replaces the two copies.
  - `FakeStorageBackend.SeedFile(targetId, path, bytes)` seeds test files directly. Seeding used to go through the upload protocol with non-staging names, which the new guards refuse.
  - `FakeStorageBackend.StagingLength` and `TotalStagingBytes` were added for assertions.
- **Schema.** New columns: `target_kind`, `existed_at_begin`, `staging_removed`, `staging_size` (all `ADD COLUMN IF NOT EXISTS` with defaults), plus the index `idx_upload_sessions_user_status`.
- **Config added to `appsettings.json`:** `MaxOpenSessionsPerUser` 8, `IdleSessionMinutes` 120, `ChunksPerMinute` 600, `MaxConcurrentChunksTotal` 128, `MinChunkBytesPerSecond` 65536.
- **Regression check.** I disabled the caps, the replacement, the cleanup ownership guard and the cleanup retry loop in the source, and 10 of the new service tests failed. I then restored the source.

### Concerns / notes
- **Blob quota is not bounded at the storage level.** For Blob, H1(a) doesn't apply: uncommitted blocks count toward account capacity, not share quota. Blob is bounded only by the per-user caps in (b) and the idle/expiry sweep. Azure discards uncommitted blocks after about 7 days.
- **Grow is a second storage call on some chunks.** It happens only when a piece extends the file, so for sequential uploads that is most chunks. The PG row lock is held across the grow call, so growth for one session is serial; writes are still parallel.
- **Begin race.** The caps check and the session insert are not atomic. Concurrent Begins from one user can overshoot the caps by the number of in-flight Begins. That number is itself bounded by the Transfers concurrency limit (4 per user).
- **Files empty-parent-folder minor:** still deferred, pending an R19 amendment.

---

## Fix round 4 (commit `b6ae4e5`): final cleanup

**Suite:** 746 passed, 7 skipped, 0 failed. Build is 0 warnings and 0 errors. The PG integration run passed 6 of 6 on three consecutive runs (podman postgres:17 via 172.21.108.241). The container was stopped afterwards and no other podman commands were run.

1. **R33: idle sweep.**
   - `ListSweepableOpenAsync` now returns Open sessions that are expired, or that have `last_activity_at IS NULL AND created_at < idleBefore`. PG and in-memory match.
   - A session that has received any chunk keeps its full 24 h lifetime.
   - **Tests:**
     - `Sweep_SessionThatNeverReceivedAChunk_AbortedWhenIdle` (was `Sweep_IdleOpenSession_AbortedBeforeExpiry`)
     - `Sweep_SessionWithAChunk_KeepsFullLifetimeDespiteIdle`: still Open at 23 h, resumes with `received = [0]`, swept at 24 h
     - The PG integration test covers both rules.

2. **MinDataRate on HTTP/1.1 only.**
   - The setting moved into `UploadEndpoints.ApplyMinDataRate(ctx, config)`, which returns early unless `HttpProtocol.IsHttp11(ctx.Request.Protocol)`. It is still called from the guard.
   - **Test:** `ApplyMinDataRate_OnlyOnHttp11` is a theory over HTTP/1.1, 2, 3 and 1.0. It uses a fake `IHttpMinRequestBodyDataRateFeature` that throws `NotSupportedException` on a non-null set unless the request is HTTP/1.1, as Kestrel does.

3. **Files Complete checks the staging size.**
   - `CompleteUploadAsync` gained an optional `expectedSize`, and the service passes `session.Size`.
   - Files calls `GetPropertiesAsync` on the staging file and requires `ContentLength == expectedSize`. A mismatch throws Conflict with the new `StorageErrorMapper.StagingIncompleteMessage`.
   - In the service that takes the existing lost path: Aborted, staging removed, a Failed audit row, and 409 `restart`.
   - The fake mirrors this using its logical staging size.
   - **Tests:**
     - `Complete_FilesStagingShorterThanSession_RestartAndAborted`: the fake shrinks the staging file
     - `Complete_FilesStagingLongerThanSession_Restart`

4. **Flaky PG grow assertion.**
   - Now two concurrent `EnsureStagingSizeAsync(9)` calls must produce exactly one grow: the row lock serialises them and the second sees the size already reached. A following call with size 6 then must not grow.
   - The old {3, 9, 6, 9} ordering could produce 1 to 3 grows depending on scheduling.
   - Run three times against real PG, all green.

5. **Connection-pool pressure.**
   - `RateLimits.DefaultMaxConcurrentChunksTotal` is now 64, with a comment that it must stay below Npgsql's default MaxPoolSize of 100. `appsettings.json` and the memory-bound xmldoc are updated (256 MiB for the app).
   - The grow call runs under a linked 30 s CTS (`UploadSessionService.GrowTimeout`, settable for tests). If that CTS fires and not the request token, the chunk returns Invalid with code `retry` ("Storage was slow to respond; the piece will be retried."). The transaction rolls back, so `staging_size` is unchanged.
   - **Test:** `PutChunk_GrowTimesOut_RetryAndChunkNotWritten` uses the new fake hook `GrowStagingDelay`. A retry after the delay is cleared succeeds.

6. **Refused cleanup stops retrying.**
   - I added a **`cleanup_refused` bool column** (ADD COLUMN IF NOT EXISTS, default false). I chose a separate flag over overloading `staging_removed`, so the data stays truthful: the staging file was not removed.
   - Each refusal path logs its Error once and then calls `SetCleanupRefusedAsync`. The paths are: target deleted, target kind changed, and a path that isn't the session's own staging name. The service's `RemoveStagingAsync` and the sweep both do this.
   - `ListAbortedWithStagingAsync` excludes refused rows. `PurgeAsync` still removes them after 30 days but no longer lists them as "abandoned", since they were already logged.
   - **Tests:**
     - `Sweep_RefusedCleanup_LoggedOnceAndNotRetried`: three sweeps produce exactly one Error-level log line and no AbortUpload call
     - `Sweep_TargetDeleted_RefusalRecorded`
     - PG integration covers `SetCleanupRefused` and exclusion from both lists.

7. **More staging-name checks.** `FileShareBackend.WriteChunkAsync` and `CompleteUploadAsync` (for the rename source) now call `RequireStagingName`. The fake mirrors both. Test: `WriteAndComplete_Files_RefuseNonStagingSource`.

8. **A 416 on Put Range is a restart.**
   - `FileShareBackend.WriteChunkAsync` maps `RequestFailedException` 416 to Conflict with `StagingIncompleteMessage`.
   - The service turns a Conflict carrying that message into 409 `restart`. Previously this was a generic 503.
   - The fake's write-past-end error is now the same Conflict, so `Begin_Files_CreatesEmptyStaging_GrowsOnlyWhenAsked` was updated.
   - **Test:** `PutChunk_FilesStagingTruncatedExternally_ConflictRestart`.

**Regression check.** I broke each rule in the source: the idle rule, the Complete size check, the grow timeout, the refused-cleanup marking and the HTTP/1.1 gate. 8 of the new tests failed, and I then restored the source.

**Not changed.** Following the instruction, nothing outside these 8 items was touched. The Files empty-parent-folder minor stays deferred, pending an R19 amendment.

---

## Fix round 5 (commit `81719c3`): final round

**Suite:** 750 passed, 7 skipped, 0 failed. Build is 0 warnings and 0 errors. The PG integration run passed 6 of 6 (podman postgres:17 via 172.21.108.241). The container was stopped afterwards and no other podman commands were run.

### 1. R34: Files staging grows once, to the full size
- **The change.** In `PutChunkAsync` the grow target is now always `session.Size`, never `offset + length`. It runs only while `session.StagingSize < session.Size`, through the same row-lock path (`EnsureStagingSizeAsync`) and with the same 30 s timeout that returns `retry`.
- **Why it can't truncate.** After the first successful grow `staging_size == Size`, so no further grow is attempted. A grow that timed out but that Azure applies late sets the same size again, so it can't truncate pieces written since. Zero-byte uploads never grow.
- **Docs.** The xmldoc on `IStorageBackend.BeginUploadAsync` and `GrowStagingAsync`, the `UploadSession` StagingSize doc and the service comment now say that quota is reserved when the first piece arrives, bounded by the per-user byte cap.
- **Fake.** Resizing staging smaller now discards staged blocks past the new end, as Azure Files does. Before, the fake could not show the truncation this fixes.
- **Tests.** Two incremental-growth tests (`GrowsStagingOnlyToHighestWrittenEnd`, `ParallelPiecesNeverShrinkStaging`) were removed or replaced.
  - `PutChunk_FirstChunkGrowsStagingToFullSize_OnceOnly` is a theory over the orders 2,0,1 (as asked) and 1,2,0. It asserts exactly one `GrowStaging` call, to `Size`, and an exact round trip. I added 1,2,0 because 2,0,1 alone would also pass under the old end-of-piece rule, since piece 2 is the last.
  - `PutChunk_LateDuplicateGrowToSameSize_KeepsWrittenBytes`: after all pieces are written, a duplicate grow to the same size leaves the SHA-256 intact.
  - `Fake_ShrinkingStaging_DiscardsPiecesPastTheEnd` pins the truncation model. A shrink, then a grow, then the last piece means Complete fails and nothing is published.
  - `PutChunk_ZeroByteUpload_NeverGrows`.
  - The existing `Begin_FileShare_StagingCreatedEmpty_NotAtDeclaredSize` still holds.

### 2. Restart loop
- **The change.** When the chunk path gets NotFound, or Conflict with `StagingIncompleteMessage` (the 416 case), the new `EndInterruptedAsync` runs before 409 `restart` is returned. It moves the session conditionally from Open to Aborted, removes the staging through the guarded path (`staging_removed` set on success), and writes a Failed audit row "Upload was interrupted; please start again." If another call has already ended the session, it does nothing.
- **Tests.** `PutChunk_StagingGone_ConflictRestart` and `PutChunk_FilesStagingTruncatedExternally_ConflictRestart` now assert Aborted, staging removed and one interrupted audit row. They also assert that the next Begin returns a NEW session id with an empty `Received`.

### Regression check
- With the old `offset + length` grow put back, `PutChunk_FirstChunkGrowsStagingToFullSize_OnceOnly` (1,2,0) fails.
- With `EndInterruptedAsync` removed, both restart tests fail.
- The source was restored after each check.

Nothing else was changed.
