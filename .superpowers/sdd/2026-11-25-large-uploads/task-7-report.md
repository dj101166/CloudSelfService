# Task 7 report: Resume after sign-in in the file browser; pause instead of fail on expiry

Status: DONE. Commit `3972ac2 Resume unfinished uploads after signing in again` (branch `feat/large-uploads`, parent 5c3bc0a).

## What I found in the inherited diff

The previous implementer's uncommitted work covered the whole brief. I checked it item by item:

| Brief item | State found |
|---|---|
| `expireAll()` pauses: status `'signed-out'`, `SIGNED_OUT` text verbatim, `notify(...'OnProgress'...)` via `flushNow`, no `finish(`, no DELETE, aborts in-flight requests | Done. Old `SESSION_EXPIRED` text removed. `classify` and `getToken` now throw `expired` carrying `SIGNED_OUT`. `tokenPromise` is reset so a later sign-in fetches a fresh token |
| `UploadStatusNames.SignedOut = "signed-out"`, `CanResume == false`, `IsFinished == false`; row shows message + **Sign in again** link to `/login` | Done (`data-testid="upload-sign-in"`) |
| `UploadPanel.Unfinished` parameter; unmatched rows render `waiting-file`, "Waiting for the file: {percent}% received", Select file `<label for>` bound to a per-row hidden `<input type=file>` with no `accept` | Done. Matching uses target + folder + name + size. A `_claimed` set keeps a resumed row from coming back after "Clear finished" |
| Stored `Overwrite` passed to `csspUploader.start`; "That is a different file; it was started as a new upload." when Begin's session id differs | Done. uploader.js reports `OnSession(id, sessionId)` after Begin, and the panel compares it to the row's id. The stored overwrite applies only to a file with the row's name (`overwriteOnlyFor`), so a differently named pick cannot silently replace another file |
| Browse calls `ListUnfinishedAsync(user.Id, TargetId, _folder)` after loading; failure logs and passes an empty list | Done (also on Refresh). Contributors only |
| Limit text "Files up to {FormatGb(Effective)} GB" (`upload-limit-text`), `GetStateAsync` read once on load | Done (in `OnInitializedAsync`). Shown in the panel heading, or on its own line when the panel is empty. Contributors only |
| `data-session-ends` on `<body>` = IssuedAt + `Auth:AbsoluteSessionHours`, authenticated only; uploader.js reads it once; `OnSessionEnding` once per upload within 15 min; banner `upload-session-ending` in local time | Done. **The `<body>` element carries it.** The value is null for an anonymous user, and Blazor then omits the attribute. The banner is set once per page (`_sessionEndsAt` is assigned once), shows only while an upload is active, and is rendered in the viewer's browser zone (`Zone` from Browse, UTC labelled if not yet known) |
| UploadPanel header comment no longer says "for 24 hours" | Done: "24 hours after its last piece arrived, and at most 7 days" |
| Tests from Step 1 | All present: `Uploader_ExpiryPausesInsteadOfFailing`, `Uploader_FingerprintIsFirstFourMiB`, `Uploader_ReadsSessionEnd`, `SignedOut_IsNotFinishedAndNotResumable`, `WaitingForFile_IsNotActive`, `AppShellContractTests.App_SessionEndsAttribute_OnlyForAuthenticatedUser`. Extra tests were added for rendering (Browse route tests), message parity with uploader.js, `WaitingRows`/`IsFor`, the banner text, and signed-in/anonymous body rendering |

Checks I made against the server code: `OnSession`'s comment says Begin replaces every open session for the same name, and `UploadSessionService.BeginAsync` (lines ~247-256) does abort all open sessions for the user and path when it does not resume, so that holds. `ListUnfinishedAsync` uses `StoragePath.Parent`, the same folder form Browse's `_folder` uses.

## What I changed

- `BrowseRouteTests.Browse_UnfinishedUploadInThisFolder_ShowsWaitingForFileRow` now also asserts that the per-row file input has no `accept` attribute (a binding requirement that wasn't covered).
- Nothing else. The inherited code met the brief, built clean, and passed.

## Tests and results

- `dotnet build`: 0 warnings, 0 errors.
- Focused run (UploaderScript, UploadItem, AppShellContract, BrowseRoute tests): 65 passed.
- Full `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj`, run twice (before and after the test edit, the second just before committing): **Passed 1326, Failed 0, Skipped 24** (the skips are PostgreSQL and live-storage tests with no env vars).

## Manual check (Development, Auth__Provider=Dev, Storage__UseFake=true, port 5310)

One app instance and one Playwright browser at a time. 40 MB random files (10 pieces of 4 MiB), with chunk PUTs delayed 1.5 to 2 s by a Playwright route so the upload is still running at sign-out. Sign-out is `/logout` in a second tab of the same browser context.

1. Signed in as `customer-full`. `<body data-session-ends>` was 1795687134, sign-in time + 43,195 s (12 h). Opened Demo share (Full): "Files up to 100 GB" shown.
2. Started `r31.bin`. At 16.0 MB of 40.0 MB, signed out in the other tab (it landed on `/signed-out`). Within about 0.3 s the row turned `data-status="signed-out"` and showed "You were signed out. Sign in again, then select the same file to carry on where it stopped." with a **Sign in again** link (`href="/login"`), still at 16.0 MB of 40.0 MB. Only the Cancel button was offered (no Resume). Network: Begin, 4 chunk PUTs (204), then nothing: **no DELETE, no Complete**.
3. Signed in again. The Files home's `unfinished-uploads` card listed "r31.bin, Demo share (Full), 40% received, 2026-11-25 17:09, Discarded after 2026-11-26 17:09" with Open folder and Discard.
4. Open folder: the panel showed a `waiting-file` row: "r31.bin 16.0 MB of 40.0 MB, Select file, Waiting for the file: 40% received". The control is a `LABEL for=upload-resume-<32 hex>`, and its input has no `accept`. **A real click on the label opened the native file chooser** (Playwright filechooser modal). Chose the same file: it finished ("Uploaded", 40.0 MB of 40.0 MB) and r31.bin appeared in the folder listing. No different-file notice.
5. Resume evidence with network detail (y.bin, same flow): after signing in again, picking the same file on its waiting row made Begin return **the row's own session id 36c8661b… with `received=[0,1,2,3]`**. Only pieces 4 to 9 were then PUT, followed by Complete. The progress went straight from 36 MB to done. No notice.
6. Different file: paused `x.bin` (from folder a) at 40%, signed in again, and on its waiting row chose a different `x.bin` (folder b, same name and size, different bytes). Begin returned a new session (adbb4c67…, `received=[]`, row was b198b95f…). The panel showed **"That is a different file; it was started as a new upload."**, and the upload completed.
7. Banner: restarted the app with `Auth__AbsoluteSessionHours=0.2` so the end falls inside 15 minutes. Starting an upload showed exactly one `upload-session-ending` banner: "Your sign-in ends at 17:24. Uploads still running then will pause; sign in again and select the same files to carry on." The browser zone was America/Toronto, and the attribute's local time was 5:24:55 PM, which matches. The banner disappeared when the upload finished.
8. Anonymous: after `/logout`, both `/signed-out` and `/login` had no `data-session-ends` on `<body>`.

Afterwards: browser closed (`browser_close`), app stopped (`Stop-Process`), no CloudSelfService process left, and port 5310 is free. `.playwright-mcp/` and the temporary test files are deleted.

## Files changed (commit 3972ac2)

- `App.razor`
- `Pages/Files/Browse.razor`
- `Services/Files/UploadItem.cs`
- `Shared/UploadPanel.razor`
- `Shared/UploadPanel.razor.css`
- `wwwroot/js/uploader.js`
- `CloudSelfService.Tests/AppShellContractTests.cs` (new)
- `CloudSelfService.Tests/BrowseRouteTests.cs`
- `CloudSelfService.Tests/UploadItemTests.cs`
- `CloudSelfService.Tests/UploaderScriptTests.cs`

Files were staged explicitly. Line endings are preserved: App.razor CRLF as before, all others LF as before; I verified this by counting CR bytes in the parent and the commit. No Co-Authored-By trailer. Not committed: `.claude/memory/*`, `.superpowers/`.

## Self-review findings

- Every security rule holds. There is no new storage path, and the waiting rows come only from `ListUnfinishedAsync`, which filters by the caller's user id and current access level. No storage URL reaches the browser. The new `data-session-ends` value is the caller's own sign-in end, nothing secret. No new routes, and no inline handlers or eval (CSP stays intact: the label/input picker needs no script, and Blazor `@onchange` is not an inline handler).
- `expireAll` pauses every active upload, not just the one that hit the expiry. That is intended, because the whole sign-in is gone.
- `checkSessionEnd` runs when an upload is enqueued and every 30 s, but only if the attribute is present.
- Comments: a few longer explanatory comments (the uploader `start` overwrite scoping, the `OnSession` claim logic) state a non-obvious WHY and are kept.
- The changelog entry and the Help text are deliberately left to Task 8, as the plan says.

## Concerns

1. **Unexplained first observation, not reproduced.** On the very first manual run, the upload row read `signed-out` (24 MB of 40 MB) *before* I triggered `/logout`. The first app log also has one "Session rejected: user record not found." line at the first request, which suggests the reused Playwright profile carried a stale `cssp-dev` cookie from the previous implementer's app instance. I tried to reproduce it six times, including on a fresh app instance and with a 66 s wait between sign-in and upload, and every upload either finished or paused only after the real sign-out. I believe it was an artefact of the stale browser state from the crashed session, but I could not prove the cause.
2. The security-auditor and unit-test-reviewer runs are scheduled in the plan's "After all tasks" step (they include the `App.razor` session-end attribute). I was told not to dispatch subagents, so they have not run for this task.
3. The banner time is formatted `HH:mm` (24-hour, invariant) in the viewer's zone. That matches how the app formats times elsewhere, but it isn't a locale-specific 12-hour format.
