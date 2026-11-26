# Task 4 report: Effective upload limit, override store, Settings admin page

Status: DONE. Commit `57f95e5` "Add an audited, expiring upload limit that Admins and Owners can change" (base f35c2c1). No Co-Authored-By trailer. `.claude/memory/` left unstaged.

## What was implemented

- `Services/Data/SettingsStore.cs`: `UploadLimitOverride` record, `IUploadLimitStore`, `InMemoryUploadLimitStore` (volatile field), `PgUploadLimitStore` (upsert of row 1, delete of row 1).
- `PgsqlContext.SchemaSql`: the `upload_limit_override` table, using the brief's columns and checks (formatted multi-line like the rest of the schema).
- `Services/Transfers/UploadLimitService.cs` (singleton):
  - `LimitsFrom` (ceiling = min(config ?? 1 TiB, `ChunkMath.MaxBlobBytes`); default = min(config ?? 100 GiB, ceiling); negatives clamp to 0 as before).
  - `GetStateAsync`, with a 30 s `TimeProvider` cache. A clock moving backwards also refreshes. A generation counter stops a read that raced an `Invalidate()` from being cached.
  - `Invalidate`, `FormatGb`, and the constants.
  - Page helpers: `Describe` (source text), `LocalToUtc` / `UtcToLocal` (browser-zone conversion, UTC when the zone is unknown), and `BytesFromGb` (saturates so huge entries are refused as out of range instead of overflowing).
  - `SetOverrideAsync` / `ClearOverrideAsync` pass through to the store, so `AdminService` does not need a second dependency.
- `UploadSessionService`:
  - The ctor follows R1: (access, sessions, backends, audit, time, config, budget, limits, logger).
  - `MaxUploadBytesFrom`, `DefaultMaxUploadBytes` and `_maxBytes` are removed.
  - `TooLargeMessage` uses `FormatGb`.
  - Begin follows R2. Input validation runs first, then Upload authorisation, then FindResumable plus the first-piece match. Only when no session is resumable does it read `GetStateAsync().Effective` once and apply the size check and per-user caps. R3 holds: `PieceSizeFor` only runs after the size has been accepted.
- `AdminService`:
  - Adds `SetUploadLimitOp = "Admin.SetUploadLimit"` to `AuditOperationNames`, and the ctor gains `UploadLimitService limits` (placed before the optional `cooldown` and `logger`).
  - `SetUploadLimitAsync` / `ClearUploadLimitAsync` use the `RequireAsync(StaffAdmin)` gate and the brief's validation messages verbatim.
  - The audit paths are `limit {old} GB -> {new} GB; until {yyyy-MM-ddTHH:mm:ssZ|permanent}; note: {note}` and `limit {old} GB -> default {default} GB`.
  - `Bytes` holds the new value; on clear it holds the default.
  - The cache is invalidated before reading "old", so the audit row names the true value being replaced, and again after the write. `RecordAsync` gained an optional `bytes`.
- `Program.cs`: both stores are registered in both branches, plus a singleton `UploadLimitService`. There are two startup warnings: the brief's ceiling text verbatim, and the default warning, now citing `Transfers:MaxUploadBytesCeiling`.
- `Pages/Admin/Settings.razor` (+ `.razor.css`, tokens only):
  - Follows the Targets.razor pattern: `AdminGate`, `AdminAlertBox`, `LoadGuardedAsync` / `ActAsync` (which re-check `StillAuthorizedAsync` and catch store failures).
  - Shows three stat tiles (current, default, ceiling) and the form, with every testid the brief lists, plus `upload-limit-card`, `-default` and `-ceiling`.
  - The zone comes from `cssp.timeZone` via `FileDisplay.ResolveZone`. The label shows the zone id, or "UTC".
  - The form is prefilled on first load and after a successful save or revert. A refused save keeps what the admin typed.
- `NavMenu.razor`: the Settings link (`monitor` icon) sits after Storage targets. It is CRLF, which was preserved.
- `appsettings.json`: `MaxUploadBytes` 107374182400 and `MaxUploadBytesCeiling` 1099511627776.

## Tests

Final: `dotnet build --no-incremental` gives 0 warnings and 0 errors. `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` gives Passed 1284, Skipped 19, Failed 0 (1303 total; the 3 new Pg tests skip without `CSSP_TEST_PG`).

New and changed tests:

- **`UploadLimitServiceTests`:**
  - The brief's seven tests.
  - `Effective_PermanentOverride_IsOverride` and `Invalidate_NextReadGoesToTheStore`.
  - `FormatGb` cases and `TooLargeMessage_UsesFormatGb`.
  - `Describe` (default, until, permanent/UTC).
  - `LocalToUtc` / `UtcToLocal` and `BytesFromGb`.
- **`SettingsPageTests`:**
  - Source checks for the route, policy and title.
  - A nav-order check.
  - A guard that the until-at input has no `@bind:format`.
  - An app-factory render as staff-admin (all testids present, no revert, "Default from configuration").
  - Staff denied.
- **`AdminServiceTests`:** the brief's six tests, plus:
  - permanent audit text
  - cache visibility after set and clear
  - clear denied for Staff
  - range boundaries (1 GiB and the ceiling are Ok; 0, -1, 1 GiB-1 and ceiling+1 are Invalid)
  - expiry boundaries (-60/0/+60 s Invalid, +61 s Ok)
  - note length
- **`UploadSessionServiceTests`:**
  - `Begin_AboveEffectiveLimit_TooLarge` and `Begin_ResumeAfterLimitLowered_StillResumes`.
  - `Begin_ReservedBytesCap_UsesEffectiveLimit`.
  - `Begin_ResumeWithDifferentFirstPiece_AfterLimitLowered_TooLarge`: a mismatched fingerprint is a new upload, so the limit applies.
  - `Begin_TooLargeWithoutAccess_DeniedFirst`, which covers R2.
- **`StartupChecksTests`:** the `MaxUploadBytesFrom` cases are replaced by `LimitsFrom` cases (null config gives 100 GiB and 1 TiB; within-ceiling cases; ceiling clamp; default clamp). The source assertion now checks `UploadLimitService.LimitsFrom(` and the ceiling warning text.
- **`PgSchemaTests`:** the table DDL, whitespace-normalised.
- **Store contract** (`UploadLimitStoreContract` in InMemoryStoresTests.cs):
  - In-memory and Pg both run `UploadLimitStore_SetGetClear_RoundTrips` and `UploadLimitStore_SetTwice_KeepsOneRow`.
  - The Pg version also counts rows directly.
  - Pg also checks that a second row id and bytes ≤ 0 are rejected (SQLSTATE 23514).
- **`PageAuthorizationAttributeTests`:** it does not enumerate pages (it scans every page for an attribute), so the new page is covered automatically. The explicit StaffAdmin-policy assertion is in `SettingsPageTests`.

### TDD evidence

- RED 1: after writing the tests, the test project failed to compile (`UploadLimitService`, `IUploadLimitStore`, `InMemoryUploadLimitStore` and `UploadLimitOverride` were not found).
- GREEN: after implementation, the full suite passed.
- Targeted RED by reverting each guard on its own:
  - Size check moved back ahead of the resumable branch: `Begin_ResumeAfterLimitLowered_StillResumes` FAILED.
  - Cache disabled: `GetState_CachesForThirtySeconds` and `Invalidate_NextReadGoesToTheStore` FAILED.
  - AdminService post-write `Invalidate()` removed: `SetUploadLimit_SeenAtOnceThroughTheCache`, `SetUploadLimit_StaffAdmin_SavesAndAudits` and `ClearUploadLimit_RevertsToDefault_Audited` FAILED.
  - Everything was restored afterwards, and the suite went green again.

## Tests adjusted for R2 and the new defaults

- `Begin_DefaultLimit_Is50Gb` became `Begin_DefaultLimit_Is100Gb`, because the new default is 100 GiB. It also asserts that exactly 100 GiB is accepted.
- The `Service(maxBytes:)` test helper now sets both `MaxUploadBytes` and `MaxUploadBytesCeiling`. Without that, the new 1 TiB default ceiling would clamp the large-blob and over-ceiling tests (for example `Begin_ConfiguredMaxAboveBlobBlockLimit_ClampedWithoutPerScopeWarning` with 2 TiB) where they previously relied on `MaxBlobBytes`.
- No existing test asserted too_large before access, so none needed reordering. `Begin_TooLarge_Invalid` uses a contributing user and still passes (`_fake.Calls` stays empty because authorisation makes no storage call). `Begin_TooLargeWithoutAccess_DeniedFirst` was added to pin the new order.
- Every constructor call site gained the `limits` argument: the UploadSessionService helper and `Begin_BlobLockMinutesFromConfig`; the AdminServiceTests `Build`, `WithOwners` and the cooldown `Make`; and AdminPageBaseTests.

## Smoke test

- The app ran in Development with Dev auth and fake storage. Port 5300 was not bindable on this machine (socket access forbidden), so it ran on 5377 through `Kestrel__Endpoints__Http__Url`.
- Signed in as staff-admin and opened `/admin/settings`. The page showed 100 GB / Default from configuration / 1024 GB ceiling. The zone label changed from UTC to America/Toronto once the circuit connected.
- **Bug found and fixed:** with `@bind:format="yyyy-MM-ddTHH:mm"` the `datetime-local` value never bound. The unquoted T is a .NET format character, so the browser rejected the value and the field cleared, and Save reported "The end time must be in the future." Removing the format fixed it, because Blazor's default datetime-local format is correct. A source guard test now prevents the format from coming back.
- Save of 150 GB until 2026-12-01 18:00 local: the page showed "Set by dev-staff-admin@advancedutility.com on 2026-11-25 14:12, until 2026-12-01 18:00", and Revert appeared.
- The audit log showed `limit 100 GB -> 150 GB; until 2026-12-01T23:00:00Z; note: smoke test` (EST is -5, so this is correct).
- Revert showed "back to the default" and audited `limit 150 GB -> default 100 GB`.
- The app was stopped and the `.playwright-mcp` artefacts removed.

## Self-review

- The brief's verbatim strings were checked: the range message, the end-time message, the op name, the audit path formats and the ceiling warning text.
- AdminService checks the role before reading the limit state, so a denied call writes exactly one Denied row and does not touch the store.
- `Bytes` on a clear row holds the default (the brief only specified Bytes for set). This is a judgement call; it could be null instead.
- Expiry validation is "> now + 1 minute" exactly (equal is refused). Expiry is normalised to UTC before it is stored.
- `UploadLimitService` holds no scoped dependencies, so it is safe as a singleton.
- The audit page filter list now includes `Admin.SetUploadLimit`.

## Concerns

- The `CLAUDE.md` configuration and architecture rows, `docs/operations.md` (which still says the default is 50 GiB, capped at 195.3 GiB), the Help text and the CHANGELOG are left to Task 8 per the plan. The 1.0.0 changelog line about the 195.3 GiB cap is now stale.
- The security-auditor has not been run. This change touches `Program.cs`, a new `@page`, and admin auth logic, so it needs one, per the plan's single post-all-tasks run.
- The Pg store and constraint tests compile but skipped locally (no `CSSP_TEST_PG`).
- The upload panel or client does not yet display the effective limit. The ledger says T7 consumes `FormatGb` / `GetStateAsync`.
