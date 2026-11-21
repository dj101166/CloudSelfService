### Task 5: Sign-in, identity resolution and role authorization

**Files:**
- Create: `Services/Identity/EntraIdentity.cs`, `Services/Identity/IdentityResolver.cs`, `Services/Identity/CurrentUser.cs`, `Authorization/AppRoleRequirement.cs`, `Authorization/NotAuthorizedRouting.cs`, `Authorization/DevSignIn.cs`, `Endpoints/AuthEndpoints.cs`, `Shared/{AccessDeniedContent.razor, RedirectToLogin.razor, PendingAccess.razor}`, `Pages/{Login.razor, SignedOut.razor, AccessDenied.razor}`
- Modify:
  - `Routes.razor`: switch to `AuthorizeRouteView` with the branching `NotAuthorized` (copy from RM).
  - `Program.cs`.
  - `Shared/MainLayout.razor`.
  - `CloudSelfService.csproj`: add `Microsoft.AspNetCore.Authentication.OpenIdConnect`.
  - `Pages/Index.razor`: add `[Authorize]`.
  - `.claude/settings.json`: allow `Bash(dotnet test*)`.
- Test: `EntraIdentityTests.cs`, `IdentityResolverTests.cs`, `AppRoleHandlerTests.cs`, `RoleHandlerSynchronyTests.cs`, `NotAuthorizedRoutingTests.cs`, `DevSignInGuardTests.cs`

**Interfaces:**
- Consumes: `IUserStore`, `ICustomerStore`, `AuditService` (Task 4).
- Produces:

```csharp
public sealed record EntraIdentity(string ObjectId, string TenantId, string Upn, string DisplayName, bool IsGuest);
public static class EntraIdentityReader {
    public static EntraIdentity? Read(ClaimsPrincipal p);   // null if oid missing; Upn = preferred_username ?? email ?? upn
    public static bool IsStaff(EntraIdentity id, string staffDomain);
}
public enum AdmissionOutcome { Admitted, AdmittedPendingRole, UnknownCustomer, Deactivated }
public sealed record Admission(AdmissionOutcome Outcome, AppUser? User);
public sealed class IdentityResolver { public Task<Admission> AdmitAsync(EntraIdentity id, CancellationToken ct = default); }
public static class AppClaims { public const string UserId = "urn:cssp:user_id"; }
public static class Policies { public const string Staff = "Staff"; public const string StaffAdmin = "StaffAdmin"; public const string Owner = "Owner"; }
public sealed class CurrentUser { public Task<AppUser?> GetAsync(CancellationToken ct = default); } // scoped; re-reads IUserStore every call
```

**Rules the tests pin:**
- **Guest detection:** `IsGuest` is true when the `acct` claim is `"1"`, or when an `idp` claim is present and differs from `iss`.
- **Staff check:** `IsStaff` requires `!IsGuest` and a UPN ending in `"@" + staffDomain` (OrdinalIgnoreCase).
- **Staff admission:**
  - Look up by `oid`, then by UPN. If neither finds a record, create `Kind=Staff, StaffRole=None, Active=true` with the `oid` bound.
  - A UPN listed in the comma-separated `Authorization:Owners` is set to `Owner` on every sign-in.
  - The outcome is `AdmittedPendingRole` when the role is `None`, otherwise `Admitted`.
  - An inactive staff record gives `Deactivated`.
- **Customer admission (any non-staff identity):**
  - Look up by `oid`, then by UPN. A UPN match counts only when the record's `EntraObjectId` is null, and the `oid` is bound on that first sign-in.
  - A UPN match whose bound `oid` differs from the token's gives `UnknownCustomer`, so a reused email address can't take over the record.
  - Nothing found gives `UnknownCustomer`, and no record is created.
  - An inactive user or inactive customer gives `Deactivated`.
- **Audit and timestamps:** every outcome writes a `SignIn` audit row, with `UnknownCustomer` and `Deactivated` recorded as `AuditResult.Denied`. Admission updates `LastSignInAt`.
- **`OnTokenValidated`:**
  - Calls `AdmitAsync`.
  - Denied outcomes redirect to `/access-denied?reason=unknown|deactivated` and then `HandleResponse()`.
  - Otherwise it replaces the principal with a cookie identity carrying `ClaimTypes.Name` = UPN, `name` = display name, and `AppClaims.UserId`.
  - No Graph photo call (guests have none in our tenant), so the requested scopes are `openid profile email` only.
- **`AppRoleHandler`:**
  - With no `AppClaims.UserId`, it returns **before the first `await`**.
  - Otherwise it loads the user and succeeds only for an active `Kind=Staff` user with `StaffRole >= required`.
- **`DevSignIn`:**
  - Mapped only when `Auth:Provider == "Dev"` **and** `IsDevelopment()`, at `GET /dev/signin?as=staff-owner|staff|staff-none|customer-read|customer-contribute|customer-full|stranger`.
  - It seeds a demo customer, a demo target and grants into whichever stores are registered, then signs in the matching principal.
  - `DevSignIn.AssertSafe(IHostEnvironment, IConfiguration)` runs at startup. When the provider is `Dev` and the environment is not Development, it throws `InvalidOperationException("Dev sign-in cannot run outside Development")`.
- **No `/auth/service-signin`.**
- **`AuthEndpoints`:**
  - `/login/entra` issues the challenge.
  - `/logout` signs out of both cookie and OIDC, then goes to `/signed-out`.
  - `/keepalive` requires authorization and returns 204.
- **Cookie:** `SameSite=Lax`, `SecurePolicy=SameAsRequest`, `ExpireTimeSpan = Auth:IdleTimeoutMinutes`, sliding expiration.
- **Forwarded headers:** `UseForwardedHeaders` with `XForwardedFor|XForwardedProto`, with known networks and proxies cleared (as in RM).
- **`MainLayout`:** when the current user is staff with `StaffRole.None`, render `<PendingAccess />` instead of `@Body`. Copy: "Your account is set up. A Cloud Self Service administrator needs to grant you access before you can see any storage."
- **`Pages/AccessDenied.razor`:**
  - With `reason=unknown`: "Your account isn't set up for Cloud Self Service yet. Contact Advanced support and give them the email address you signed in with."
  - With `reason=deactivated`: "Your access has been turned off. Contact Advanced support."
- **Session guard:** turn on the idle timeout, passing `Auth:IdleTimeoutMinutes` and `Auth:IdleWarningSeconds` through as RM does.

- [ ] **Step 1: Write the failing tests.**
  - `EntraIdentityTests`:
    - `Read_GuestAcctClaim_IsGuest`
    - `Read_IdpDiffersFromIss_IsGuest`
    - `IsStaff_MemberAdvancedUpn_True`
    - `IsStaff_GuestWithAdvancedEmail_False`
    - `Read_MissingOid_Null`
  - `IdentityResolverTests` (in-memory stores):
    - `Admit_NewStaff_CreatedWithRoleNone_PendingRole`
    - `Admit_OwnerFromConfig_PromotedToOwner`
    - `Admit_UnknownGuest_UnknownCustomer_NoRecordCreated`
    - `Admit_PreRegisteredGuestFirstSignIn_BindsOid`
    - `Admit_UpnMatchButDifferentBoundOid_UnknownCustomer`
    - `Admit_InactiveCustomer_Deactivated`
    - `Admit_Denied_WritesDeniedAuditRow`
  - `AppRoleHandlerTests`:
    - `Handle_StaffRoleNone_FailsStaffPolicy`
    - `Handle_CustomerUser_FailsStaffPolicy`
    - `Handle_StaffAdmin_PassesStaffPolicy`
    - `Handle_DeactivatedStaff_Fails`
  - `RoleHandlerSynchronyTests.Handle_NoUserIdClaim_ReturnsCompletedTask`
  - `NotAuthorizedRoutingTests`: copy RM's two cases.
  - `DevSignInGuardTests`:
    - `AssertSafe_DevProviderInProduction_Throws`
    - `AssertSafe_EntraInProduction_DoesNotThrow`
- [ ] **Step 2: Run them; expect FAIL.**
- [ ] **Step 3: Implement.** Wire `Program.cs` in this order: forwarded headers → HSTS/HTTPS redirect → security headers → static assets → authentication → authorization → antiforgery → endpoints.
- [ ] **Step 4: Run the tests and expect PASS.** Then run the app with `Auth:Provider=Dev` and check each sign-in:
  - `/dev/signin?as=stranger` → the Access Denied "isn't set up" copy.
  - `as=staff-none` → the PendingAccess panel.
  - **Failing-policy principal (required by the migration recipe):**
    1. Add a temporary page `@page "/zz-owner-probe"` with `[Authorize(Policy="Owner")]`.
    2. Sign in `as=staff`, open it, and confirm Access Denied renders in place, the process stays alive, and the server log shows no stack overflow.
    3. Delete the page.
- [ ] **Step 5: Security review.** Invoke the `security-auditor` agent on this task's diff, fix what it finds, and re-run the tests.
- [ ] **Step 6: Commit:** "Add Entra sign-in with guest admission, staff roles and dev-only test sign-in".

---

