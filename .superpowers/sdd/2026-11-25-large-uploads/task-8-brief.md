### Task 8: Docs, changelog, help text and web tests

**Files:**
- Modify: `Pages/Help.razor`, `Shared/UploadPanel.razor` (header comment), `docs/operations.md`, `CHANGELOG.md`, `webapp-tests/test_cloudselfservice.py`, `CLAUDE.md` (Configuration table row for `Transfers:*` and Architecture: `Pages/Admin/Settings`)

- [ ] **Step 1: Help** (`data-testid="help-uploads"` list):
  - Replace the 24-hour bullet with: "An unfinished upload is kept for 24 hours after its last piece arrived, and at most 7 days. If you are signed out part-way, sign in again, open the same folder and select the same file to carry on."
  - Add: "The largest file you can upload is shown above the upload list. For a database backup larger than that, ask Advanced support, or split the backup into several files."
- [ ] **Step 2: `docs/operations.md`** Transfers table:
  - `MaxUploadBytes` default 107374182400 (100 GiB), "the default limit; Admins can override it in Admin → Settings".
  - New `MaxUploadBytesCeiling` 1099511627776 (1 TiB), "the most an Admin can set; capped at 1,562.5 GiB, the Azure block-blob maximum at 32 MiB pieces".
  - `SessionHours` 24, "an unfinished upload expires this long after its last piece".
  - New `MaxSessionDays` 7, "and never later than this after it started".
  - New `MaxInFlightPieceBytes` / `MaxInFlightPieceBytesPerUser`.
  - `ParallelChunks` wording: "pieces in flight per upload for 4 MiB pieces; larger pieces use 2".
  - Add a short "Large uploads" operator note: piece sizes by file size; about four uploads over 390 GB at once per instance before others wait; the WAF exemption must allow 32 MiB bodies on `/api/uploads/*`; and the striping fallback (SQL Server `BACKUP ... TO DISK = 'a.bak', DISK = 'b.bak'`) for anything over the ceiling.
- [ ] **Step 3: `CHANGELOG.md`**: new top entry `## [1.1.0] - TBD`:
  - 🔄 Changed: "The default upload limit is now 100 GB."; "An unfinished upload is now kept for 24 hours after its last piece arrives (up to 7 days), instead of 24 hours after it started."; "Being signed out during an upload now pauses it instead of failing it."
  - ✨ Added: "Files larger than 195 GB can now be uploaded: large files are sent in bigger pieces, up to about 1.5 TB for blob containers."; "Admins and Owners can change the upload limit in Admin, Settings, permanently or until a set time. Every change is recorded in the audit log."; "The Files home lists your unfinished uploads, and the file browser shows each one as waiting for its file so you can carry on after signing in again."; "The upload panel shows the largest file you can upload, and warns before your sign-in ends while uploads are running."
  - No em-dashes, no customer names.
- [ ] **Step 4: Web tests** (`webapp-tests/test_cloudselfservice.py`, `data-testid` only):
  - `test_staff_admin_sets_and_reverts_upload_limit`: persona `staff-admin`; open `/admin/settings`; set 150 GB, Permanent, Save → `upload-limit-effective` contains "150"; Revert → contains "100".
  - `test_staff_cannot_open_settings`: persona `staff` → Access Denied content.
  - `test_upload_panel_shows_limit`: persona `customer-full`, open a share → `upload-limit-text` contains "100".
- [ ] **Step 5: CLAUDE.md** updates: the Architecture `Pages/` list gains `Admin/Settings`; the Configuration table row for `Transfers:*` mentions the limit ceiling, session days and piece-memory budget.
- [ ] **Step 6: Verify:** `dotnet build` (0 errors), the full test project (0 failures), then the web tests against the app in Development (`Auth__Provider=Dev`, `Storage__UseFake=true`). Expected: all pass.
- [ ] **Step 7: Commit** `Document large uploads, the upload limit setting and resuming after sign-in`.

### After all tasks

- Dispatch `security-auditor` (the new admin route, the upload endpoint body cap and budget, the `AdminService` gate, `App.razor` session-end attribute) and `unit-test-reviewer` (ChunkMath, `PieceMemoryBudget`, `UploadLimitService`, sliding expiry, `ListUnfinishedAsync`). Fix what they confirm.
- Before release: in the test environment, upload a file over 195 GB (or at least one above 50,000 × 4 MiB to a Blob target) through the App Gateway to confirm 8 MiB+ piece bodies pass the WAF exemption.

