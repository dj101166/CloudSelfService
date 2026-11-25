# Large uploads, admin upload limit, uploads that outlive a sign-in — design

**Date:** 2026-11-25. **Status:** draft, awaiting Dan's approval. **Target version:** 1.1.0.

## Why

During the DevOps demo a 70 GB database backup was refused by the 50 GB limit. Some clients have backups well over
195 GB, which is today's hard ceiling. Dan wants:

1. the default limit raised to 100 GB;
2. Admins and Owners able to change the limit inside the app, for a while or for good;
3. uploads beyond 195 GB handled by the portal itself, the way Azure Storage Explorer does, rather than asking clients to
   split their backups (striping stays the fallback if this slips);
4. a long upload that outlives the user's sign-in kept alive on the server, so after signing in again the user carries on
   from where it stopped.

## Where the 195 GB ceiling comes from

It is the portal's own piece size, not an Azure limit on the file:

| | Azure | Portal today |
|---|---|---|
| Blob | 50,000 blocks per blob; a block can be up to 4,000 MiB | every block is 4 MiB, so 50,000 x 4 MiB = 195.3 GiB |
| Files | each Put Range is at most 4 MiB; no count limit; file up to 4 TiB | the same 195.3 GiB cap is applied, needlessly |

Storage Explorer (AzCopy underneath) picks a block size big enough to fit the file in 50,000 blocks. We do the same.

## Approaches considered

- **A. Piece size chosen per upload (chosen).** Begin picks the piece size from the target kind and file size and stores it
  on the session; the browser cuts the file at that size. Memory stays bounded by budgeting in-flight bytes rather than
  piece count. Resume, per-piece MD5 and the commit flow are unchanged. `upload_sessions.chunk_size` already exists.
- **B. Server regroups 4 MiB pieces into large blocks.** Rejected: Azure blocks cannot be appended to, so pieces would wait
  on the app's local disk, adding temp-disk capacity, cleanup and multi-instance problems for nothing A doesn't give.
- **C. Client striping only.** No code; kept as the documented fallback.

## Goals

- Default upload limit 100 GiB (107,374,182,400 bytes).
- Admin-set limit up to a deployment ceiling (default 1 TiB), optionally expiring, every change audited.
- Files up to the limit on both Files and Blob targets.
- Server-side upload session survives a lapse in sign-in and stays resumable while pieces keep arriving.
- After signing in again, the user sees their unfinished uploads and can resume each by selecting the file.

## Non-goals

- Keeping an upload running through a sign-in renewal **without** selecting the file again. The 12-hour absolute sign-in
  ends the Blazor circuit, the page leaves, and a browser never lets a page re-read a file the user has not just picked.
  Doing this needs a popup sign-in plus swapping a live circuit's identity, which is security-sensitive; revisit only if
  users ask.
- Separate limits per customer or per target.
- Changing the download path.
- Changing `Auth:AbsoluteSessionHours` (an operator can, but it is a security control and stays 12 h).

## Anti-goals

- An Admin able to remove the limit altogether: the deployment ceiling is config-only.
- Memory growth with piece size: total buffered piece bytes must stay bounded as today (256 MiB app-wide, 64 MiB per user).
- A storage URL, key or SAS reaching the browser, or any storage call outside `StorageAccessService`'s decision.

## Design

### 1. Piece size per session

- `ChunkMath` becomes size-aware: `PieceSizeFor(StorageKind kind, long size)`, `ChunkCount(size, pieceSize)`,
  `ExpectedLength(size, pieceSize, index)`. Constants: `MinPieceSize = 4 MiB`, `MaxBlobPieceSize = 32 MiB`,
  `MaxFilesPieceSize = 4 MiB` (Put Range limit), `MaxBlocksPerBlob = 50,000`.
- **Files:** always 4 MiB. Platform maximum 4 TiB.
- **Blob:** the smallest of 4, 8, 16, 32 MiB that keeps the piece count at or under 50,000. Platform maximum
  32 MiB x 50,000 = 1,562.5 GiB.
- Begin stores the chosen size in `upload_sessions.chunk_size` and returns it (`chunkSize` already in the response). Every
  later call (chunk PUT length check, offsets, block IDs, commit, missing-piece count, status) uses the **session's**
  stored size, never the constant. Existing 4 MiB sessions keep working.
- Block IDs keep the six-digit index (50,000 fits).
- Chunk PUT body cap: the endpoint filter allows up to `MaxBlobPieceSize`; the service then enforces the session's exact
  expected length (as today).
- The piece read timeout scales with the piece: at least today's 120 s, and long enough for a full piece at
  `Transfers:MinChunkBytesPerSecond`.

### 2. Memory budget

- Replace "count of in-flight pieces" as the memory bound with **bytes**: app-wide `Transfers:MaxInFlightPieceBytes`
  (default 256 MiB) and per user `Transfers:MaxInFlightPieceBytesPerUser` (default 64 MiB), enforced in the server before
  the piece buffer is rented. Over budget answers 429 `busy` with `Retry-After`, which the uploader already handles.
  The existing count limiters stay as request-rate guards.
- Parallel pieces per upload, returned by Begin: enough to keep about 16 MiB in flight, never fewer than 2 nor more than
  `Transfers:ParallelChunks` (4 MiB → 4, 8 → 2, 16 → 2, 32 → 2).
- Buffers stay pooled and are cleared on return.
- *(Builder's call)* Capacity consequence, accepted: at 32 MiB pieces one upload holds 64 MiB, the whole per-user budget
  (fine, the uploader sends one file at a time per user) and a quarter of the app budget, so about four uploads over
  390 GB fit at once per instance; ordinary uploads (4 MiB pieces) are unaffected. A piece refused for **budget** is a
  wait, not a failure: the 429 carries a distinct code (e.g. `wait`) and `Retry-After`, and `uploader.js` waits on it
  indefinitely without counting it toward `MAX_BUSY_WAITS`. Document the four-at-once figure in `docs/operations.md`.
- *(Builder's call)* Per-user throughput stays where it is today (`ChunksPerMinute` 600 x 4 MiB ≈ 40 MB/s): the piece
  rate is charged in 4 MiB units, so a 32 MiB piece costs 8. Bigger pieces must not mean a faster per-user ceiling.

### 3. Upload limit

- **Deployment ceiling** `Transfers:MaxUploadBytesCeiling`, default 1 TiB, clamped to 1,562.5 GiB (the lower of the two
  platform maxima, Blob's) with a startup warning when clamped. Config only, never editable in the app.
- *(Builder's call)* **One limit for both kinds.** Because the ceiling never exceeds what Blob can take, a single number is
  valid on every target: the admin form, the effective limit, the reserved-bytes cap and the panel text all use it, and
  the size check stays before authorisation in Begin.
- Production sets no `Transfers__MaxUploadBytes` (checked: nothing in ZZ_Pulumi), so the new `appsettings.json` default
  takes effect on deploy.
- **Default** `Transfers:MaxUploadBytes`, now 100 GiB, clamped to the ceiling (the existing startup warning stays).
- **Override**, stored in PostgreSQL (in-memory store in Development): limit in bytes, expiry (nullable = permanent),
  set-by UPN, set-at, optional note. One current value; history lives in the audit log.
- **Effective limit** = the override if it is set and unexpired, else the default; always clamped to the ceiling. Expiry
  needs no job: it is evaluated on read. Read at Begin (a short cache, at most 30 s, is acceptable across instances).
- The limit applies when a **new** upload starts. An upload already open may be resumed and finished after the limit is
  lowered (it reserved its size under the old limit).
- The per-user reserved-bytes cap uses the effective limit, as today.
- The "too large" message names the effective limit, as today.

### 4. Admin page: `/admin/settings`

- `[Authorize(Policy = Policies.StaffAdmin)]` (Admins and Owners), nav link in the Admin group. `AdminService` re-checks the
  effective role through `StaffAccess` on save, like every admin action.
- Shows: default (from config), ceiling, current effective limit and where it comes from ("override until 2026-12-01,
  set by x" or "default").
- Form: new limit in GB (1 to ceiling), **Permanent** or **Until** a date and time (must be in the future), optional note.
  Save, and **Revert to default** (clears the override).
- *(Builder's call)* **Time zone:** the form takes and shows the expiry in the browser's local time (offset captured
  through the existing JS interop), labelled with the zone; it is stored and audited in UTC, as the audit log already is.
- Every save and revert writes an audit event (new operation, e.g. `Settings.UploadLimit`) with before and after values,
  expiry and note.
- `data-testid` hooks for the web tests.

### 5. Upload sessions that stay alive

- **Sliding expiry.** Each landed piece pushes `expires_at` to `min(created_at + Transfers:MaxSessionDays,
  last piece + Transfers:SessionHours)`. Defaults: `SessionHours` 24 (meaning changes from "after Begin" to "after the
  last piece"), `MaxSessionDays` 7 (new; bounds Files staging that holds share quota, and stays inside Blob's one-week
  uncommitted-block retention). Folded into the existing `MarkChunkAsync` update, so no extra write per piece.
  `IdleSessionMinutes` (a session no piece ever reached) is unchanged.
- **Sign-in lapse.** When the uploader sees the session has ended (redirect or 401), running uploads are marked
  **Paused: sign in again to carry on**, not failed, and nothing is aborted server-side.
- **Warning ahead of the 12 h end.** While an upload runs and the sign-in ends within 15 minutes, the upload panel says
  when it ends and that the user will need to select the file again afterwards. The page needs the sign-in end time
  (issued-at + `AbsoluteSessionHours`) from the server.
- **Resume after sign-in.**
  - Files home shows an **Unfinished uploads** card for the signed-in user: file name, share or container display name,
    folder, percent received, last activity, and when it will be discarded. Each row links to its folder, and has
    **Discard** (aborts the session, as Cancel does today).
  - The Browse page lists that folder's unfinished uploads in the upload panel as **Waiting for the file** with a
    **Select file** button (a user click, so the file picker may open). Selecting the same file resumes through the
    existing Begin match (same name, size and first-4-MiB fingerprint). A different file is a new upload, as today.
    *(Builder's call)* The resume reuses the session's stored overwrite choice, so an overwrite upload does not ask
    again; Begin still re-authorises Overwrite against the user's current access, as it does today.
  - The list comes from a new `UploadSessionService` method that returns only the caller's own Open, unexpired sessions on
    targets where the caller's current access still allows the upload; it goes through `StorageAccessService` for that
    check and makes no storage call.
- **Fingerprint.** `firstPieceMd5` becomes explicitly the MD5 of the file's first 4 MiB, independent of piece size, so the
  browser can compute it before Begin as it does now.

### 6. Text and docs

- Help: "An unfinished upload is kept for 24 hours after its last piece arrived, and at most 7 days. If you are signed out
  part-way, sign in again and select the same file to carry on." Mention the size limit is shown on the upload panel.
- Upload panel shows the effective limit ("Files up to 100 GB").
- `docs/operations.md`: new keys, changed meaning of `SessionHours`, the admin page, and the striping fallback for files
  over the ceiling.
- `CHANGELOG.md`: `## [1.1.0] - TBD` entries (Added, Changed).

## Constraints

- Single storage gate and `StorageGateArchitectureTests` allow-list unchanged.
- `/api/uploads/*` keeps its WAF exemption; pieces up to 32 MiB must be verified through the App Gateway in the test
  environment before release (enforcement is off on that path, so no size rule should apply).
- No hard-coded colours; sumi tokens. `data-testid` selectors.
- Security-auditor pass (routes, admin action, upload endpoint, body cap); unit-test-reviewer pass (ChunkMath, limit
  resolution, sliding expiry, memory budget, listing).

## Left to the builder

- Settings table shape (dedicated row or small key/value) and cache mechanism.
- How the byte budget is implemented (weighted semaphore or limiter), as long as rejection is 429 `busy`.
- Exact UI layout within the sumi system.
- How the page obtains the sign-in end time (a field on an existing JSON endpoint or a component parameter).
