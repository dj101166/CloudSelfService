### Task 10: Files home and browser pages

**Files:**
- Create: `Pages/Files/Browse.razor` (+ `.razor.css`), `Shared/UploadPanel.razor` (+ `.razor.css`), `Shared/ConfirmDialog.razor`, `Shared/NameDialog.razor`, `wwwroot/js/uploader.js`, `Pages/Help.razor`
- Modify: `Pages/Index.razor` (+ `.razor.css`), `Shared/NavMenu.razor` (Files, Help), `Shared/Icon.razor` (add icons), `App.razor` (load `uploader.js`)
- Test: `UploaderScriptTests.cs` (static), `BrowseRouteTests.cs`

**Interfaces:**
- Consumes: `CurrentUser`, `StorageAccessService` (list, create folder, move, delete, reachable targets, level), and the Task 8/9 endpoints.
- Produces: `window.csspUploader.start(dotnetRef, targetId, folder, inputElementId, overwrite)`. It calls back with `[JSInvokable] OnProgress(string fileName, long sent, long total)`, `OnFileDone(string fileName, bool ok, string? message)` and `OnConflict(string fileName)`.

**Behaviour:**
- **`/` (Files home).**
  - Customers: one card per reachable target, showing display name, kind badge (`File share` / `Blob container`) and level badge (`Read` / `Contribute` / `Full`).
  - Staff: the same cards grouped under customer headings, with internal targets under "Advanced internal". A filter box narrows by customer or target name.
  - No reachable targets: the empty state reads "No storage has been shared with you yet. Contact Advanced support if you expected to see something here."
- **`/files/{TargetId:guid}/{*Path}`.**
  - **Layout:** breadcrumb (target name › folders); a table with name, size (humanised), modified (local time), and row actions; folders first, then files, alphabetical.
  - **Load more:** appears when `ContinuationToken` is non-null (Review Focus 5).
  - **Download:** a plain `<a href="/api/files/{id}/download?path={Uri.EscapeDataString(path)}" download>` link, which bypasses the circuit.
  - **Actions shown by level:**
    - Read: download.
    - Contribute: adds Upload (button and drag-and-drop zone), New folder, Rename/Move (the `NameDialog` takes a new relative path; staying in the same folder is a rename).
    - Full: adds Delete, which needs `ConfirmDialog` and, for a folder, typing its name.
  - **Results:** every action goes through `StorageAccessService`. Results show in an inline `.alert` with the message; `Unavailable` gets the alert in `--warning` styling. Any `Denied` result means the page's cached level was stale, so it re-fetches the level and refreshes the action set.
  - **Unreachable targets:** a target the user can't reach renders `AccessDeniedContent` in place. Don't navigate (this is the recipe trap).
  - **Uploads:** `UploadPanel` lists each file with progress, pause, resume and cancel.
  - **Overwrite:** `OnConflict` opens `ConfirmDialog` ("{name} already exists. Replace it?"). On confirm it restarts that file with `overwrite = true`.
- **`uploader.js`.**
  - Gets the antiforgery token from `/api/antiforgery`, then for each selected file calls `POST /api/uploads`.
  - Skips chunk indices already in `received`, and sends the rest with at most `parallelChunks` in flight.
  - Computes MD5 for each chunk. WebCrypto has no MD5, so vendor `spark-md5` 3.0.2 under `wwwroot/lib/spark-md5/` with its licence, read from `File.slice`.
  - Retries a chunk up to 5 times with exponential backoff (1, 2, 4, 8, 16 s) on network failure or 5xx/409-Conflict. A 403 or 404 stops that file.
  - Calls `complete` once all chunks are sent. A 409 from `POST /api/uploads` calls `OnConflict`.
  - Reports progress through `dotnetRef.invokeMethodAsync('OnProgress', …)`, throttled to 4 calls per second.
  - Uses no inline event handlers and no `eval`, so it is CSP-safe.
- **`/help`.** Shown to everyone. Sections:
  - Signing in: guest invites, redeem the invitation email, use your organisation's work account.
  - What Read, Contribute and Full allow (the table from spec §5.3).
  - Large uploads: resume after a dropped connection by selecting the same file again.
  - Contacting Advanced support.

- [ ] **Step 1: Write the failing tests.**
  - `UploaderScriptTests` reads `wwwroot/js/uploader.js` from disk and asserts it:
    - contains `RequestVerificationToken`
    - contains `Content-MD5`
    - contains no `eval(` and no `innerHTML =`
    - contains the retry delays `[1000, 2000, 4000, 8000, 16000]`
  - `BrowseRouteTests` (via `TestAppFactory`):
    - `Browse_OtherCustomersTarget_RendersAccessDenied_Not500`
    - `Home_CustomerRead_ShowsOnlyOwnTarget`
- [ ] **Step 2: Run them and confirm they FAIL.**
- [ ] **Step 3: Implement the pages, components and script.** Re-run the static guards from Task 2. Every new class needs a rule, and every icon needs a switch case.
- [ ] **Step 4: Run them and confirm they PASS.**
- [ ] **Step 5: Verify in the running app** with `Auth:Provider=Dev`, in-memory stores and `FakeStorageBackend`. Register the fake backend when `Storage:UseFake=true` in Development only, and guard it the same way as DevSignIn.
  - Sign in as `customer-full`: browse, create a folder, upload a 20 MB file, then a 0-byte file.
  - Kill the network mid-upload in devtools, restore it, and re-select the file. It should resume.
  - Rename, move, download (checking the filename), and delete.
  - Sign in as `customer-read`: only download is offered.
  - Check light and dark at 1440 px and 375 px. Expect no console errors and no CSP violations.
- [ ] **Step 6: Commit:** "Add Files home, file browser and resumable upload UI".

---

