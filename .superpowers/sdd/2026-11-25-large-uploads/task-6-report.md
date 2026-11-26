# Task 6 report: List a user's unfinished uploads; Files home card

Status: DONE. Commit `5c3bc0a Show a user's unfinished uploads on the Files home` (no trailer).

## Implemented
- `IUploadSessionStore.ListOpenForUserAsync(userId, now, ct)`: the user's Open sessions with `expires_at > now`, ordered by `COALESCE(last_activity_at, created_at) DESC`. Implemented in the in-memory store (LINQ, `LastActivityAt ?? CreatedAt`), the Pg store (SQL as specified) and the `ThrowingSessionStore` test double (throws, like the other request-path calls).
- `UnfinishedUpload` record in `UploadSessionService.cs`, with the fields and order from the brief.
- `UploadSessionService.ListUnfinishedAsync(userId, targetId?, folder?, ct)`:
  - Store query by user id, then a filter on `targetId` and on `folder == StoragePath.Parent(FinalPath)`.
  - Returns early (no access calls) when nothing is open.
  - Calls `ListReachableTargetsAsync` once per list to get target names. A session is dropped if its target isn't reachable, or if `AccessPolicy.Allows(await GetLevelAsync(...), OperationOf(session))` is false.
  - `ReceivedBytes` is the sum of `ChunkMath.ExpectedLength` over the distinct received indices that are in range.
  - `LastActivity` is `LastActivityAt ?? CreatedAt`.
  - No storage call and no new `IStorageBackendResolver` use. `StorageGateArchitectureTests` passes unchanged.
- `Pages/Index.razor` (Files home):
  - An "Unfinished uploads" `card` with a `table-wrap`/`table` above the target groups, shown only when the list is non-empty.
  - testids: `unfinished-uploads`, `unfinished-upload` with `data-session-id`, `unfinished-open`, `unfinished-discard`.
  - Each row shows:
    - the file name, through `StoragePath.DisplayName`
    - the target name and folder, both through `DisplayName`
    - `"{percent}% received"`
    - last activity and `"Discarded after {time}"`, both via `FileDisplay.Modified` with the browser zone (`cssp.timeZone` + `FileDisplay.ResolveZone`, the same as Browse.razor)
  - **Open folder** uses `FileDisplay.BrowseHref(TargetId, Folder)`.
  - **Discard** opens a `ConfirmDialog` with the exact message from the brief, then calls `AbortAsync(userId, sessionId, Audit.Next())` (`PageAuditContext`) and reloads the list.
  - CSS in `Index.razor.css`: three small `home__unfinished*` rules, tokens only.

## Failure-handling choice
- **Listing failure:** logged at Error, and the card is left out (empty list). The page's `UnavailableMessage` stays reserved for the main target list failing, so a broken session query never hides storage the user can still reach. When the target list itself fails, the existing early return applies and the unfinished list is not queried.
- **Discard failure** (an exception, or an outcome other than Ok, NotFound or Denied): shows the page's existing `UnavailableMessage` in an `alert alert--warning` above the card, and the circuit stays up. NotFound and Denied are treated as "already gone or no longer yours": the reload simply hides the row.

## Tests (TDD)
- Contract (in-memory `[Fact]`, Pg `[PgFact]`): `ListOpenForUser_ReturnsOnlyOwnOpenUnexpired`; `ListOpenForUser_NewestActivityFirst` (extra, covers the ORDER BY).
- Service:
  - `ListUnfinished_OtherUsersSessions_NotReturned` (also checks TargetName, Folder and FileName)
  - `ListUnfinished_GrantRevoked_Omitted`
  - `ListUnfinished_DowngradedToRead_Omitted`
  - `ListUnfinished_ReportsReceivedBytes` (8 MiB pieces, 20 MiB, pieces 0 and 2 → 12 MiB; also checks LastActivity and ExpiresAt)
  - `ListUnfinished_FolderFilter_OnlyThatFolder` (excludes subfolders and the root; also checks the root and target-only filters)
  - `ListUnfinished_MakesNoStorageCalls` (`_fake.Calls.Count` unchanged)
  - extra: `ListUnfinished_NoPieceYet_LastActivityIsCreatedAt`
  - extra: `ListUnfinished_OverwriteSession_ReportsOverwrite`
- RED:
  1. The first build failed with CS1061, because `ListOpenForUserAsync` and `ListUnfinishedAsync` did not exist.
  2. With empty-list stubs, 8 of the 10 new tests failed. GrantRevoked and Overwrite passed against the empty stub, as expected.
- GREEN: the filtered run passed 12, with 2 Pg tests skipped. Full project: **Passed 1303, Failed 0, Skipped 24** (Pg tests skip because CSSP_TEST_PG is unset). `dotnet build`: 0 errors, 0 warnings.
- Note: I first wrote an extra test assuming an Overwrite session needs Full. `AccessPolicy.Required(Overwrite)` is Contribute, so the test was wrong, not the code. I replaced it with the Overwrite-flag test.

## Smoke (Development, Dev auth, fake storage, port 5310)
1. Signed in as `customer-full`. With no sessions, no card rendered.
2. Began a 10 MiB upload via `/api/uploads` and reloaded. One row showed "smoke-big.bin / Demo share (Full) / 0% received / 2026-11-25 14:36 / Discarded after 2026-11-26 14:36" (local time), with the Open folder href `files/{targetId}`.
3. Discard showed the exact confirm text. Confirming removed the card, and the target card stayed.
4. The only console errors were the dev hot-reload 404 and the long-polling warning, which are unrelated.
5. Afterwards I stopped the app and removed the `.playwright-mcp/` folder.

## Files changed
- Services/Data/UploadSessionStore.cs
- Services/Transfers/UploadSessionService.cs
- Pages/Index.razor
- Pages/Index.razor.css
- CloudSelfService.Tests/UploadSessionStoreContractTests.cs
- CloudSelfService.Tests/UploadSessionServiceTests.cs
- CloudSelfService.Tests/Infrastructure/ThrowingSessionStore.cs (the new interface member)

All of these were LF before and stayed LF.

## Self-review / concerns
- **Database calls per row:** `GetLevelAsync` and `ReceivedChunksAsync` run once per session. Rows are bounded by `MaxOpenSessionsPerUser` (8 by default), so this is acceptable.
- **Changelog:** not written here. The plan assigns `CHANGELOG.md` (the 1.1.0 entry) to Task 8.
- **Security review:** none run. A page now injects `UploadSessionService` and calls `AbortAsync`, which re-authorises through `StorageAccessService` and is limited to the caller's own sessions. No new route, endpoint or storage path. The orchestrator may want the branch-level security-auditor pass to cover this.
- **Webapp tests:** no Playwright test added to `webapp-tests/`. The brief didn't list one, and Task 8 touches `test_cloudselfservice.py`.
