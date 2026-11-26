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

