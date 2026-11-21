# Task 11 tests report

Commit 6a44e20 on feat/private-storage-access. Build: 0 errors, 0 warnings. Tests: 1062 passed, 0 failed, 11 skipped (pre-existing).

## Code changes
- R40: `AdminService.IsValidUpn` rejects `Rune.ReplacementChar` (lone surrogate and literal U+FFFD) and `UnicodeCategory.PrivateUse`. `StoragePath.TryValidateSegment` (strict) had the same hole (its checks run over chars, with IsInvisible over runes, so a lone surrogate passed). Added `HasLoneSurrogate` to the strict path; the lenient path for existing items is unchanged on purpose.
- R41: `staff-admin` persona added to `DevSignIn` (StaffAdmin, "Staff (Admin)"); `AuditCsv_StaffAdmin_200` and `Personas_IncludeStaffAdmin` added.
- CS0108: `Staff.razor` re-injected `OwnerConfig Owners`, hiding `AdminPageBase.Owners`. Removed the duplicate `@inject`; the page now uses the inherited property.

## Defect found by the new tests (real, fixed)
- `AuditFilters.ToQuery` threw ArgumentOutOfRangeException for a `to` of `9999-12-31` (AddDays overflow), which would be a 500 on the CSV endpoint and audit page. Fixed with `EndOfDay` clamping to `DateTimeOffset.MaxValue`; test `AuditFilters_MaxDateTo_DoesNotThrow`.

## Adaptations to the list
- UPN length limit is 320 (as guessed): 320 true, 321 false.
- xUnit theory data mangles lone surrogates into U+FFFD, so lone-surrogate cases are built in `[Fact]` bodies; the U+FFFD literal is a separate theory row.
- Mutation denied theory covers 7 ops x {Staff role, unknown actor} (14 cases). Item 5 uses an Owner no longer in config.
- Item 7 uses TestAppFactory with `Transfers:RequestsPerMinute=1` (first 200, second 429, one ExportAudit row).
- Item 14 uses `FakeStorageBackend.FailOnCall` (it is also the resolver) instead of a custom stub.
- Item 12 allow-list: config normalisation (trim, lowercase, distinct, drop blanks) and fallback to defaults tested via `AllowedEndpointSuffixes`.
- Item 13 duplicate-name: in-memory store throws InvalidOperationException; asserts Invalid, no row, no audit.
- Added a CHANGELOG.md Security bullet (no `_changelog` entry in MainLayout since that list has no matching pending item format to extend).

## Tests added
AdminServiceTests (items 1-6, 8 part, 12-15, low items), AdminPageBaseTests (9-11), AdminEndpointTests (7, 8, StaffAdmin 200), StoragePathTests (surrogate), DevSignInGuardTests (persona).
