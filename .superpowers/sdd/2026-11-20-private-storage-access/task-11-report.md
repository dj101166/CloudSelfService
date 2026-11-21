# Task 11 report: staff admin

Status: DONE (with concerns noted below). Commit: see `git log -1` on feat/private-storage-access (follows 7516de9).

## Verification
- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test`: 947 passed, 10 skipped (was 869/10). Static guards (class/icon/stylesheet/page-attribute/token) green.
- Live run (Auth:Provider=Dev, Storage:UseFake, port 5397, Python Playwright, 37 checks, all pass):
  - staff-owner: added a Gov customer and a customer user (staff-domain UPN refused), saved cross-tenant status and tenant ID, registered a target (bad account name refused, gov endpoint suffix accepted), Test access returned "Reachable — the portal can list this file share.", granted Contribute (dropdown listed only that customer's target), Staff page showed the config owner read-only, audit log filtered by operation, CSV downloaded as `audit-yyyyMMdd-HHmm.csv` with no unsafe cells (a `=HYPERLINK(1)` customer name came out as `'=HYPERLINK(1)`).
  - staff: Admin nav group absent; /admin/customers and /admin/staff render Access Denied; CSV 302.
  - customer-full: no admin nav; /admin/audit-log renders Access Denied; CSV 302.
- App process stopped; port 5397 free.

## What was built
- `Services/AdminService.cs`: the seven methods with the brief's operation names, validation regexes (account `^[a-z0-9]{3,24}$`, container per brief, subscription GUID, suffix allow-list `Storage:AllowedEndpointSuffixes` default `core.windows.net` + `core.usgovcloudapi.net`), plus `CsvCell`, `AuditFilters`, `AdminAlert`. Every method re-loads the actor via `IUserStore` and requires the role (actor must be active Staff kind; Owner via `OwnerConfig.EffectiveRole`). Role refusals write a Denied audit row; validation failures write nothing.
- Methods take an optional `AuditContext` (from `PageAuditContext.Next()` in the pages) so rows carry SourceIp and a per-action correlation id.
- `Endpoints/AdminEndpoints.cs`: `GET /api/admin/audit.csv` (StaffAdmin, Sec-Fetch-Site cross-site/same-site 403, bad filter 400, store failure 503, no-store, attachment, nosniff, limit 5000).
- Pages/Admin: Customers, CustomerDetail, Targets, Staff (Owner), AuditLog, each with `.razor.css`; `Shared/AdminPageBase.cs` and `AdminAlertBox.razor` hold the shared load/act/alert handling (store failures show "The service is temporarily unavailable.", never end the circuit). Shared admin CSS is in `wwwroot/css/app.css`.
- NavMenu: Admin group for StaffAdmin+, Staff link for Owner only (display convenience; pages/service re-check).
- `OwnerConfig.Upns` exposed so the Staff page can list config owners.
- `docs/gov-cloud-guest-setup.md` (placeholder `<Advanced tenant ID>`, cites Microsoft Cross-cloud settings page). CHANGELOG entry added.
- Tests: `AdminServiceTests` (all 13 brief-named tests plus extras: malformed UPNs, suffix allow-list, gov default status, denied audit rows, probe not called when denied) and `AdminEndpointTests` (CSV headers, safe cells, filters, cross-site, bad filter, below-StaffAdmin, anonymous).

## Rulings applied
- Staff users: only an Owner may deactivate/activate them (StaffAdmin gets Denied + audit row); nobody changes their own access; config owners cannot be deactivated or re-roled in the UI.
- SetStaffRole: self refused, Owner refused, config owners refused, target must be Staff kind. The Staff page offers None/Staff/Staff Admin.
- AddCustomerUser: needs one `@` with text either side, no spaces, not staff domain, case-insensitive unique, display name defaults to local part; Gov customers show the UPN note.
- Gov customers saved with status NotRequired are normalised to Pending; Commercial is always NotRequired.

## Concerns / deviations
- Commit subject shortened to 72 chars ("Add staff admin: customers, users, targets, grants, roles, audit") instead of the brief's longer title, per the ≤72 commit rule.
- No new icons were needed (reused users, database, shield, history, plus, pencil, download), so Icon.razor is unchanged.
- Dev-run note: `ASPNETCORE_URLS` is overridden by the config-driven `Kestrel:Endpoints:Http:Url` (default 5300, which is in a Windows excluded port range here); I ran on 5397 via `Kestrel__Endpoints__Http__Url`.
- Playwright check needed `domcontentloaded` waits (SignalR never goes network-idle) and locator text waits (CSP forbids `eval`, so `wait_for_function` fails).
- Date filter inputs use `value`/`@onchange` handlers (string `@bind` on `type=date` binds as TimeOnly/DateTime and fails to compile).
- Anonymous or below-role CSV requests get a 302 (cookie challenge/access-denied redirect) rather than 401/403; not served either way.
- A stale page cannot act: the service re-reads the actor every call. A staff user demoted mid-session still sees the old nav until it re-renders; actions are refused.
- Not done per R5: security-auditor and unit-test-reviewer (controller runs them).

## Fix round 1
- IMPORTANT stale-role reads: `AdminPageBase.StillAuthorizedAsync()` re-reads the user and requires `OwnerConfig.EffectiveRole >= RequiredRole` (StaffAdmin; Owner for Staff). Called before every load, reload (`ActAsync`), and `AuditLog.ApplyAsync` (via `RunGuardedAsync`). Failure clears the page's data and `AdminGate.razor` renders Access Denied in place (or Session ended when the user is gone). The CSV href is built only from applied filters. Tests: `AdminPageBaseTests` (demote between load and apply, demote during an action, Owner-only page, session ended, reload failure keeps success + warning).
- Minors 1-11 done: Version-keyed selects (grant level, staff role) revert after refused changes; repointing a granted target's account/container/kind/suffix refused; CSV export audited (`Admin.ExportAudit`, path = filter summary, bytes = rows) and rate-limited (Transfers); CsvHref from applied filters; ActAsync null user -> session ended; store writes wrapped in SetUserActive/SetStaffRole; stricter UPN validation; Guid "D" form for tenant/subscription; `Storage:AllowedEndpointSuffixes` in appsettings.json and Targets default suffix falls back to first allowed; extra tests; data-testids on CustomerDetail links.
- Results: build 0/0; 974 passed / 10 skipped; Playwright re-run 37/37 plus applied-filter href and export-audit row checks. A live refused-grant dropdown revert was not driven in the browser (needs a mid-session demotion); covered by the Version-bump unit tests.

## Security follow-up (R43/R44)
- M1: `IUserStore` gains `SetActiveAsync`, `SetStaffRoleAsync` (Staff kind only) and `RecordSignInAsync` (single conditional UPDATE ... RETURNING; binds oid, refreshes upn/name/time only while active and unbound-or-same-oid). AdminService uses the first two; IdentityResolver sign-in never writes Active or role (existing staff and customers go through RecordSignInAsync; only a brand-new staff record is inserted, with role None). Decision: Owner is never persisted by sign-in; `OwnerConfig.EffectiveRole` derives it from config (and still reads a legacy persisted Owner outside the config as StaffAdmin). Interleave tests (customer deactivated, staff demoted between read and write) pass.
- M2: `CsvCell.Escape` quotes every cell and prefixes `'` for `= + - @ tab CR` at cell start or after `; , | tab` (optionally after spaces).
- M3: `Storage:AllowedSubscriptions` (empty = any, startup Warning logged, verified); SaveTarget rejects others; creating an internal target or moving one to internal needs Owner (StaffAdmin gets Denied + audit row). Editing an existing internal target's other fields is allowed for StaffAdmin.
- L1: `UpnNormalizer` (NFKC + ToLowerInvariant) used by in-memory store, PG `upn_normalized` column (backfill, NOT NULL, unique index), staff-domain check, OwnerConfig matching, add-user (domain must be ASCII; config owners refused).
- L2: `EntraIdentity.Idp`, `IdentityResolver.HomeTenantOf`; first customer bind refuses Pending cross-tenant and a home-tenant mismatch (audited "Home tenant does not match").
- L4: Test access 20 s linked timeout ("The storage account didn't respond in time.") and 5 s per-actor cooldown (Busy).
- L5: export over the limit appends `# truncated at N rows ...` and filename `-truncated`. L6: audit indexes, 30 s query timeout, `RateLimits.AuditExport` (10/min per user, `Admin:AuditExportsPerMinute`). L7: trigger rejects owner/account/container/kind/suffix change while grants exist.
- INFO: `OwnerConfig.Upns` returns a copy. R44: `core.usgovcloudapi.net` removed from default suffixes (appsettings + code); Targets hint text updated; the gov doc is about guest sign-in only so unchanged.
- Results: build 0/0; 1099 passed / 15 skipped (the PG tests skip without the env var); PG integration 16/16 against postgres:17-alpine at 172.21.108.241:55432 and container stopped; Playwright admin checks 37/37 (Gov-suffix step removed). Note the existing-user sign-in no longer rewrites a persisted Owner row; `/dev/signin` staff-owner still gets Owner via config.

## Security follow-up round 2
- Storage:AllowedSubscriptions now fails fast: `AdminService.ParseAllowedSubscriptions` throws "Storage:AllowedSubscriptions contains an invalid subscription id: {entry}" for any non-blank non-GUID (or empty GUID) entry; blanks ignored. Used by the AdminService constructor (so a bad value stops the first admin use) and by Program.cs, whose "empty" startup warning is now based on the parsed set (a bad value throws at startup).
- Test access cooldown moved to a singleton `ProbeCooldown` (ConcurrentDictionary + TimeProvider), injected into AdminService; tested across two AdminService instances and window expiry.
- R45: for an existing internal target, handing it to a customer or changing account/container/kind/suffix needs an Owner (Denied + audit row); display name and active edits remain StaffAdmin.
- Member customer users with a recorded HomeTenantId are compared against the token `tid` (`HomeTenantOf` returns tid for non-guests). Adding a configured Owner's UPN returns OwnersFromConfigMessage.
- Test access timeout configurable via `Admin:TestAccessTimeoutSeconds` (default 20); the test now uses a 0.05 s timeout with a slow probe and asserts TestTimeoutMessage in the result and the audit row.
- Results: build 0/0; 1110 passed / 15 skipped (PG tests skip without CSSP_TEST_PG; no store changes this round).
