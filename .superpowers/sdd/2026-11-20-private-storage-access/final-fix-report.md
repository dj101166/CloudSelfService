# Final whole-branch review: fix report

Branch `feat/private-storage-access`. Base fefa322. Commits:

- `b9d6363` Derive staff role in one helper so configured Owners get in (item 1)
- `3eac5cd` Harden startup, audit refused tokens, pin the storage gate (items 2, 4 test, 6, 7, 8, 9, 10, 11, 12)
- `847b43a` Document gate callers, scan-tag deferral and ops corrections (items 3, 4 docs, 5, CHANGELOG)

Final verification:

- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj`: 1157 passed, 0 failed, 16 skipped. The skipped tests are the PG integration and live-storage tests, which need environment variables. No store or schema code changed, so the PG tests were not run.
- webapp-tests against the app on :5397 (Development, Dev auth, fake storage): 8 passed, including the new `test_staff_owner_reaches_staff_admin`.
- `git diff --stat` before committing showed only real line changes. CRLF files (MainLayout, NavMenu, test_cloudselfservice.py) kept CRLF and every other file kept LF. The new files are LF.

## 1. CRITICAL: configured Owner locked out (fixed)

- New file `Services/Identity/StaffAccess.cs` with three methods:
  - `Effective(user, owners)`: `OwnerConfig.EffectiveRole` for staff, None for customers.
  - `IsPending(user, owners)`: true for staff whose effective role is None.
  - `Reaches(user, owners, minimum)`.
- Call sites now using the helper:
  - MainLayout: `_pendingRole = user is not null && StaffAccess.IsPending(user, Owners)`. This was the bug: it read the stored role.
  - NavMenu: `StaffAccess.Effective`.
  - AdminPageBase.StillAuthorizedAsync: `StaffAccess.Reaches`.
  - AppRoleHandler: `StaffAccess.Reaches`.
- The services (AdminService, StorageAccessService, IdentityResolver) were already correct, since they call `EffectiveRole` with their own kind checks, and were left unchanged.
- `DevSignIn.EnsureStaffAsync` now stores the result of `StoredRole(personaKey)`, which is None for staff-owner. The persona is Owner only through `EffectiveOwners`, the same path production takes.
- Tests in `StaffAccessTests`:
  - Helper unit tests: config owner with stored None gives Owner and is not pending; staff None not in config is pending; a customer is never pending and holds no role, even when its UPN is in the Owners list and its record says Owner; a stored Owner no longer in config counts as StaffAdmin.
  - A source-pinning theory checks that all four call sites use the helper and none matches `StaffRole: StaffRole.None` or `EffectiveRole(`.
  - `DevStaffOwnerPersona_IsStoredWithNoRole_LikeProduction`.
  - HTTP prerender tests:
    - staff-owner's stored record is None and /admin/staff renders the staff table with no pending panel.
    - staff-none with its UPN in `Authorization:Owners` renders the admin staff table.
    - staff-none without config shows pending.
- Web test: `test_staff_owner_reaches_staff_admin` checks that the staff table and Owners section show, there is no pending panel and the admin nav is visible. It passed.
- Runtime check: the app ran with `Authorization__Owners=dev-staff-none@advancedutility.com` and signed in as staff-none.

  | Page | Result |
  |---|---|
  | `/` | No pending panel, admin nav shown |
  | `/admin/staff` | Staff table and Owner badge |
  | `/admin/customers` | Customers table |
  | `/admin/targets` | Target form |
  | `/admin/audit-log` | Filters |

  No page showed "Access pending" or Access Denied.

## 2. Missing connection string outside Development (fixed)

- `Services/StartupChecks.UsePostgres(isDevelopment, cs)` throws `InvalidOperationException("ConnectionStrings:Default is required outside Development.")` when the string is blank outside Development. Program.cs picks the stores through it.
- Tests:
  - Throws for null, empty and whitespace outside Development.
  - Returns in-memory in Development.
  - Returns Postgres whenever the string is set.
  - Program.cs source-pinning test.
  - `TestAppFactory` (Development, empty string) still starts, and the whole suite runs on it.
- Runtime: `ASPNETCORE_ENVIRONMENT=Production` with Entra values filled and no connection string exited with "Unhandled exception. System.InvalidOperationException: ConnectionStrings:Default is required outside Development."

## 3. Defender scan result badge (deferred, not implemented)

- I checked the role on Microsoft Learn (built-in-roles/storage, Storage Blob Data Contributor, id ba92f5b4-...). Its dataActions are blobs/delete, read, write, move/action and add/action. **It does not include `blobs/tags/read`.**
- Per the ruling I did not implement the badge. The deferral is recorded in:
  - The spec, as a §5.5 note "(deferred, R47)".
  - `docs/operations.md` under "Things an operator must know": why it is deferred, that scanning itself is unaffected, and how to revisit it (a custom role adding only tags/read).
- No `StorageItem` or Browse change.

## 4. Single-gate wording plus architecture test (done)

- `CloudSelfService.Tests/StorageGateArchitectureTests.cs` reflects over every type in the app assembly. It fails if any field, property, constructor parameter or method parameter has a type assignable to `IStorageBackendResolver`, `IStorageBackend` or `StorageClientFactory`, including inside generic arguments.
  - Compiler-generated nested types (lambdas, state machines) count as their outermost declaring type.
  - Allow-list: StorageAccessService, DownloadEndpoints, UploadSessionService, UploadCleanupService, AdminService, the backends, resolvers and factory, the fake, Program (DI registration) and DevSignIn.
  - DevSignIn is on the list because it takes `FakeStorageBackend?` to seed demo files. It is Development-only and the list carries a comment saying so. The caller should be aware of this addition.
- A second test checks that the scan really finds the five known callers, so the allow-list test cannot pass vacuously.
- Spec §5.4 gained the sentence naming those callers and saying they act only after a StorageAccessService decision and only on the target it returned.
- The CLAUDE.md security check is reworded to match and names the test.

## 5. operations.md accuracy (done)

- Test access cooldown is per admin, across all targets. This matches `ProbeCooldown.TryEnter(actor.Id)`.
- `Auth:RevalidateSeconds` re-checks that the user and the customer are active, the oid binding and the absolute lifetime. Roles are re-read on every page and action.
- Owner is derived from config on every role check (page, menu, action), never stored.
- `Transfers:RequestsPerMinute` and `MaxConcurrentPerUser` cover downloads and upload Begin, Status, Complete and Abort; chunk PUTs have their own limits. I confirmed this against the `RequireRateLimiting` calls in UploadEndpoints and DownloadEndpoints.
- The required connection string, the single startup clamp warning and the no-Owners warning are now documented.
- The CLAUDE.md config table rows for ConnectionStrings, Auth, Owners and Transfers were aligned with these.

## 6. Audit the early sign-in denials (done)

- `AuthEndpoints.OnTokenValidatedAsync` now writes a row through AuditService before redirecting when the token has no oid or comes from the wrong tenant:
  - Operation `SignIn`, result Denied, UserId null.
  - UserDisplay comes from preferred_username, then email, then upn.
  - Error is `Token has no object id (oid)` or `Token was issued by another tenant`, plus `(tenant {tid})` when present.
  - SourceIp and correlation come from `AuditContextFor`.
- The row is built by the pure `RefusedTokenEvent`. `IdentityResolver.SignInOperation` was made public so the operation name is shared.
- Tests:
  - Unit tests for the row's shape and fallbacks, including a null principal.
  - An end-to-end theory that builds a real `TokenValidatedContext` for both cases and asserts the stored row and the redirect to `/access-denied?reason=unknown`.
- The CHANGELOG claim "every sign-in, admitted or refused" is now true and the bullet mentions these cases.

## 7. Upload clamp warning once (done)

- The `UploadSessionService.MaxUploadBytesFrom(config)` pure helper returns (Effective, Configured). The constructor no longer logs. Program.cs logs the warning once after Build.
- The existing test was renamed to `Begin_ConfiguredMaxAboveBlobBlockLimit_ClampedWithoutPerScopeWarning` and now asserts 0 warnings from the service. New helper tests cover the default, an in-range value, a negative value and the over-ceiling clamp.

## 8. No-Owners warning (done)

- `StartupChecks.OwnersWarning(isDevelopment, owners)` returns "No Owners configured; nobody can assign staff roles." outside Development when the list is empty. Program.cs logs it.
- Tests cover the warning case and the silent cases (owners configured; Development).

## 9. StoragePaths / StoragePath duplication (done)

- `StoragePaths.Parent` and `Leaf` now delegate to `StoragePath`. No behaviour change: for paths without "/", the old `Leaf` slice `[(-1+1)..]` and the new one both return the whole path. The existing StoragePaths and StoragePath tests pass.

## 10. .gitignore (done)

- Added `.superpowers/`. The folder stays untracked.

## 11. Routine storage outcome logging (done)

- `StorageAccessService.LogFailure`:
  - NotFound and AlreadyExists log at Information without the exception object.
  - NotAuthorised, Unreachable, Conflict and Other keep Warning with the exception.
- Tests: two theories (2 routine cases and 4 fault cases) using the existing ListLogger.

## 12. session-guard.js keepalive (done)

- The keepalive fetch uses `redirect: "manual"`. A response whose type is `opaqueredirect` or whose status is 401 calls the new `sessionEnded()`, which navigates to `/logout`. That clears the cookie and lands on /signed-out, the same exit the guard already uses.
- The shared-script structure is unchanged. **The other suite apps' copies of session-guard.js are unchanged.** The workspace consistency checker (Check 34) may flag the drift.
- Runtime: an anonymous `/keepalive` returns 302 to /login, which a manual-redirect fetch sees as opaqueredirect. A signed-in `/keepalive` returns 204.
- Test: a source-pinning test in UploaderScriptTests.

## CHANGELOG

Bullets were merged into the existing `## [1.0.0] - TBD` sections, in plain language with no em-dashes and no config keys:

- Refused sign-ins of the new kinds are audited.
- An ended session is signed out at the next activity.
- Owners reach admin pages from their first sign-in.
- Under Security: the app will not start in production without its database, and it warns when no Owner is configured.

## Processes

Every app instance I started on :5397 and :5398 was stopped. No listener remains, and the temporary cookie and log files and the webapp-tests `__pycache__` were removed.
