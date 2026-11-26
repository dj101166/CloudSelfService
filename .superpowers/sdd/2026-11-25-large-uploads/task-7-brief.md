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

