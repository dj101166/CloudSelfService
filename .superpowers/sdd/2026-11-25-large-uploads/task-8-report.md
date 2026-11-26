# Task 8 report

Commit 60ced03 "Document large uploads, the upload limit setting and resuming after sign-in".

## Changed
- CHANGELOG.md: new `## [1.1.0] - TBD` with Changed and Added entries (brief copy verbatim).
- Pages/Help.razor: replaced the 24-hour bullet; added the largest-file bullet (help-uploads structure kept).
- docs/operations.md: Transfers table updated (MaxUploadBytes, new MaxUploadBytesCeiling, ParallelChunks, SessionHours, new MaxSessionDays, new MaxInFlightPieceBytes / PerUser); new "Large uploads" operator note; WAF note now says 32 MiB; abandoned-uploads note no longer says "up to 24 hours" flat. No remaining 50 GiB / 195.3 GiB citations in docs outside historical specs/plans.
- CLAUDE.md: Pages list gains Admin/Settings; Transfers:* row updated.
- UploadPanel.razor header comment was already accurate (Task 7); left unchanged.
- webapp-tests: three new tests (settings denied for staff, admin set/revert, upload-limit-text). CRLF kept.
- CloudSelfService.Tests/SmokeTests.cs: hardcoded `"1.0.0"` -> `"1.1.0"` (test follows the top changelog version; the new entry broke it).

## Tests
- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj`: Failed 0, Passed 1326, Skipped 24 (PG/live).
- Web (app on port 5310, Dev auth + fake storage): `pytest test_cloudselfservice.py` 11 passed (run twice more after the final fix, both green).
- App stopped by pid; port 5310 closed. 8 chrome-named processes remain; not started by me as far as I can tell (Playwright closes its own contexts), so left alone.

## Concerns / notes
- Settings page: a Save clicked before the Blazor circuit connects is silently lost (server-rendered form, no interactivity yet). The admin test therefore retries the save in a loop. Real users are unlikely to hit this, but it is a minor UX wart.
- Settings page shows ceiling 1024 GB in Dev (1 TiB default), consistent with config.
- webapp-tests/__pycache__ was removed; untracked memory files and .superpowers left untouched.

## Fix round 1

- SmokeTests.cs: line endings restored to CRLF (the first commit had converted it to LF); versus 3972ac2 the diff is one line. The literal "1.1.0" is replaced by `Assert.NotEqual("dev", svc.Version)`.
- Settings.razor (R10): inputs, radios, note, Save and Revert render `disabled` until `OnAfterRenderAsync(firstRender)` sets `_ready` and calls StateHasChanged. Web test now waits for Save to be enabled, then fills, saves once and asserts; the retry loop is gone.
- New .NET test `AdminEndpointTests.Settings_Prerender_RendersSaveAndRevertDisabled_UntilInteractive` (prerendered HTML has Save and the limit input disabled). Revert is not rendered in the prerender (no override), so it is not asserted there.
- docs/operations.md and the design doc (R11): 16 MiB pieces hold 32 MiB per upload, about eight at once; 32 MiB pieces hold 64 MiB, about four.
- Other admin pages: Customers, CustomerDetail, Targets, Staff and AuditLog use the same prerender-then-circuit pattern with `@onclick`/`@onsubmit` buttons, so a very fast click could be lost there too; not changed, as instructed.

Results: `dotnet build` 0 warnings, 0 errors; `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` Failed 0, Passed 1327, Skipped 24. Web (port 5310): `-k "upload_limit or settings"` 2 passed; full suite 11 passed, twice. App stopped by pid, port closed.
