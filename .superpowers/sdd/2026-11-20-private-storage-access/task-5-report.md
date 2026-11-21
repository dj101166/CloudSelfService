# Task 5 report: sign-in, identity resolution and role authorization

**Status:** DONE_WITH_CONCERNS. The concerns are below, and none of them blocks the task.
**Commit:** `a6adf53` "Add Entra sign-in with guest admission, staff roles and dev-only test sign-in" on `feat/private-storage-access`, parent `6c627f1`. It has no Co-Authored-By trailer. Files were staged explicitly by name. Nothing under `.superpowers/`, no `.msg`, no `appsettings.Development.json` and no `.claude/settings*.json` was committed.

## What was built

- **`Services/Identity/EntraIdentity.cs`**: `EntraIdentity` record and `EntraIdentityReader.Read/IsStaff`.
  - `Read` returns null when `oid` is missing.
  - `Upn` is taken from `preferred_username`, then `email`, then `upn`.
  - `IsGuest` is true when `acct == "1"`, or when `idp` is present and differs from `iss`.
  - `IsStaff` requires `!IsGuest` and a UPN ending in `"@" + domain` (OrdinalIgnoreCase).
- **`Services/Identity/IdentityResolver.cs`**: `AdmissionOutcome`, `Admission`, `AppClaims` and `IdentityResolver`.
  - `AppClaims` holds the `UserId` constant `urn:cssp:user_id`, plus `PrincipalFor` and `UserIdOf` helpers. The cookie principal carries the Name UPN, `name` and the user id only; it never carries a role.
  - `AdmitAsync(EntraIdentity, CancellationToken)` matches the brief. An overload with an `AuditContext` carries the source IP and correlation id.
  - **Staff:** look up by oid, then by UPN. If nothing is found, create `Staff/None/Active` with the oid bound. Owners listed in `Authorization:Owners` (comma-separated, case-insensitive) are set to Owner on every sign-in. Role `None` gives `AdmittedPendingRole`; an inactive record gives `Deactivated`.
  - **Customer:** look up by oid, then by UPN. A UPN match counts only if the record's oid is null, and the oid is bound on that sign-in. A UPN match with a different bound oid gives `UnknownCustomer`. Nothing found gives `UnknownCustomer` and creates no record. An inactive user or inactive customer gives `Deactivated`.
  - Every outcome writes a `SignIn` audit row; `UnknownCustomer` and `Deactivated` are recorded as `Denied`, with the reason, oid, tenant and guest flag in `Error`. Admission updates `LastSignInAt`.
- **`Services/Identity/CurrentUser.cs`**: scoped. It re-reads `IUserStore` on every call, using the user-id claim from `AuthenticationStateProvider`.
- **`Authorization/AppRoleRequirement.cs`**: `Policies` (Staff, StaffAdmin, Owner), `AppRoleRequirement(StaffRole)` and `AppRoleHandler`.
  - The handler returns **before the first await** when there is no user-id claim, and keeps RM's comment explaining why.
  - Otherwise it succeeds only for an active `Kind=Staff` user whose role is at least the requirement.
- **`Authorization/NotAuthorizedRouting.cs`**: `ShouldRedirectToLogin`, the same as RM.
- **`Authorization/DevSignIn.cs`**:
  - `AssertSafe` throws `InvalidOperationException("Dev sign-in cannot run outside Development")`. `IsEnabled` requires the Dev provider **and** `IsDevelopment()`.
  - `Map` registers `GET /dev/signin?as=…` only when enabled.
  - Seeding, per your ruling:
    - Staff personas get a staff record with Owner or Staff. `staff-none` gets no seeded record, so the resolver auto-creates it.
    - Each customer persona gets its own `Demo Customer (Read|Contribute|Full)`, an active FileShare target `Demo share (Level)` on account `cssptestdemo`, and a grant at its level.
  - **Every persona, including `stranger`, goes through `IdentityResolver.AdmitAsync`** with a synthetic `EntraIdentity`:
    - Customers are guests (`IsGuest = true`, `@customer.example`).
    - Staff are members at `@{Entra:StaffEmailDomain}`.
  - An unknown `as=` value returns 400 listing the valid personas.
- **`Endpoints/AuthEndpoints.cs`**:
  - `OnTokenValidatedAsync`:
    - Calls `AdmitAsync`.
    - A denied outcome redirects to `/access-denied?reason=unknown|deactivated`, then `HandleResponse()`.
    - Otherwise it swaps in the cookie principal.
    - A missing oid is treated as `unknown`.
  - `/login/entra` issues the challenge. In Dev mode it redirects to `/login?error=entra_not_configured`.
  - `/logout` signs out of the cookie, plus OIDC when Entra is on, then goes to `/signed-out` (keeping `?reason=inactivity`).
  - `/keepalive` uses `RequireAuthorization()` and returns 204.
  - There is no `/auth/service-signin`.
- **`Program.cs`**:
  - Startup guards:
    - `DevSignIn.AssertSafe`.
    - Fail fast unless `Auth:Provider` is `Entra` or `Dev`.
    - With Entra, fail fast on an empty `Entra:TenantId`, `Entra:ClientId` or `Entra:ClientSecret`.
  - Cookie: `SameSite=Lax`, `SameAsRequest`, `ExpireTimeSpan` = `Auth:IdleTimeoutMinutes`, sliding expiration, `LoginPath=/login`, `AccessDeniedPath=/access-denied`.
  - OIDC:
    - Tenant-specific v2.0 authority, code flow, `MapInboundClaims=false`.
    - Scopes cleared to `openid profile email` only.
    - Token validation goes through `OnTokenValidated`.
    - `OnRemoteFailure` redirects to `/login?error=signin_failed`.
  - Policies are registered, and there is no FallbackPolicy.
  - Pipeline order: forwarded headers (For + Proto, known networks and proxies cleared) → exception handler + HSTS (outside Development) → HTTPS redirect → security headers (unchanged) → `MapStaticAssets` → authentication → authorization → antiforgery → `AuthEndpoints.Map` → Razor components.
- **`CloudSelfService.csproj`**: `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.9, the same version as the rest of the suite.
- **`Routes.razor`**: `AuthorizeRouteView` with the branching `NotAuthorized` from RM, plus the crash comment.
- **`App.razor`**: the idle timeout is initialised for authenticated requests with `Auth:IdleTimeoutMinutes` and `Auth:IdleWarningSeconds`, as in RM. `HttpContext` became nullable and null-safe.
- **`Shared/MainLayout.razor`**:
  - An active staff user with `StaffRole.None` gets `<PendingAccess />` instead of `@Body`.
  - Callers without a user-id claim resolve **synchronously**. That keeps the anonymous `RedirectToLogin` on the first, non-streaming pass; the reason is commented in the file.
  - `Body` is withheld until the role is known, so a pending user never sees a page flash.
- **`Shared/AppMenu.razor`**: the identity block (name and UPN), initials on the trigger and Sign out (`/logout`) are restored. There is no photo. The stale comment in `AppMenu.razor.css` is removed.
- **Shared and pages**:
  - `AccessDeniedContent` takes `Reason` and shows the brief's exact copy for `unknown` and `deactivated`, with a generic message otherwise.
  - `RedirectToLogin` navigates at most once (the latch).
  - `PendingAccess` uses the exact copy.
  - `Login` shows a single "Sign in with Microsoft" button and a note that guests use their own organisation's work account. In Dev mode it also lists the persona links.
  - `SignedOut` and `AccessDenied` use `LoginLayout` and `[AllowAnonymous]`.
  - `Index` now carries `[Authorize]`.
- **`CHANGELOG.md`**: the old "Auth intentionally NOT yet wired" bullet was false after this change. It is replaced by sign-in and account-menu bullets under `## [1.0.0] - TBD`.

## Tests

Full suite: `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` gives **Passed 209, Failed 0, Skipped 5** (the 5 skips are the existing PG integration tests, which need a database). The baseline before this task was 166 passed. `dotnet build CloudSelfService.slnx` gives **0 errors, 0 warnings**.

| File | Tests (named ones from the brief in **bold**) |
|---|---|
| `EntraIdentityTests` | **Read_GuestAcctClaim_IsGuest**, **Read_IdpDiffersFromIss_IsGuest**, **IsStaff_MemberAdvancedUpn_True**, **IsStaff_GuestWithAdvancedEmail_False**, **Read_MissingOid_Null**, plus Read_MemberWithoutGuestMarkers_IsNotGuest, Read_IdpEqualsIss_IsNotGuest, IsStaff_LookalikeDomain_False |
| `IdentityResolverTests` | **Admit_NewStaff_CreatedWithRoleNone_PendingRole**, **Admit_OwnerFromConfig_PromotedToOwner**, **Admit_UnknownGuest_UnknownCustomer_NoRecordCreated**, **Admit_PreRegisteredGuestFirstSignIn_BindsOid**, **Admit_UpnMatchButDifferentBoundOid_UnknownCustomer**, **Admit_InactiveCustomer_Deactivated**, **Admit_Denied_WritesDeniedAuditRow**, plus NewStaffListedAsOwner, ExistingStaffByUpn_BindsOid, InactiveStaff, GuestWithStaffDomainEmail, GuestMatchingStaffRecordByUpn, MemberMatchingCustomerRecordByOid, BoundGuestReturnsWithChangedEmail, InactiveCustomerUser, Deactivated_WritesDeniedAuditRow, Admitted_WritesSuccessAuditRow |
| `AppRoleHandlerTests` | **Handle_StaffRoleNone_FailsStaffPolicy**, **Handle_CustomerUser_FailsStaffPolicy**, **Handle_StaffAdmin_PassesStaffPolicy**, **Handle_DeactivatedStaff_Fails**, plus StaffAdmin_FailsOwnerPolicy, UnknownUserId_Fails, MalformedUserIdClaim_Fails |
| `RoleHandlerSynchronyTests` | **Handle_NoUserIdClaim_ReturnsCompletedTask** |
| `NotAuthorizedRoutingTests` | RM's two cases (**SignedInPrincipal_FailingAPolicy_IsNotSentToLogin**, **AnonymousPrincipal_IsSentToLogin**) plus RM's Routes.razor branch guard and the RedirectToLogin latch guard. RM's `LoginUrl` tests are not copied because `LoginUrl` has no consumer. |
| `DevSignInGuardTests` | **AssertSafe_DevProviderInProduction_Throws** (exact message checked), **AssertSafe_EntraInProduction_DoesNotThrow**, plus a case-insensitive "dev" in Staging throws, Dev in Development does not throw, and IsEnabled requires both conditions |

**RED:** with the tests written and no production code, `dotnet test` failed to compile with `CS0234` for "The type or namespace name 'Authorization' does not exist in the namespace 'CloudSelfService'" and "'Identity' does not exist in 'CloudSelfService.Services'", and `CS0246` for `IdentityResolver` and `EntraIdentity`. This came from all six new test files.

**GREEN:** after implementing, the suite gave 209 passed and 0 failed.

**Mutation check on the synchrony pin:** I inserted `await Task.Yield();` above the handler's early return. `Handle_NoUserIdClaim_ReturnsCompletedTask` then failed with `Assert.True() Failure`. I removed the line and the test passed again.

## Manual checks (Step 4)

Run with `ASPNETCORE_ENVIRONMENT=Development Auth__Provider=Dev` and in-memory stores. Kestrel bound to `:5300` from config.

**Startup guards:**
- `Auth__Provider=None` fails with "Auth:Provider must be 'Entra' or 'Dev' (found 'None')."
- `Production` plus `Dev` fails with "Dev sign-in cannot run outside Development".
- `Entra` with empty ids fails with "Entra:TenantId is required when Auth:Provider is Entra…".

**HTTP checks (curl):**

| Request | Result |
|---|---|
| anonymous `/` | 302 to `/login?ReturnUrl=%2F` |
| anonymous `/keepalive` | 302 to `/login` |
| `/login` | "Sign in with Microsoft", the guest note, and all 7 persona links |
| `/dev/signin?as=stranger` | 302 to `/access-denied?reason=unknown`. **No auth cookie in the jar**, and the page body has "Your account isn't set up for Cloud Self Service yet. Contact Advanced support and give them the email address you signed in with." |
| `as=root` | 400 with the list of valid personas |
| `as=staff-none`, then `/` | 200; the body has `data-testid="pending-access"` and "…administrator needs to grant you access before you can see any storage.", and not the Files heading |
| `as=staff`, then `/` | 200 with the Files heading |
| `as=staff`, then `/keepalive` | 204 |
| `as=staff-owner`, then `/zz-owner-probe` | 200 "Owner probe" |
| `as=customer-read`, `customer-contribute`, `customer-full`, then `/zz-owner-probe` | 302 to access-denied for each |
| `as=customer-read` again | 302 to `/` (seeding is idempotent) |
| `/logout?reason=inactivity` | 302 to `/signed-out?reason=inactivity`; `/` afterwards gives 302 to `/login` |

**Failing-policy principal (the crash path), checked with a temporary `Pages/ZzOwnerProbe.razor` marked `[Authorize(Policy="Owner")]`:**
- **Full page load** as `staff`: the endpoint authorization middleware forbids it first, giving 302 to `/access-denied?ReturnUrl=%2Fzz-owner-probe`, which renders "Access Denied / You do not have permission to view this page."
- **In-circuit navigation** (Playwright, signed in as `staff`, `Blazor.navigateTo('/zz-owner-probe')`): this is the async `NotAuthorized` render. The URL stays `/zz-owner-probe`, the only h1 is "Access Denied", `data-testid="access-denied"` is present, and the message is the generic one. Access Denied rendered **in place** with no navigation. The menu trigger showed initials "ST", and `SessionGuard.initIdleTimeout` was present.
- Afterwards `/login` still returned 200, so **the process stayed alive**. A grep of the server log for `stack overflow|unhandled|fail:|crit:` found **0 matches**. The only warnings were the in-memory-store notice, the Kestrel address override and "Failed to determine the https port".
- The probe page was **deleted before committing**. The dotnet process was stopped (`tasklist` shows no dotnet.exe), and the Playwright browser was closed. The `.playwright-mcp/` and cookie-jar scratch files were removed.

## Deviations and judgement calls

1. **Demo share names:** each persona's target uses share `demo-read`, `demo-contribute` or `demo-full` rather than `demo`. Three targets on `cssptestdemo/FileShare/demo` would violate the unique `(account, kind, container)` constraint that both stores enforce.
2. **Staff UPN fallback also fails closed:** a staff UPN match whose record has a *different* bound oid gives `UnknownCustomer` (shown as the "isn't set up" copy), instead of rebinding. Rebinding would let a recreated account inherit the old record's role. The brief states this rule for customers only.
3. **Records of the wrong kind are refused:** a guest whose oid or UPN matches a staff record, and a member who matches a customer record, both get `UnknownCustomer`. Neither identity is admitted under the other's record kind. Each case has a test.
4. **`AdmitAsync` overload:** I added `AdmitAsync(EntraIdentity, AuditContext?, CancellationToken)` so the audit row carries the source IP and correlation id. The brief's two-argument signature is kept and delegates to it.
5. **`Entra:ClientSecret` is part of the fail-fast check,** alongside TenantId and ClientId, as RM does. The sample already keeps it blank with `Provider: Dev`, so I did not need to edit the sample.
6. **No Graph photo:** `AppMenu` shows initials only.

## Concerns and suggestions

- **The forwarded-headers trade-off (per ruling):** clearing KnownNetworks/KnownProxies means `X-Forwarded-For` is trusted from any peer. The audit `SourceIp` is therefore only as trustworthy as the guarantee that App Gateway is the single ingress. A caller able to reach the container directly could spoof it. This is noted in a comment in `Program.cs`.
- **`/logout` is a GET**, as in RM, so a cross-site link can sign a user out. It is low impact, and it is the suite pattern.
- **Entra sign-out needs the post-logout redirect URI registered:** `/logout` in Entra mode signs out of OIDC, which sends the browser to Entra's end-session endpoint and back to `/signed-out`. That return address must be allowed on the app registration, or Entra will show its own page. This could not be tested without a registration.
- **The Entra path is untested end to end:** there is no registration, so the real OIDC round trip and the `acct` / `idp` claims in a live guest token are unverified. The `acct` claim is optional and must be added to the app registration's optional claims; the `idp != iss` fallback covers guests without it.
- **Settings suggestion (R6, not done):** consider allowing `Bash(dotnet test*)` in `.claude/settings.json`, and re-enabling the page-authorization hook now that pages carry `[Authorize]`.
- **Security review (Step 5) was not run, per R5.** It is left for the controller after task review.
- **`.superpowers/` stays untracked,** as instructed.

## Files

Created:
- `Authorization/AppRoleRequirement.cs`, `Authorization/NotAuthorizedRouting.cs`, `Authorization/DevSignIn.cs`
- `Endpoints/AuthEndpoints.cs`
- `Services/Identity/EntraIdentity.cs`, `Services/Identity/IdentityResolver.cs`, `Services/Identity/CurrentUser.cs`
- `Shared/AccessDeniedContent.razor`, `Shared/RedirectToLogin.razor`, `Shared/PendingAccess.razor`
- `Pages/Login.razor`, `Pages/SignedOut.razor`, `Pages/AccessDenied.razor`
- `CloudSelfService.Tests/` : `EntraIdentityTests.cs`, `IdentityResolverTests.cs`, `AppRoleHandlerTests.cs`, `RoleHandlerSynchronyTests.cs`, `NotAuthorizedRoutingTests.cs`, `DevSignInGuardTests.cs`

Modified:
- `Program.cs`, `Routes.razor`, `App.razor`, `_Imports.razor`, `CloudSelfService.csproj`
- `Shared/MainLayout.razor`, `Shared/AppMenu.razor`, `Shared/AppMenu.razor.css`
- `Pages/Index.razor`, `CHANGELOG.md`

Line endings: each modified file keeps its committed ending. `App.razor`, `MainLayout.razor` and `AppMenu.razor` are CRLF; the rest are LF. New files are LF, which matches 79 of the 85 text files in the repo.

---

# Fix round 1

**Commit:** `59bade0` "Revalidate sessions per request and make Owner config-authoritative", parent `a6adf53`. It has no Co-Authored-By trailer and was staged explicitly by name. **Build:** 0 errors, 0 warnings. **Suite:** Passed 229, Failed 0, Skipped 5 (the Postgres integration tests).

## Changes

**IMPORTANT 1: session revalidation.**

New file `Services/Identity/SessionValidator.cs`:
- `SessionValidator.ValidateAsync(principal, IUserStore, ICustomerStore, now, SessionLifetime, ct)` returns `SessionValidation(Valid, Reason, User)`.
- It rejects when:
  - the user-id claim is missing;
  - the issue-time claim is missing, or the session is older than `AbsoluteLifetime`;
  - the user record is missing (deleted) or inactive;
  - the `urn:cssp:oid` claim is missing or differs from the record's `EntraObjectId`;
  - the user is a customer and the customer is missing or inactive.
- `SessionLifetime.FromConfiguration` reads `Auth:AbsoluteSessionHours`, default 12. That key is now in `appsettings.json`. The type was renamed from `SessionOptions` because that name collides with `Microsoft.AspNetCore.Builder.SessionOptions`.

Claims and wiring:
- `AppClaims` gains `ObjectId = "urn:cssp:oid"` and `IssuedAt = "urn:cssp:iat"` (unix seconds).
- `PrincipalFor(user, scheme, issuedAt)` stamps both. It is called from `OnTokenValidated` and from `/dev/signin`, so dev personas stamp them too.
- Cookie `Events.OnValidatePrincipal = AuthEndpoints.OnValidatePrincipalAsync`: when the session is invalid it calls `RejectPrincipal()` plus `SignOutAsync(cookie)`, and logs `Session rejected: {Reason}` at Information.
- `TimeProvider.System` is registered.

`CurrentUser.GetAsync` now runs the same validator and returns null for any invalid session (inactive user, inactive customer, deleted record, oid mismatch, expired).

`MainLayout`:
- A signed-in principal whose `CurrentUser` is null gets a "Session ended" panel (`data-testid="session-ended"`) with a link to `/logout`, never `Body`.
- This is deliberately NOT `RedirectToLogin`. That code runs after an await, which is the async-completion path where a navigation crashes the host.
- The anonymous path remains synchronous, as before.

**IMPORTANT 2: Owner is config-authoritative (R12).**

New file `Services/Identity/OwnerConfig.cs` provides `IsOwner(upn, config)` and `EffectiveRole(user, config)`. `EffectiveRole` turns a persisted Owner whose UPN is not in `Authorization:Owners` into StaffAdmin.
- **At sign-in:** a UPN in config is set to Owner. A persisted Owner no longer in config is written back as StaffAdmin.
- **`AppRoleHandler`:** takes `IConfiguration` and compares `EffectiveRole` against the requirement. Its synchronous early return is unchanged.

**MINOR fixes:**

- **#4 first-sign-in bind race:**
  - The oid stamped in the cookie plus the validator's oid check means a session whose oid binding was lost is rejected on its next request.
  - The resolver also uses `BindAsync`, which upserts and then re-reads. It refuses (`UnknownCustomer`) if the stored oid is not ours, or if the upsert throws a unique violation.
- **#5 email fallback:**
  - `EntraIdentity` gains `UpnFromEmail`, set by `Read` when neither `preferred_username` nor `upn` is present. `Upn` still falls back to `email` for display.
  - Customer binding by UPN is refused (`UnknownCustomer`) when `UpnFromEmail` is set.
  - `IsStaff` also requires `!UpnFromEmail`, so an email-only member cannot be auto-created as staff.
- **#6:** `OnRemoteFailure` logs `Entra sign-in failed: {Message}` at Warning, using `ctx.Failure?.Message` only.
- **#7:** `PageAuthorizationAttributeTests.EveryPage_HasAuthorizeOrAllowAnonymous` scans `Pages/**/*.razor` files that have `@page` for `@attribute [Authorize` or `[AllowAnonymous]`. `Error.razor` is explicitly allow-listed. A companion test asserts each allow-listed page still carries `[ExcludeFromInteractiveRouting]`.

Left as instructed: #3 (hardening the `acct` claim) and #8 (GET `/logout`).

## Tests added or changed

**`SessionValidatorTests` (new):**
- Validate_ActiveUser_Accepted
- Validate_DeactivatedUser_Rejected
- Validate_DeletedUser_Rejected
- Validate_InactiveCustomer_Rejected
- Validate_ActiveCustomer_Accepted
- Validate_OidMismatch_Rejected
- Validate_OlderThanAbsoluteLifetime_Rejected (checks both sides of the 12 h boundary)
- Validate_CookieWithoutIssueTimeOrOid_Rejected
- SessionLifetime_DefaultsToTwelveHours
- **CurrentUser_InactiveUser_Null**, CurrentUser_InactiveCustomer_Null and CurrentUser_ActiveUser_ReturnsFreshRecord. These are plain unit tests with a fixed `AuthenticationStateProvider` and `TimeProvider`, no TestServer.

**`IdentityResolverTests`:** Admit_OwnerRemovedFromConfig_DemotedToStaffAdmin, Admit_OnlyEmailClaim_DoesNotBind, Admit_EmailOnlyMemberWithStaffDomain_NotCreatedAsStaff.

**`AppRoleHandlerTests`:** Handle_PersistedOwnerNotInConfig_FailsOwnerPolicy_PassesStaffAdmin and Handle_PersistedOwnerInConfig_PassesOwnerPolicy. The handler now takes config.

**`EntraIdentityTests`:** Read_OnlyEmailClaim_FlaggedAsUnverified.

**`PageAuthorizationAttributeTests` (new):** EveryPage_HasAuthorizeOrAllowAnonymous and AllowListedPages_StayStaticErrorPages.

**`RoleHandlerSynchronyTests`:** constructor updated. It still passes, so the early return is still synchronous.

## RED evidence

I wrote the fix code before its tests in this round, so RED was shown by mutation. Each guard was removed on its own, the full suite was run, and the file was restored afterwards.

| Guard removed | Tests that failed |
|---|---|
| `!user.Active` check | Validate_DeactivatedUser_Rejected, CurrentUser_InactiveUser_Null |
| oid equality check | Validate_OidMismatch_Rejected |
| absolute-lifetime check | Validate_OlderThanAbsoluteLifetime_Rejected |
| customer-active check | Validate_InactiveCustomer_Rejected, CurrentUser_InactiveCustomer_Null |
| handler uses `user.StaffRole` instead of `EffectiveRole` | Handle_PersistedOwnerNotInConfig_FailsOwnerPolicy_PassesStaffAdmin |
| resolver keeps the persisted role instead of `EffectiveRole` | Admit_OwnerRemovedFromConfig_DemotedToStaffAdmin |
| email-only bind guard | Admit_OnlyEmailClaim_DoesNotBind |
| `@attribute [Authorize]` removed from Index.razor | EveryPage_HasAuthorizeOrAllowAnonymous |

Each mutation produced `Failed!` with only the tests listed. After all files were restored the suite was back to 229 passed.

## Manual checks in Dev mode

Port 5300 had been taken in the meantime by an http.sys listener (PID 4), so the app ran on `:5397` via `Kestrel__Endpoints__Http__Url`.

**Absolute lifetime** (`Auth__AbsoluteSessionHours=0.0005`, about 1.8 s):

| Request | Result |
|---|---|
| sign in as `staff` | 302 to `/` |
| `/` immediately | 200 |
| `/keepalive` immediately | 204 |
| `/` after 3 s | 302 to `/login?ReturnUrl=%2F` |
| `/keepalive` after 3 s | 302 to `/login` |

The log shows `Session rejected: absolute session lifetime exceeded.` once. After the first rejection the cookie was cleared, so the later `/keepalive` request was simply anonymous.

**Default lifetime, personas re-run:**
- `stranger`: 302 to `/access-denied?reason=unknown`, and no cookie set.
- `staff-none`: `/` shows `pending-access`.
- `staff-owner`, `customer-read`, `customer-contribute`, `customer-full`: `/` shows the Files heading.
- Logged-out `/keepalive`: 302.

Both server logs had 0 matches for `stack overflow|unhandled|fail:|crit:`. Every dotnet process I started was killed (`tasklist` count 0), and the scratch files were removed.

**Deactivating a signed-in user** was proven by the unit tests (SessionValidator plus CurrentUser), not over HTTP, per your instruction not to add a dev toggle. The cookie hook and the circuit both call the same validator.

## Remaining notes

- Session validation does two store reads per cookie-authenticated request: the user, plus the customer for customer users. On Postgres that is two primary-key lookups per request, including each Blazor long-polling request. This is acceptable, but worth knowing.
- A live circuit learns of a deactivation the next time `CurrentUser` is called (MainLayout re-runs it on each navigation), and from Task 7 onwards on every storage operation. It does not learn while the user sits idle on one page. That is consistent with "takes effect on the next request".

---

# Fix round 2

**Commit:** `3814027` "Revalidate live circuits, tighten CSP and cookie, check token tenant", parent `59bade0`. It has no Co-Authored-By trailer, and the files were staged explicitly, including the new `.dockerignore`. **Build:** 0 errors, 0 warnings. **Suite:** 268 passed, 0 failed, 5 skipped (Postgres).

## Changes

1. **Dev owner persona.**
   - `OwnerConfig` is now an injected singleton built from `DevSignIn.EffectiveOwners(env, config)`.
   - That list is `Authorization:Owners`, plus `dev-staff-owner@{staff domain}` when, and only when, `DevSignIn.IsEnabled`.
   - The resolver and the role handler both use it.
2. **M1: circuit revalidation.**
   - `Authorization/SessionRevalidatingStateProvider` is a `RevalidatingServerAuthenticationStateProvider`. Its `RevalidationInterval` comes from `Auth:RevalidateSeconds`, default 60.
   - `ValidateAuthenticationStateAsync` creates an async scope and calls `SessionValidator.ValidateAsync`.
   - It is registered as the scoped `AuthenticationStateProvider`.
   - `AppRoleHandler` now runs the full `SessionValidator` check (oid, lifetime, active user, active customer) and applies `OwnerConfig.EffectiveRole`.
   - The no-user-id early return still sits above the first await. `RoleHandlerSynchronyTests` is green.
   - `SessionLifetime` is registered once as a singleton. `CurrentUser` and the cookie hook use it.
3. **M2: CSP.**
   - `script-src 'self' 'unsafe-inline'` (no `unsafe-eval`).
   - `connect-src 'self'`.
   - `img-src 'self' data:`.
   - `style-src` is unchanged.
   - A one-line comment at the header says the app deliberately tightens the suite CSP.
4. **M3: `.dockerignore`**, modelled on ReleaseMetrics.
   - It excludes `**/bin/`, `**/obj/`, IDE folders, `CloudSelfService.Tests/bin|obj`, `appsettings.Development.json`, `appsettings.*.json` (then `!appsettings.json`), `*.user`, `*.msg`, `.git/`, `.claude/`, `.superpowers/`, `.playwright-mcp/`, `docs/` and `CLAUDE-SECURITY-*/`.
   - The test sources are not excluded.
5. **L1: cookie.** `AuthPolicy.CookieFor(isDevelopment)`:
   - Outside Development: `__Host-cssp` with `Always`.
   - In Development: `cssp-dev` with `SameAsRequest`.
   - Program.cs applies both values.
6. **L3: failure logging.** `AuthPolicy.FailureSummary(ex)` returns the distinct `AADSTS\d+` codes found in `ex.ToString()`, otherwise the exception type name. The log line is `Entra sign-in failed: {Failure} (trace {TraceId})`, and the raw message is never logged.
7. **L6: name refresh.**
   - `IdentityResolver.RefreshNamesAsync` runs on staff and customer admission, after the record has been matched by oid or bound by a verified UPN.
   - It copies `Upn` and `DisplayName` from the token. It skips the copy for an email-only identity, and keeps the old UPN when another record already holds the new one, rather than throwing.
   - Staff role is computed after the refresh, so a renamed Owner is judged against the new UPN.
8. **L7: tenant.**
   - `AuthPolicy.ParseTenantId` requires a non-empty GUID. It runs at startup when Provider=Entra, so startup fails fast.
   - `OnTokenValidated` refuses with `/access-denied?reason=unknown` unless `AuthPolicy.IsOurTenant(tid, configured)` holds. That check compares as GUIDs, so case and braces don't matter.
9. **L8: headers.**
   - `Cross-Origin-Opener-Policy: same-origin` is set on every response.
   - `Cache-Control: no-store` is set when `AuthPolicy.ShouldDisableCaching(path)` holds.
   - **The rule:** no-store unless the path is at or under `/_framework`, `/_content`, `/css`, `/js`, `/images` or `/lib`, or its last segment contains a `.`. This covers every page, `/keepalive`, `/logout` and `/_blazor`. Static assets keep MapStaticAssets' own caching.

## Tests

**New: `AuthPolicyTests`**
- CookieFor_Production_HostPrefixedAndAlwaysSecure, CookieFor_Development_PlainNameSameAsRequest
- ParseTenantId_NotAGuid_Throws (4 cases: empty, null, domain name, empty GUID), ParseTenantId_Guid_Parses
- IsOurTenant_ComparesAsGuid (6 cases), IsOurTenant_CaseInsensitive
- FailureSummary_WithAadstsCodes_ReturnsOnlyCodes, FailureSummary_NoCode_ReturnsTypeNameNotMessage
- ShouldDisableCaching_PagesAndEndpointsOnly (10 cases)

**New: `SessionRevalidationTests`**
- **Revalidate_DeactivatedUser_False**, Revalidate_AnonymousState_False
- **DevStaffOwnerPersona_PassesOwnerPolicy_InDevelopment**
- **DevStaffOwnerPersona_NotAnOwner_OutsideDevSignIn** (Production+Dev, Development+Entra, Production+Entra)
- **Dockerignore_ExcludesDevelopmentSettings** (also checks the negation's order and that the test sources are included)

**Changed: `AppRoleHandlerTests`**
- Principals now come from `AppClaims.PrincipalFor`, and the handler is built through `HandlerFor`.
- Added **Handle_OidMismatchedPrincipal_FailsStaffPolicy** and **Handle_ExpiredPrincipal_FailsStaffPolicy**.

**Changed: `IdentityResolverTests`**
- **Admit_RenamedOwner_StoredUpnUpdated_RemovalFromConfigTakesEffect**
- Admit_RenamedCustomer_StoredUpnAndNameUpdated
- Admit_EmailOnlyIdentityForBoundUser_DoesNotRename
- Admit_RenameToUpnHeldByAnotherRecord_KeepsOldUpn

**Mutation RED evidence.** Each guard was removed in turn, the full suite was run, and the file was restored:

| Guard removed | Tests that failed |
|---|---|
| Handler skips the validator (plain store read) | Handle_ExpiredPrincipal, Handle_OidMismatchedPrincipal |
| Provider always returns true | Revalidate_AnonymousState_False, Revalidate_DeactivatedUser_False |
| Dev owner never appended | DevStaffOwnerPersona_PassesOwnerPolicy_InDevelopment |
| Dev owner always appended | DevStaffOwnerPersona_NotAnOwner_OutsideDevSignIn (3 cases) |
| No name refresh | Admit_RenamedCustomer, Admit_RenamedOwner |
| Refresh also from email | Admit_EmailOnlyIdentityForBoundUser_DoesNotRename |
| Production cookie `SameAsRequest` | CookieFor_Production |
| Tenant compare always true | IsOurTenant_ComparesAsGuid (4 cases) |
| Failure summary returns the message | both FailureSummary tests |
| no-store on static assets | ShouldDisableCaching_PagesAndEndpointsOnly |

## Runtime evidence

All runs were Development, `Auth__Provider=Dev`, on `:5397`.

**Headers (curl -I):**
- `/login` returned:
  - `Cache-Control: no-store`
  - `Cross-Origin-Opener-Policy: same-origin`
  - `Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self';`
- `/css/app.css` returned `Cache-Control: no-cache`, which is MapStaticAssets' own Development value and was not overridden, plus `Cross-Origin-Opener-Policy: same-origin`.
- The dev cookie is named `cssp-dev`, and `/keepalive` with it returned 204.

**(2) Circuit revalidation**, with `Auth__AbsoluteSessionHours=0.002` (about 7 s) and `Auth__RevalidateSeconds=3`:
- Playwright signed in as `staff`, landed on `/` (Files), then sat on the page without navigating.
- The circuit moved to `http://localhost:5397/login` by itself, showing the h1 "Advanced Cloud Self Service".
- `#blazor-error-ui` stayed hidden.
- The server log shows `Session rejected: absolute session lifetime exceeded.` twice, once for the circuit's tick and once for the subsequent `/login` load's cookie.
- `/login` still returned 200, so the process stayed alive. The log had 0 matches for `stack overflow|unhandled|fail:|crit:`.
- **Control run:** same lifetime, `RevalidateSeconds=600`. The page stayed on `/` with "Files" 12 s after sign-in, well past the 7 s lifetime, and no rejection was logged. So the move to `/login` in the first run was caused by the revalidation tick. This confirms the interactive RedirectToLogin path is safe: no crash.

**(3) CSP in the browser**, with Playwright signed in as `staff-owner`:
- The page loaded and the circuit connected via LongPolling (a `/_blazor?id=` resource was present, and the console showed the "using the Long Polling fallback transport" notice).
- The account menu opened and showed "Staff (Owner) / dev-staff-owner@advancedutility.com".
- Dark set `data-bs-theme="dark"`; Light set it to `light`.
- Release notes opened the dialog ("Release notes / v1.0.0 / Release date: TBD …").
- 0 `securitypolicyviolation` events, and 0 console lines matching `content security|refused to|csp`.
- The only console error during the session was the dev-only `hot-reload-settings.json` 404. Later errors were reconnect attempts after I stopped the server.

**Cleanup:** every dotnet process I started was killed (`tasklist` count 0), the browser was closed, and the scratch logs, cookies and `.playwright-mcp/` were removed.

## Notes

- `RedirectToLogin` from a revalidation tick goes to a bare `/login`. The cookie has already been rejected at that point, so the next HTTP request is anonymous.
- `Auth:RevalidateSeconds` is not added to `appsettings.json` (the default of 60 lives in code). Add it if it should be visible in config.
- A cookie issued by an earlier build has no oid or iat claim, so it is rejected on the next request. That forces a re-sign-in once after deploy, which is harmless because the app is unreleased.
