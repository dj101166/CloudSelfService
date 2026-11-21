# Task 10 report: Files home, file browser and resumable upload UI

**Status:** DONE_WITH_CONCERNS (concerns are judgement calls for the controller, not open defects)
**Commit:** aab9c69 "Add Files home, file browser and resumable upload UI" (base 048434e). No Co-Authored-By trailer. `.superpowers/` not staged.
**Tests:** 847 passed / 10 skipped / 0 failed (`dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj`); `dotnet build` 0 warnings / 0 errors. Static guards (ComponentStyleContract, StylesheetIntegrity, IconNameContract, PageAuthorizationAttribute) all green.

## What was built

- **`/` Files home** (`Pages/Index.razor` + `.razor.css`): one `target-card` per reachable target with its display name, a kind badge (`File share` / `Blob container`) and a level badge (`Read` / `Contribute` / `Full`). Staff see cards grouped under customer headings with internal targets last under "Advanced internal", and a `target-filter` box that narrows by customer or target name. The empty state uses the brief's exact sentence. A store failure shows a warning alert.
- **`/files/{TargetId:guid}/{*Path}`** (`Pages/Files/Browse.razor` + `.razor.css`):
  - Breadcrumb `browse-breadcrumb` (Files › target › folders), a table (name, humanised size, local modified time, row actions), folders first then files.
  - Load more (`load-more`) when `ContinuationToken` is non-null. Pages are merged, de-duplicated by path and re-sorted.
  - Download is a plain `<a download>` to `/api/files/{id}/download?path=Uri.EscapeDataString(path)`.
  - Actions by level. Read: download. Contribute: also Upload (button and drop zone), New folder, Rename/Move. Full: also Delete, which needs the folder's name typed for a folder.
  - Every action goes through `StorageAccessService`. Results show in an inline `alert`; Unavailable, Busy and Conflict use `alert--warning`. Invalid and AlreadyExists keep the dialog open with the message inline.
  - **Denied** on any action or listing re-reads the level via `ListReachableTargetsAsync`. If the target is no longer reachable it renders `AccessDeniedContent` in place; there is never a navigation.
  - **R24:** store exceptions in every handler are caught → "The service is temporarily unavailable." (warning alert); the circuit survives.
  - **R27:** every file name, folder name, breadcrumb and target name renders through `StoragePath.DisplayName`; links and actions use the real path.
- **Dialogs:** `Shared/NameDialog.razor` (New folder / Rename-or-move; the path is relative to the share root, so a changed name alone is a rename) and `Shared/ConfirmDialog.razor` (Danger style, `RequireText`). Both close on Escape and submit on Enter, and focus their input on open.
- **`Shared/UploadPanel.razor`** (+ `.razor.css`):
  - Per-file progress bar, status line, and Pause / Resume / Cancel / "Replace…" buttons, plus Clear finished.
  - `OnConflict` opens ConfirmDialog with "{name} already exists. Replace it?". Confirm restarts that file with `overwrite = true`; Cancel marks it "Not uploaded…". Several conflicts queue.
  - A NavigationLock warns before leaving the Files pages while uploads are active.
  - On dispose it detaches from uploader.js. The server keeps each session, so re-selecting the file later resumes.
- **`wwwroot/js/uploader.js`** (`window.csspUploader`):
  - Gets the token from `/api/antiforgery` and sends the header it names (`RequestVerificationToken`).
  - Begins with `POST /api/uploads`, skips the indices in `received`, and keeps at most `parallelChunks` in flight. Each piece goes with `Content-MD5` from spark-md5 over a `File.slice` buffer and an exact body; then `complete`.
  - Branches on the JSON `code`:
    - `exists` → OnConflict.
    - `in_progress` → terminal, "Someone else is uploading this file right now."
    - `restart` / `not_found` → a fresh Begin, at most 3 times. On Begin itself these are terminal, to avoid a loop.
    - `retry` / `unavailable` / network failure → backoff `[1000, 2000, 4000, 8000, 16000]`, then the file becomes `interrupted` (resumable).
    - `busy` → waits Retry-After (default 5 s). A busy wait only counts as an attempt after 10 of them.
    - `denied` → terminal, and the page re-reads the level.
    - `invalid` / `too_large` → terminal.
  - A bare 401, or a redirect (`redirect: 'manual'` → opaqueredirect), means the session expired: every upload stops with a sign-in-again message.
  - Progress is batched to one flush every 250 ms (at most 4/s), plus a final flush on completion.
  - Files go one at a time. There is no eval, `new Function`, innerHTML or inline handler, and every URL is same-origin and relative.
  - The callbacks carry a leading upload `id` as well as the brief's arguments. `OnStatus` and `OnDenied` were added. See Concerns.
- **spark-md5 3.0.2** vendored at `wwwroot/lib/spark-md5/spark-md5.min.js`, unmodified, with its `LICENSE` and a `README.txt` (source URL and SHA-256).
  - Fetched from jsdelivr. SHA-256 `d80e84c8…f1c9` is identical to unpkg's copy.
  - The header is the UMD wrapper that sets `glob.SparkMD5`; it defines `SparkMD5.ArrayBuffer.hash`, and it contains no eval or `new Function`.
  - Note: the package's LICENSE file is the WTFPL text. package.json says `(WTFPL OR MIT)`; the README.txt records that we use it under the MIT option.
- **`/help`** (`Pages/Help.razor` + `.razor.css`, `[Authorize]`): signing in (guest invitation, accept, your own organisation's work account), the §5.3 level table, large uploads (Resume / re-select the same file, 24 h), and contacting Advanced support.
- **Shell:**
  - NavMenu has Files and Help. Files is a new `Shared/FilesNavLink.cs`, a NavLink subclass that stays active on `/files/*` too.
  - `refresh` icon added to Icon.razor and `KnownNames`.
  - App.razor loads spark-md5, then uploader.js, after session-guard.js.
  - `cssp.timeZone` and `cssp.openFilePicker` added to cloudselfservice.js. `openFilePicker` is unused; it is harmless, but could be dropped.
  - `.dialog__form` and `.dialog__message` rules in app.css.
- **Services/Files:**
  - `FileDisplay` (pure): size, modified time in the browser's zone, the zone resolver with a fixed-offset fallback when the host lacks tzdata, per-segment-escaped browse hrefs, download href, breadcrumbs, IsRename, CleanTypedPath.
  - `UploadItem` / `UploadStatusNames`.
  - `PageAuditContext`: scoped. It takes the source IP from `IHttpContextAccessor` on first use in the circuit, and a fresh correlation id per action. `AddHttpContextAccessor` was added to Program.cs.
- **CHANGELOG.md:** four Added bullets under `## [1.0.0] - TBD`.

## Tests

- `UploaderScriptTests` (8): RequestVerificationToken; Content-MD5; no `eval(`, `innerHTML =`, `innerHTML=` or `new Function`; the literal `[1000, 2000, 4000, 8000, 16000]`; only same-origin `/api/...` paths (no `http(s)://`); every UploadErrorCodes value is branched on; spark-md5 is vendored with its LICENSE; App.razor loads spark-md5 before uploader.js.
- `BrowseRouteTests` (8, real app via TestAppFactory prerender):
  - `Browse_OtherCustomersTarget_RendersAccessDenied_Not500`
  - `Home_CustomerRead_ShowsOnlyOwnTarget`
  - `Browse_OwnTarget_ListsSeededItems` (and, for Read, no upload or delete markup)
  - `Home_StaffOwner_GroupsByCustomer`
  - `Help_SignedIn_Renders`
  - `Browse_StoreDown_ShowsUnavailableAlert_Not500` and `Home_StoreDown_ShowsUnavailableAlert_Not500` (R24, using a throwing ITargetStore)
  - `Home_NoTargets_ShowsEmptyState`
- `FileDisplayTests` and `UploadItemTests`: pure helpers, including `%2B` and per-segment escaping, sanitised breadcrumb labels with real paths, zero-byte percent, and that the C# "connection lost" text equals uploader.js's INTERRUPTED constant.
- TDD: the 13 brief-specified and extra tests were written first and failed 13/13 (404 on `/files/...`, and missing script and library), then passed.

## Runtime checks (Python Playwright, headless Chromium, app on :5397, Development, Auth:Provider=Dev, Storage:UseFake=true, in-memory stores)

The final full run was against a clean instance: **43/43 passed** (`D:\tmp\t10\verify.py`). Measured, unless marked visual:

- **customer-full:**
  - Upload, New folder, Rename and Delete are all offered. Order is `Archive, Documents, Reports, Welcome.txt`.
  - Created folder `Drop #1`. Typing `bad:name` kept the dialog open with "Names cannot contain ':'."
  - Uploaded a 20 MB random file (about 2 s; size shows "20.0 MB"), a 0-byte file ("0 B"), and a file named `Q3 #1 50%+tax & 'café'.txt`.
  - **Interrupt and resume:**
    - A Playwright route aborted (`internetdisconnected`) every chunk PUT with index ≥ 2 for a 20 MB file (5 pieces).
    - The status went through "Connection lost. Retrying…" for about 40 s (the backoff: 1+2+4+8+16 s plus request time), then to `interrupted` with a Resume button.
    - With the route removed, re-selecting the same file reused the same panel entry, and **only pieces [2, 3, 4] were sent**. The file completed and was listed.
  - Re-uploading an existing name opened ConfirmDialog with the text "zero.bin already exists. Replace it?". Confirm completed the overwrite.
  - Rename `zero.bin` → `zero-renamed.bin`. Move `big.bin` → `Drop #1/big.bin`.
  - Opening `Drop #1` gave the URL `.../Drop%20%231`, and its breadcrumb read "Files › Demo share (Full) › Drop #1".
  - The Files nav link was active inside the browser.
  - **Download:**
    - `big.bin` came back with suggested filename `big.bin`, 20,971,520 bytes, byte-identical to the source.
    - The odd-named file downloaded as exactly `Q3 #1 50%+tax & 'café'.txt`, via `?path=Q3%20%231%2050%25%2Btax%20%26%20%27caf%C3%A9%27.txt`.
  - **Delete:**
    - File delete: confirm, then gone.
    - Folder delete: the confirm button stays disabled until `Drop #1` is typed, then enables; the folder is gone.
  - No `core.windows.net` or `sig=` anywhere in the page HTML.
- **customer-read:** the home page shows exactly one card, "Demo share (Read)". In the browser the counts are action-download 1 and zero for upload, new-folder, rename, delete and the upload input.
- **Foreign target:** customer-read opening customer-full's browse URL gets Access Denied in place. The URL is unchanged and no rows render.
- **staff-owner:** groups "Demo Customer (Full)" and "Demo Customer (Read)". Filtering "(Read)" leaves one card.
- **Console and CSP:**
  - A `securitypolicyviolation` listener was injected into every page; no CSP violations and no console errors on any page.
  - The only filtered noise is the dotnet-run hot-reload 404 (`/_framework/dotnet-browser-tools/hot-reload-settings.json`, a dev probe) and the expected LongPolling transport message.
  - Incidental proof the CSP has no unsafe-eval: Playwright's own string `wait_for_function` was blocked by it.
- **Light and dark × 1440 and 375:**
  - Measured: `data-bs-theme` matched the emulated scheme, and `scrollWidth - clientWidth = 0` on the home and browse pages for full and read in all four combinations.
  - **Visual (screenshots, reviewed by eye):** browse full light 1440, home full dark 1440, help light 1440, browse full dark 375 with the conflict dialog, browse read light 375. Readable in both themes.
  - One visual defect found and fixed: at 375 px the page heading was squeezed beside the action buttons. `.browse__heading` now has `flex: 1 1 280px`, so the actions wrap below; re-checked visually.
- **Load more** (separate script): as customer-contribute, uploaded 205 files into a new folder (all done, 0 failed, about 132 s). Refresh showed 200 rows and a `load-more` button; clicking it showed 205 rows and the button disappeared.
  - Contribute shows no delete buttons.
  - The browser logged a series of 429s while the 205 files ran. That is the server's per-user `transfers` bucket (120/min) answering Begin and Complete; uploader.js waited Retry-After and every file still completed. That is the busy path working, but see Concerns.
- **Server log:** no exceptions; only the dev "Failed to determine the https port" warning.
- Every app process I started was stopped. Port 5397 was confirmed closed at the end.

## Defects found and fixed during runtime testing

1. **Re-selecting a file created a second panel entry, and `detach` never matched.**
   - Cause: Blazor passes JS a new wrapper object for the same `DotNetObjectReference` on every interop call, so the `u.ref === ref` identity comparisons never matched.
   - Fix: UploadPanel now passes a stable per-instance `ownerKey` (a GUID string). uploader.js keys `sameFile` and `detach` on it, and refreshes `ref` on re-select.
   - Without this, leaving the page would not have stopped in-flight uploads, which would have kept calling back into a disposed component.
2. While retrying, the status read "Retrying: The connection dropped. Select Resume…", which told the user to act while the script was still working. It now reads "Connection lost. Retrying…". A test pins the C# constant to the JS text.
3. The success alert from the previous action stayed visible during the next one. RunAsync now clears the alert when an action starts.
4. `CleanTypedPath` originally trimmed each segment, which would silently rename a legitimate leading- or trailing-space name. It now only drops empty or blank segments.

## Self-review

- Every `@page` carries `[Authorize]`. Nothing navigates after an await. The only NavigationManager use is `ToBaseRelativePath` in the NavigationLock and FilesNavLink.
- No storage URL, SAS or key reaches the browser. Downloads stay on the Task 8 endpoint, which always sends an attachment.
- Classes: every BEM element and modifier, including those built in `@code` (`upload-panel__fill--done`, and the others), has a rule; the guards pass. `is-dragover` is a JS-toggled state class with a scoped rule.
- Line endings are preserved: CRLF files edited in CRLF (NavMenu, Icon, App.razor); new files are LF like Index.razor and the recent test files.

## Concerns / for the controller

1. **Callback signatures differ from the brief.**
   - The brief: `OnProgress(fileName, sent, total)`, `OnFileDone(fileName, ok, message)`, `OnConflict(fileName)`.
   - What shipped: each callback takes a leading `id` (the upload id), so two files with the same name in different folders can't be confused.
   - Added: `OnStatus(id, status, message)`, which drives the pause, retry, interrupted and queued states, and `OnDenied(id)`.
   - `start(...)` takes an extra `ownerKey` argument (see defect 1).
   - Task 12's web tests should use the data-testids, not these JS names.
2. **Rate limits versus many files.** A multi-file drop of more than about 60 small files hits the 120/min `transfers` bucket (Begin and Complete each cost one). uploader.js handles 429 correctly, so files still finish, but slowly and with console noise. The controller may want Begin/Complete on a looser bucket in a later task. I did not change server limits.
3. **Audit source IP from the circuit.** `PageAuditContext` reads `IHttpContextAccessor` the first time it is used in the circuit. Under InteractiveServer the HttpContext there is the SignalR or long-poll request's, which is the same client behind the gateway (X-Forwarded-For honoured). It is a documented-unsupported pattern in Blazor, but it degrades to `""`, never to someone else's IP. The security auditor may want to look.
4. **Idle sign-out during very long uploads.** session-guard.js signs out after 60 min with no mouse or keyboard input, so a multi-hour upload with no user activity could be cut off. Re-selecting the file after signing back in resumes it, which is mitigated by design. I did not change the suite-shared script.
5. **Uploads queue one file at a time**, each with `parallelChunks` (4) pieces in flight. This is deliberate, to stay under the per-user limits.
6. Help page support contact is generic ("your usual support channel"). There is no Advanced support address or URL in the repo to cite; Dan may want a real one.
7. security-auditor was not run (R5).

## Files

New:
- `Pages/Files/Browse.razor`, `Pages/Files/Browse.razor.css`, `Pages/Help.razor`, `Pages/Help.razor.css`, `Pages/Index.razor.css`
- `Shared/UploadPanel.razor`, `Shared/UploadPanel.razor.css`, `Shared/ConfirmDialog.razor`, `Shared/NameDialog.razor`, `Shared/FilesNavLink.cs`
- `Services/Files/FileDisplay.cs`, `Services/Files/PageAuditContext.cs`, `Services/Files/UploadItem.cs`
- `wwwroot/js/uploader.js`, `wwwroot/lib/spark-md5/{spark-md5.min.js, LICENSE, README.txt}`
- `CloudSelfService.Tests/{UploaderScriptTests, BrowseRouteTests, FileDisplayTests, UploadItemTests}.cs`

Modified: `Pages/Index.razor`, `Shared/NavMenu.razor`, `Shared/Icon.razor`, `App.razor`, `Program.cs`, `wwwroot/css/app.css`, `wwwroot/js/cloudselfservice.js`, `CHANGELOG.md`.

Verification scripts (not committed, outside the repo): `D:\tmp\t10\verify.py`, `D:\tmp\t10\loadmore.py`; screenshots are in `D:\tmp\t10\shots\`.

---

## Fix round 1 (commit 7516de9)

**Tests:** 869 passed / 10 skipped / 0 failed. Build: 0 warnings, 0 errors.

### IMPORTANT: audit SourceIp in the circuit (R38)

**Chose PersistentComponentState** (the first option), using .NET 10's declarative form:
- `PageAuditContext` is a scoped service whose `SourceIp` carries `[PersistentState]`.
- `Program.cs` registers it with `.RegisterPersistentService<PageAuditContext>(RenderMode.InteractiveServer)`.
- `App.razor`, which has the cascading HttpContext during the page request, calls `PageAudit.Capture(HttpContext)` in `OnInitialized`.
- Blazor persists the value into the prerendered page and restores it into the circuit's scoped instance. On Blazor Server that state is data-protected, so a client cannot forge it.
- `IHttpContextAccessor` was removed.
- `Next()` returns `"unknown"` when no IP was captured (for example a circuit started without a prerender), and `Guid.NewGuid().ToString("N")` as the correlation id for every action.

**Runtime check:**
- I temporarily added an Information log of every audit row in `AuditService` and ran the app as customer-full.
- Observed rows:
  - `CreateFolder Success audit-probe ip=::1 corr=6761f9b1…`
  - `Delete Success audit-probe ip=::1 corr=cfb48a21…`
  - `Move Success padtmp ->  padded ip=::1 …`
  - `Delete Success  padded ip=::1 …`
- Every page action now carries the client IP (`::1` locally) and a distinct correlation id.
- The temporary log was removed (`git checkout`) before committing.

**Unit tests (`PageAuditContextTests`):**
- persisted IP present → used; absent or blank → `"unknown"`
- distinct 32-hex correlation ids
- `Capture` reads RemoteIpAddress; null context or missing address leaves it unset
- the property carries `[PersistentState]`
- Program.cs and App.razor wiring is pinned

### Minors

1. Browse `OnFilesChosenAsync`, the `attachDropZone` call, UploadPanel `CallAsync` and `OnBeforeNavigationAsync` now also catch `JSException` and `TaskCanceledException`. They show the inline warning "The upload component failed to load. Refresh the page and try again." (UploadPanel raises `OnScriptFailed` to the page). A failing leave-page confirm is logged and lets the navigation through.
2. ConfirmDialog matches exactly, without trimming: `ConfirmDialog.IsConfirmed(required, typed)` is an Ordinal comparison. `ConfirmDialogTests` has 7 cases.
   - Runtime: a folder renamed to `" padded"` stayed disabled when `padded` was typed, enabled with `" padded"`, and deleted.
3. The Denied branch of `LoadAsync` clears `_items` and `_continuation` on reset before re-reading the level.
4. **Antiforgery retry:** `UploadPanel` passes `UploadEndpoints.AntiforgeryMessage` into `csspUploader.start(...)` as its 7th argument. In `api()`, a 400 whose code is `invalid` and whose message equals that text clears `tokenPromise` and retries once.
   - Runtime: the antiforgery cookie was replaced with another session's after the token was cached. The network log showed `400 POST /api/uploads` → `200 GET /api/antiforgery` → `200 POST /api/uploads` → `204 complete`.
5. Index and Browse (both list and action handlers) show "The service is temporarily unavailable." when `CurrentUser` returns null. The spinner stops because `_loaded` and `_loading` resolve.
6. **Chose "reload as many pages as were loaded"** (`ReloadLoadedPagesAsync`, tracked by `_pagesLoaded`).
   - I first tried merging page 1. At runtime it lost the new file whenever it sorted onto a later page (250-file folder, both pages loaded: 250 rows after upload instead of 251), so I replaced it.
   - Runtime: 200 rows → Load more → 250 → upload → **251** rows including the new file. With only page 1 loaded, Load more is still offered after an upload.
7. NameDialog has a `ShownAs` parameter. For a move, when `StoragePath.DisplayName(raw) != raw`, it shows the hint `name-shown-as`: "Currently shown as: rep_ort.txt. This path contains invisible or direction-changing characters, shown as "_"."
   - Runtime: a file seeded as `rep‮ort.txt` (temporary seed in FakeStorageBackend, reverted) showed the hint; `Welcome.txt` showed none.
8. `data-testid="browse-crumb"` is on every breadcrumb anchor (Files, the target when not current, and intermediate folders). `Browse_BreadcrumbAnchors_CarryTestIds` counts 3 at `Reports/2026`.
9. The spark-md5 README.txt now says it is used under the WTFPL, matching the shipped LICENSE, and notes package.json also offers MIT.
10. `cssp.openFilePicker` removed.
11. Tests through the existing TestAppFactory prerender path (no bUnit):
   - `Browse_FolderLargerThanOnePage_OffersLoadMore`: 250 seeded files; page 1 shows `load-more` and exactly 200 rows.
   - `Browse_FolderOfOnePage_HasNoLoadMore`.
   - **Denied → level refresh is feasible:** `Browse_GrantRevoked_ListDeniedRefreshesLevel_RendersAccessDenied` removes the grant after sign-in. ListAsync returns Denied, `RefreshLevelAsync` finds the target unreachable, and Access Denied renders in place.

### Controller rulings

- **R36:** while pieces land, `markActivity()` signals activity at most once a minute (`ACTIVITY_INTERVAL_MS = 60000`) by dispatching a `scroll` event on `document`. session-guard.js already listens for that event; its handler resets the idle timer and pings `/keepalive`. session-guard.js has no public activity hook and is suite-shared, so I didn't change it.
  - Runtime: a 13 MB upload raised 1 activity event and 1 `/keepalive`.
  - A static test pins the interval, the event, and that session-guard.js listens for `scroll`.
- **R37:** Help reads `Support:Contact`, defaulting to "your Advanced account team"; the key was added to appsettings.json. Tests cover both the configured and the default value.

### Runtime checks for changed areas

Python Playwright, :5397, Development, Dev auth, fake storage:
- `round1.py`: **14/14 PASS**. No console errors and no CSP violations. The 250-file folder was seeded through the real upload API, with `Transfers__RequestsPerMinute=2000` set only for this local run.
- Also run: `r36.py` (activity signal) and `r36b.py` (stale-token retry).
- All processes I started were stopped, and port 5397 was confirmed closed.
- Temporary code (the audit log and the bidi seed) was reverted, and neither is in the commit.

### Remaining notes

- `PersistentState` restoration relies on the prerender. With the current render model (InteractiveServer pages prerendered from App.razor), every circuit starts from a prerendered page; a future non-prerendered entry would audit "unknown", as required.
- R36 uses a synthetic DOM event. If session-guard.js ever stops listening for `scroll`, the static test fails.

---

## Security follow-up (R39) (commit 88d4f13, on top of e3ccbbe)

**Tests:** 1005 passed, 11 skipped, 0 failed (the extra skip is the new PG contract test, which skips without `CSSP_TEST_PG`).
- PG integration: 12/12 against a throwaway `cssp-pg-test` container (podman postgres:17-alpine at 172.21.108.241:55432, `--rm`), stopped afterwards. The only other podman command was a read-only `podman ps`. The existing `nexus-devdb` container belongs to another project and was not touched.
- Build: 0 errors and 1 warning. The warning is CS0108 in `Pages/Admin/Staff.razor` (Task 11's code, present at e3ccbbe, not touched here).

1. **MEDIUM: no-store on dotted folders** (`Endpoints/AuthPolicy.ShouldDisableCaching`)
   - Everything under `/files` and `/api` is now always no-store.
   - Other paths are cacheable only under the static prefixes, or when they match an explicit root-asset pattern. The prefixes are `/_framework`, `/_content`, `/css`, `/js`, `/images`, `/fonts`, `/lib`, plus `/Shared` (where the collocated ReconnectModal.razor.js is served). I read the full route list from `staticwebassets.build.endpoints.json`; `/fonts` and `/Shared` were previously only covered by the dot heuristic.
   - The root-asset pattern covers `CloudSelfService[.fp].styles.css`, `CloudSelfService[.fp].modules.json` and `favicon[.fp].png|ico`. Those are the only root-level files actually served, plus favicon.ico for browsers that request it.
   - The dot heuristic is gone, so `/robots.txt` and `/some.page` are no-store too.
   - 14 new InlineData cases cover the dotted folder (`/files/{guid}/2024.01`, `…/q1.final`), fingerprinted styles, the favicons, fonts, lib, Shared, `/api/...` and `/help`.
   - Runtime: `/files/{id}/2024.01` returns `no-cache, no-store, max-age=0`. Styles, favicon, spark-md5 and fonts keep plain `no-cache` (MapStaticAssets' own header).
2. **LOW: audit IP authority**
   - `PageAuditContext` now holds two values: `ConnectionIp` (authoritative) and the `[PersistentState] SourceIp` (fallback), then "unknown". The xmldoc says plainly that the persisted value can be dropped or replayed.
   - **Finding:** as specified, `IHttpContextAccessor` in `CircuitHandler.OnConnectionUpAsync` is **null** in this app. I measured it with a temporary log (`conn=<null>`). Under the app's LongPolling transport, the hub loop outlives the request that started it.
   - Fix: a small `ConnectionIpHubFilter` (an `IHubFilter` registered via `AddSignalR(o => o.AddFilter<…>())`). It exposes `Context.GetHttpContext()` for the connection through an AsyncLocal during each hub invocation. The circuit's start and reconnect run inside such an invocation, where `AuditCircuitHandler.OnConnectionUpAsync` reads it, falling back to the accessor. This is the connection's own request as seen after `UseForwardedHeaders`, and it runs on every connection-up, including reconnects.
   - Runtime (temporary log, removed): `hub=::1 ip=::1` on every connection-up. With the persisted-state comment **stripped** from the prerendered HTML (Playwright route rewrite), the circuit still audited `::1`.
   - Tests cover: connection beats persisted value; dropped state still uses the connection; reconnect updates to the latest; null or missing address keeps the previous; the handler reads the hub context in preference to the accessor; the filter exposes the context only during the invocation; Program.cs wiring is pinned.
3. **spark-md5 under MIT**
   - `ThirdPartyNotices/spark-md5/LICENSE` is the MIT text, copyright (C) 2015 André Cruz. A `README.md` beside it gives the version, the dual-licence note, the source URL, the SHA-256 and the usage.
   - `wwwroot/lib/spark-md5/` now holds only `spark-md5.min.js` (unchanged, same SHA-256). `/lib/spark-md5/LICENSE` returns 404.
   - `ThirdPartyNotices/` was added to `.dockerignore`, since it is documentation and the Web SDK does not publish it.
   - The test asserts the directory contains only the script, and that the notice is MIT with the author.
4. **INFO: first-piece hash**
   - uploader.js hashes piece 0 (up to 4 MiB, via `File.slice`) before Begin and sends `firstPieceMd5`. A 0-byte file sends none, and the cached hash is dropped when the user re-selects a file.
   - Server side:
     - `BeginUploadRequest.FirstPieceMd5` (optional; a malformed value → Invalid `FirstPieceMd5InvalidMessage`).
     - `UploadSession.FirstPieceMd5`, stored in `first_piece_md5 TEXT NULL` via `ALTER TABLE … ADD COLUMN IF NOT EXISTS`, and saved on create.
     - In `BeginAsync`, a resumable session whose stored hash differs from the declared one is not resumed. The existing replace path aborts it, removes its staging, and writes the "Replaced by a new upload of the same file" audit row before creating a fresh session.
     - `SameFirstPiece(stored, declared)` refuses only two known, different hashes, so older clients and sessions resume as before.
   - Tests: resume on the same hash; a different hash → fresh session, old one Aborted, Replaced row, new hash stored; no hash still resumes; malformed → Invalid; a 5-case Theory on `SameFirstPiece`; and an InMemory plus PG store round-trip contract test.
   - Runtime: file A (20 MB) was interrupted after pieces 0–1. Choosing a different B with the same name and size gave Begin `received: []`, sent pieces [0,1,2,3,4], and the downloaded result is byte-identical to B.
5. **Browse, invalid path after a target change:** clears `_target`, `_level` and `_denied` (and `_pagesLoaded`), so the previous target's header and buttons don't linger.
6. **uploader.js:** Retry-After is capped at 300 s (`MAX_RETRY_AFTER_SECONDS`).

**Playwright (`D:\tmp\t10\r39.py`, :5397, final build): 13/13 PASS.**
- Dotted-folder no-store, the static-asset cache rules, and the notices not being served.
- Upload plus resume still sends only pieces [2,3,4], and Begin carries a 24-character `firstPieceMd5`.
- A different same-name, same-size file starts fresh and lands intact.
- Console errors were only the simulated `ERR_INTERNET_DISCONNECTED` aborts and the dev hot-reload 404.
- Every app process I started was stopped, and port 5397 was confirmed closed. Temporary logging was removed before committing.

**Concerns**
- The IP capture depends on a hub filter. If a future .NET runs the circuit's connection-up outside a hub invocation, `Current` is null; the handler then falls back to the accessor, then to the persisted value. The precedence tests pin this order but not the framework behaviour; only the runtime check above shows it today.
- The Staff.razor CS0108 warning from Task 11 is still in the build.
