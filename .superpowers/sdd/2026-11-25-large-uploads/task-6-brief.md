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

