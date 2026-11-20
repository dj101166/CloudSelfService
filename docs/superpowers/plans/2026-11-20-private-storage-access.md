# Private Storage Access Portal (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild Cloud Self Service onto the suite standard and ship an Entra-signed-in portal through which customers (B2B guests) and Advanced staff browse, download, upload (chunked, resumable), create folders, rename/move and delete in Azure file shares and blob containers that sit behind private endpoints.

**Architecture:** One Blazor Server app (Blazor Web App render model, sumi skin). Every storage call goes through `StorageAccessService`, which reloads the user, computes an effective access level, refuses or executes through an `IStorageBackend` (Files or Blob, managed identity), and writes an audit row. Downloads and chunked uploads are minimal-API endpoints outside the Blazor circuit. State is held in PostgreSQL.

**Tech Stack:** .NET 10, Blazor Web App (InteractiveServer), Npgsql 10.0.3, Azure.Identity 1.21.0, Azure.Storage.Files.Shares 12.28.0, Azure.Storage.Blobs 12.30.1, Microsoft.AspNetCore.Authentication.OpenIdConnect 10.0.x, Markdig, xUnit 2.9.3, Microsoft.AspNetCore.Mvc.Testing 10.0.x, Python Playwright (web tests).

**Spec:** `docs/superpowers/specs/2026-11-20-private-storage-access-design.md`

**Reference app:** `D:\SC\Personal\ReleaseMetrics` (already on the sumi skin, Entra, PostgreSQL, roles, audit). "Copy from RM" below means: copy the file, rename the namespace to `CloudSelfService`, and rename `releaseMetrics*` localStorage keys / `rm` JS names as stated in the task.

## Global Constraints

- Branch: `feat/private-storage-access` in `D:\SC\Personal\CloudSelfService`. Commit steps run only once Dan has authorised commits for this execution. **Never add a `Co-Authored-By` trailer.**
- Target `net10.0`; Nullable and ImplicitUsings enabled.
- Page titles: `<PageTitle>{Page} — Advanced Cloud Self Service</PageTitle>`; layout `<PageTitle>Advanced Cloud Self Service</PageTitle>`.
- CSS: sumi tokens only (`var(--accent)`, `var(--surface-page)`, `var(--surface-raised)`, `var(--text-primary)`, `var(--border)` …). No literal hex outside `wwwroot/css/theme.css`.
- Theme attribute `data-bs-theme` on `<html>`. localStorage keys `cloudSelfServiceTheme`, `cloudSelfServiceSidebarCollapsed`.
- Comments only where the WHY is non-obvious.
- Every `@page` carries `@attribute [Authorize]` or a policy, except `/login`, `/signed-out` and `/access-denied`, which are `[AllowAnonymous]`.
- Security headers stay: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, the `Permissions-Policy`, HSTS 365 days, CSP with `frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'`.
- No storage keys, SAS tokens or direct storage URLs ever reach a browser.
- Files access uses `ShareTokenIntent.Backup` on every client.
- Fixed upload chunk size **4 MiB (4,194,304 bytes)**; default parallel chunks **4**; session lifetime **24 h**; `Transfers:MaxUploadBytes` default **53,687,091,200** (50 GiB).
- Downloads are always `Content-Disposition: attachment` and `Content-Type: application/octet-stream`.
- Test project `CloudSelfService.Tests`. Run with `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj`, naming the project explicitly: a bare `dotnet test` runs nothing. Test names follow `Method_Condition_Expected`.
- Dockerfile: no `--no-restore` on `dotnet publish`; final stage runs as `appuser` (UID 1001) via `gosu`.
- Never commit `appsettings.Development.json`, `*.msg`, `bin/`, `obj/`.
- Never run `pulumi up/preview/refresh/destroy`, and never any podman/az auth command.
- Changelog: one file, `CHANGELOG.md`, top entry `## [1.0.0] - TBD`. It is embedded and rendered by `ChangelogDocument`; there is no `_changelog` list.

## Review Focus

1. **Zero-byte upload.** A 0-byte file must upload and appear as an empty file. It has zero chunks, and Complete must still succeed. Pinned in Task 9 (`Complete_ZeroByteFile_CreatesEmptyFile`).
2. **Grant revoked or user deactivated mid-upload.** The next chunk PUT must be refused with 403 and no bytes written, even though the session exists. Pinned in Task 9 (`PutChunk_GrantRevokedAfterBegin_Denied`).
3. **Names with spaces, `#`, `%`, `+`, `&`, `'` and non-ASCII.** These must list, download with the right filename (RFC 5987 `filename*`), rename and delete. Pinned in Task 3 (`StoragePath` round-trips) and Task 8 (`Download_NameWithSpecialCharacters_HeaderEncoded`).
4. **Storage unreachable, or the app identity missing RBAC on an account.** Show a readable "storage unreachable" or "the portal is not authorised on this storage account" message, never a 500 or a stack trace. Pinned in Task 6 (`StorageErrorMapper` tests) and Task 7 (`List_BackendUnavailable_ReturnsUnavailable`).
5. **A folder with more than one page of entries.** Azure Files returns up to 5,000 entries per page. The continuation token must pass through and the UI must offer "Load more" rather than silently truncating. Pinned in Task 7 (`List_PassesContinuationToken`).

---

## File Structure

```
CloudSelfService.slnx                     app + tests (replaces CloudSelfService.sln)
App.razor, Routes.razor                   document shell; router with branching NotAuthorized
Program.cs                                composition root; endpoints mapped from Endpoints/*
Authorization/
  AppRoleRequirement.cs                   StaffRole-minimum requirement + handler (sync early return)
  NotAuthorizedRouting.cs                 anonymous → login, else Access Denied in place
  DevSignIn.cs                            Development-only test principal + startup guard
Endpoints/
  AuthEndpoints.cs                        /login/entra, /logout, /keepalive, /api/antiforgery
  DownloadEndpoints.cs                    GET /api/files/{targetId}/download
  UploadEndpoints.cs                      /api/uploads/*
Models/                                   enums + records (Task 3)
Services/
  Access/AccessPolicy.cs                  pure: effective level + required level per op
  Access/StoragePath.cs                   pure: path/name normalisation
  Access/StorageAccessService.cs          THE gate to storage
  Identity/EntraIdentity.cs               pure: claims → identity, staff vs guest
  Identity/IdentityResolver.cs            sign-in admission + oid binding
  Storage/IStorageBackend.cs, FileShareBackend.cs, BlobBackend.cs,
  Storage/StorageClientFactory.cs, StorageErrorMapper.cs
  Transfers/ChunkMath.cs, RangeHeader.cs, ContentDispositionHeader.cs   pure helpers
  Transfers/UploadSessionService.cs, UploadCleanupService.cs
  Data/PgsqlContext.cs, I*Store.cs, Pg*Store.cs
  AuditService.cs, AdminService.cs, AppVersionService.cs, ChangelogDocument.cs
Pages/  Index (Files home), Files/Browse, Admin/{Customers,CustomerDetail,Targets,Staff,AuditLog},
        Help, Login, SignedOut, AccessDenied
Shared/ MainLayout, NavMenu, AppMenu, Icon, ChangelogDialog, ReconnectModal, LoginLayout,
        AccessDeniedContent, RedirectToLogin, PendingAccess
wwwroot/css/{theme.css, app.css}  wwwroot/js/{cloudselfservice.js, session-guard.js, uploader.js}
CloudSelfService.Tests/                   xUnit; Infrastructure/TestAppFactory.cs (in-memory stores and
                                          FakeStorageBackend live in the app: Services/Data, Services/Storage)
webapp-tests/                             Python Playwright suite owned by the app
docs/gov-cloud-guest-setup.md             customer-IT checklist
```

---

### Task 1: Render model, solution and test project

**Files:**
- Create: `CloudSelfService.slnx`, `App.razor`, `Routes.razor`, `CloudSelfService.Tests/CloudSelfService.Tests.csproj`, `CloudSelfService.Tests/SmokeTests.cs`
- Modify: `CloudSelfService.csproj`, `Program.cs`, `_Imports.razor`, `Pages/Index.razor`, `.gitignore`
- Delete: `CloudSelfService.sln`, `Pages/_Host.cshtml`, `Pages/Error.cshtml`, `Pages/Error.cshtml.cs`, `Pages/AppServices.razor`, `Pages/ServiceHealth.razor`

**Interfaces:**
- Produces: `public partial class Program;` at the end of `Program.cs`, which Task 8's `WebApplicationFactory<Program>` relies on. `InternalsVisibleTo("CloudSelfService.Tests")`.

- [ ] **Step 1: Create the branch.** Run `git checkout -b feat/private-storage-access`. Add `*.msg` to `.gitignore`. The two Microsoft emails stay on disk, untracked.
- [ ] **Step 2: Create the test project** by copying RM's `ReleaseMetrics.Tests.csproj` (same package versions) with the root namespace and assembly name `CloudSelfService.Tests`. Add `SmokeTests.AppVersionService_NoEnvVar_ReturnsFallback`, asserting that `new AppVersionService(...)` yields a non-empty version string.
- [ ] **Step 3: Edit the csproj.** Remove the `MudBlazor` reference. Add the `Compile/Content/EmbeddedResource/None Remove="CloudSelfService.Tests\**"` block (as in RM's csproj lines 17–20) and `<InternalsVisibleTo Include="CloudSelfService.Tests" />`. Replace the `.sln` with `CloudSelfService.slnx` listing both projects.
- [ ] **Step 4: Move to the render model.** `App.razor` is RM's `App.razor` with `releaseMetrics*` keys renamed. Until Task 2 it links only the existing stylesheets. `Routes.razor` is a plain `<Router>` with `<RouteView>` for now; Task 5 adds authorization. Program changes: `AddRazorComponents().AddInteractiveServerComponents()`, `UseAntiforgery()`, `MapStaticAssets()`, `MapRazorComponents<App>().AddInteractiveServerRenderMode()`. Remove `AddRazorPages`/`MapBlazorHub`/`MapFallbackToPage`. Keep the security-header middleware unchanged. Set culture to `en-US`, as RM does.
- [ ] **Step 5: Trim the pages.** Delete the placeholder pages listed above. `Pages/Index.razor` becomes `@page "/"` with heading `Files`.
- [ ] **Step 6: Verify.** Run `dotnet build CloudSelfService.slnx` and expect 0 errors. Run `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` and expect 1 passed. Then `dotnet run`, and `curl -s -o NUL -w "%{http_code}" http://localhost:5300/` should return `200`.
- [ ] **Step 7: Commit:** "Move to Blazor Web App render model; add test project and slnx".

---

### Task 2: Sumi skin, shell and static guards

**Files:**
- Copy from RM: `wwwroot/css/theme.css` (verbatim), `wwwroot/css/app.css`, `Shared/{Icon.razor, AppMenu.razor(.css), ChangelogDialog.razor(.css), ReconnectModal.razor(.css,.js), LoginLayout.razor, MainLayout.razor(.css), NavMenu.razor(.css)}`, `Services/ChangelogDocument.cs`, `wwwroot/js/session-guard.js` (replaces the current copy)
- Create: `wwwroot/js/cloudselfservice.js` (RM's `rm.js` with the global renamed `window.cssp`, keys renamed)
- Tests, copied from RM and adapted: `ComponentStyleContractTests.cs`, `StylesheetIntegrityTests.cs`, `IconNameContractTests.cs`, `ChangelogDocumentTests.cs`
- Delete: `wwwroot/css/bootstrap/`, `wwwroot/css/open-iconic/`, `wwwroot/css/site.css`, `wwwroot/js/theme-manager.js`, `Shared/MainLayout.razor.css`'s old rules (replaced by RM's)
- Modify: `CloudSelfService.csproj` (add `Markdig`; `<EmbeddedResource Include="CHANGELOG.md" LogicalName="CloudSelfService.Docs.CHANGELOG.md" />`), `App.razor` (link `theme.css`, `app.css`, `CloudSelfService.styles.css` in that order; no chart palette)

**Interfaces:**
- Produces: `<Icon Name="…" />`. Later tasks may use `folder`, `file`, `upload`, `download`, `trash`, `pencil`, `plus`, `shield`, `users`, `database`, `history`, `help`, and must add any missing name to `Icon.razor`'s switch. NavMenu items are added by later tasks.

- [ ] **Step 1: Copy the four guard tests from RM** and repoint their paths and namespaces. Run them. Expected: FAIL, because `site.css`/Bootstrap classes are still referenced and theme.css is absent.
- [ ] **Step 2: Copy the shell files.** Rename the keys and the JS global, then strip RM-specific nav entries. NavMenu for now has one item, `Files` → `/`. The AppMenu shows the theme control and the changelog link. The header shows `advanced-logo-full.png`, "Cloud Self Service" and the `AppVersionService` chip.
- [ ] **Step 3: Delete MudBlazor, Bootstrap, Open Iconic, `site.css` and `theme-manager.js`.** Remove the `@using MudBlazor` lines.
- [ ] **Step 4: Prove each guard red.** Plant one defect per guard, watch the test fail, then revert. The four defects: an undefined `var(--nope)` in a `.razor.css`; a `block__element--modifier` class with no rule; a stylesheet missing one `*/`; `<Icon Name="nope" />`. Record the four red runs in the task report.
- [ ] **Step 5: Verify.** `dotnet test …` passes, all green. Run the app and check `/` in light and dark at 1440 px, 768 px and 375 px: the header, collapsible sidebar, changelog dialog and theme switch all work, and there are no console errors.
- [ ] **Step 6: Commit:** "Adopt Developer Standards sumi skin and shell; drop MudBlazor and Bootstrap".

---

### Task 3: Domain models and pure access helpers

**Files:**
- Create: `Models/Enums.cs`, `Models/Records.cs`, `Services/Access/AccessPolicy.cs`, `Services/Access/StoragePath.cs`, `Services/Transfers/ChunkMath.cs`, `Services/Transfers/RangeHeader.cs`, `Services/Transfers/ContentDispositionHeader.cs`
- Test: `AccessPolicyTests.cs`, `StoragePathTests.cs`, `ChunkMathTests.cs`, `RangeHeaderTests.cs`, `ContentDispositionHeaderTests.cs`

**Interfaces (produced, used by every later task):**

```csharp
public enum UserKind { Staff, Customer }
public enum StaffRole { None = 0, Staff = 1, StaffAdmin = 2, Owner = 3 }
public enum AccessLevel { None = 0, Read = 1, Contribute = 2, Full = 3 }
public enum StorageKind { FileShare, BlobContainer }
public enum CustomerCloud { Commercial, AzureGov }
public enum CrossTenantStatus { NotRequired, Pending, Complete }
public enum StorageOperation { List, Download, Upload, Overwrite, CreateFolder, Move, Delete }
public enum AuditResult { Success, Denied, Failed }

public sealed record AppUser(Guid Id, UserKind Kind, Guid? CustomerId, string Upn, string DisplayName,
    string? EntraObjectId, StaffRole StaffRole, bool Active, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);
public sealed record Customer(Guid Id, string Name, bool Active, string Notes, string? HomeTenantId,
    CustomerCloud Cloud, CrossTenantStatus CrossTenantStatus);
public sealed record StorageTarget(Guid Id, Guid? CustomerId, string DisplayName, string SubscriptionId,
    string ResourceGroup, string AccountName, StorageKind Kind, string ContainerName, string EndpointSuffix, bool Active);
public sealed record Grant(Guid CustomerId, Guid TargetId, AccessLevel Level, string GrantedBy, DateTimeOffset GrantedAt);
public sealed record AuditEvent(Guid Id, DateTimeOffset At, Guid? UserId, string UserDisplay, Guid? CustomerId,
    Guid? TargetId, string Operation, string Path, long? Bytes, AuditResult Result, string Error, string SourceIp, string CorrelationId);
public sealed record StorageItem(string Name, string Path, bool IsFolder, long? Size, DateTimeOffset? LastModified);
public sealed record StorageListing(IReadOnlyList<StorageItem> Items, string? ContinuationToken);
public sealed record AuditContext(string SourceIp, string CorrelationId);

public static class AccessPolicy {
    public static AccessLevel EffectiveLevel(AppUser? user, Customer? customer, StorageTarget? target, Grant? grant);
    public static AccessLevel Required(StorageOperation op);
    public static bool Allows(AccessLevel have, StorageOperation op) => have >= Required(op);
}
public static class StoragePath {
    public static bool TryNormalizePath(string? raw, out string normalized, out string? error); // "" = root
    public static bool TryValidateName(string? name, out string? error);                      // one segment
    public static string Combine(string folder, string name);
    public static string Parent(string path);
    public static string Leaf(string path);
}
public static class ChunkMath {
    public const int ChunkSize = 4 * 1024 * 1024;
    public static int ChunkCount(long size);                      // 0 for size 0
    public static long ExpectedLength(long size, int index);      // throws on bad index
    public static string BlockId(Guid sessionId, int index);      // base64 of $"{sessionId:N}-{index:D6}", fixed width
}
public static class RangeHeader { public static bool TryParse(string? header, long length, out long start, out long end); } // single range only
public static class ContentDispositionHeader { public static string Attachment(string fileName); }
```

**Rules the tests pin:**
- `EffectiveLevel` returns `None` when:
  - the user is null or inactive;
  - the target is null or inactive.
- Staff map by role: `None`→None, `Staff`→Contribute, `StaffAdmin` and `Owner`→Full. Staff access ignores customers and grants.
- A customer user gets the grant's level only when all of these hold:
  - the user has a `CustomerId`;
  - the customer is non-null, active, and has that Id;
  - `target.CustomerId == user.CustomerId`;
  - `grant.CustomerId == user.CustomerId` and `grant.TargetId == target.Id`.
- Anything else gives `None`.
- `Required`: List and Download → Read; Upload, Overwrite, CreateFolder and Move → Contribute; Delete → Full.
- `TryNormalizePath`:
  - Trims leading and trailing `/` and collapses `//`.
  - Rejects `\`, `.` or `..` segments, control characters (U+0000–U+001F, U+007F), the characters `" : | < > * ?`, a segment ending in `.` or a space, a segment longer than 255 characters, and a total longer than 1,024 characters.
  - Accepts spaces, `#`, `%`, `+`, `&`, `'` and non-ASCII characters.
- `TryValidateName` applies the same rules to a single segment and also rejects `/` and an empty name.
- `Attachment(name)` returns `attachment; filename="<ASCII fallback, non-ASCII→_ and quotes stripped>"; filename*=UTF-8''<RFC 5987 percent-encoded>`.

- [ ] **Step 1: Write the failing tests.** At minimum these, each with concrete values:
  - **AccessPolicy:** a role×level×op theory covering every `StorageOperation` for each StaffRole and AccessLevel, plus:
    - `EffectiveLevel_CustomerUserOtherCustomersTarget_None`
    - `EffectiveLevel_GrantForDifferentTarget_None`
    - `EffectiveLevel_InactiveCustomer_None`
    - `EffectiveLevel_InactiveTarget_None`
    - `EffectiveLevel_StaffRoleNone_None`
    - `Required_Delete_IsFull`
  - **StoragePath:**
    - `TryNormalizePath_DotDot_Rejected` with inputs `"../x"`, `"a/../../b"`, `"a/./b"`
    - `TryNormalizePath_Backslash_Rejected` with `"a\\b"`
    - `TryNormalizePath_LeadingTrailingSlashes_Trimmed` (`"/a//b/"` → `"a/b"`)
    - `TryNormalizePath_SpecialCharacters_Accepted` (`"Q3 #1 50% a+b & c's/Ünïcode.txt"` unchanged)
    - `TryNormalizePath_ControlCharacter_Rejected` (`"a\u0001b"`)
    - `TryNormalizePath_TrailingDotSegment_Rejected` (`"a./b"`)
    - `TryValidateName_ContainsSlash_Rejected`
    - `Parent_RootChild_IsEmpty`
  - **ChunkMath:**
    - `ChunkCount_Zero_IsZero`
    - `ChunkCount_ExactMultiple` (8 MiB → 2)
    - `ChunkCount_Remainder` (8 MiB + 1 → 3)
    - `ExpectedLength_LastChunk_IsRemainder` (size 8 MiB+1, index 2 → 1)
    - `ExpectedLength_IndexOutOfRange_Throws`
    - `BlockId_AllIndicesSameLength` (indices 0 and 999,999)
  - **RangeHeader:**
    - `TryParse_BytesStartEnd` (`"bytes=0-99"`, len 1000 → 0, 99)
    - `TryParse_OpenEnded` (`"bytes=500-"` → 500, 999)
    - `TryParse_Suffix` (`"bytes=-100"` → 900, 999)
    - `TryParse_MultiRange_False`
    - `TryParse_StartBeyondLength_False`
    - `TryParse_Null_False`
  - **ContentDispositionHeader:**
    - `Attachment_NonAscii_HasFilenameStar` (`"Ünï #1.txt"` → contains `filename*=UTF-8''%C3%9Cn%C3%AF%20%231.txt`)
    - `Attachment_QuoteInName_Stripped`
- [ ] **Step 2: Run the tests and expect FAIL.** They won't compile, because the types don't exist yet.
- [ ] **Step 3: Implement the types above** in the listed files.
- [ ] **Step 4: Run the tests and expect PASS.**
- [ ] **Step 5: Commit:** "Add domain model and pure access, path, chunk and header helpers".

---

### Task 4: PostgreSQL schema, stores and audit

**Files:**
- Create: `Services/Data/PgsqlContext.cs`, `Services/Data/Stores.cs` (interfaces), `Services/Data/PgStores.cs` (Npgsql implementations), `Services/Data/InMemoryStores.cs` (used by the app when no DB is configured, and by the tests), `Services/AuditService.cs`
- Modify: `CloudSelfService.csproj` (add `Npgsql`), `Program.cs` (register services, call `EnsureSchemaAsync` at startup), `appsettings.json`, `appsettings.Development.json.sample`
- Test: `InMemoryStoresTests.cs`, `PgSchemaTests.cs`

**Interfaces:**
- Consumes: the Task 3 records and enums.
- Produces:

```csharp
public interface IUserStore {
    Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default);
    Task<AppUser?> FindByObjectIdAsync(string oid, CancellationToken ct = default);
    Task<AppUser?> FindByUpnAsync(string upn, CancellationToken ct = default);          // case-insensitive
    Task<IReadOnlyList<AppUser>> ListAsync(UserKind? kind = null, Guid? customerId = null, CancellationToken ct = default);
    Task UpsertAsync(AppUser user, CancellationToken ct = default);
}
public interface ICustomerStore {
    Task<Customer?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(Customer c, CancellationToken ct = default);
}
public interface ITargetStore {
    Task<StorageTarget?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<StorageTarget>> ListAsync(Guid? customerId = null, CancellationToken ct = default);
    Task UpsertAsync(StorageTarget t, CancellationToken ct = default);
}
public interface IGrantStore {
    Task<Grant?> GetAsync(Guid customerId, Guid targetId, CancellationToken ct = default);
    Task<IReadOnlyList<Grant>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task SetAsync(Grant g, CancellationToken ct = default);                // upsert
    Task RemoveAsync(Guid customerId, Guid targetId, CancellationToken ct = default);
}
public interface IAuditStore {
    Task AppendAsync(AuditEvent e, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery q, CancellationToken ct = default);
}
public sealed record AuditQuery(Guid? UserId = null, Guid? CustomerId = null, Guid? TargetId = null,
    string? Operation = null, AuditResult? Result = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 500);
public sealed class AuditService { // wraps IAuditStore; logs and swallows store failures so audit never breaks a user action
    public Task RecordAsync(AuditEvent e, CancellationToken ct = default);
}
```

**Schema** (`EnsureSchemaAsync`, idempotent, `CREATE … IF NOT EXISTS`). Column names are snake_case, and enums are stored as `TEXT` holding their names.
- `customers(id uuid pk, name text not null unique, active bool, notes text, home_tenant_id text, cloud text, cross_tenant_status text)`
- `users(id uuid pk, kind text not null, customer_id uuid null references customers, upn text not null, display_name text, entra_object_id text null unique, staff_role text not null default 'None', active bool, created_at timestamptz, last_sign_in_at timestamptz)`, plus a unique index on `lower(upn)`
- `storage_targets(id uuid pk, customer_id uuid null references customers, display_name text, subscription_id text, resource_group text, account_name text, kind text, container_name text, endpoint_suffix text not null default 'core.windows.net', active bool)`, plus `unique(account_name, kind, container_name)`
- `grants(customer_id uuid references customers, target_id uuid references storage_targets, level text, granted_by text, granted_at timestamptz, primary key(customer_id, target_id))`. A constraint trigger `grants_owner_check` raises unless `storage_targets.customer_id = NEW.customer_id`, so a grant can go only to the customer that owns the target.
- `audit_events(id uuid pk, at timestamptz not null, user_id uuid, user_display text, customer_id uuid, target_id uuid, operation text, path text, bytes bigint, result text, error text, source_ip text, correlation_id text)`, indexed on `(at desc)`. The store's SQL contains no `UPDATE` or `DELETE` on this table.
- `upload_sessions(id uuid pk, user_id uuid, target_id uuid, final_path text, staging_name text, size bigint, chunk_size int, overwrite bool, created_at timestamptz, expires_at timestamptz, status text)`
- `upload_chunks(session_id uuid references upload_sessions on delete cascade, idx int, primary key(session_id, idx))`

**Config.**
- `appsettings.json` gets these sections:
  - `"Auth": { "Provider": "Entra", "IdleTimeoutMinutes": 60, "IdleWarningSeconds": 120 }`
  - `"Entra": { "Instance": "https://login.microsoftonline.com", "TenantId": "", "ClientId": "", "StaffEmailDomain": "advancedutility.com" }`
  - `"Authorization": { "Owners": "" }`
  - `"Transfers": { "MaxUploadBytes": 53687091200, "ParallelChunks": 4, "SessionHours": 24 }`
- The sample file sets these, with every secret left blank:
  - `Auth:Provider` = `Dev`
  - `ConnectionStrings:Default` pointing at `cloudselfservice_dev` on `auseusdevpgsql.infinitycis.com` with `SSL Mode=Require`
  - `Kestrel:Endpoints:Http:Url` = `http://localhost:5300`
- With no `ConnectionStrings:Default`, the in-memory stores are registered instead.

**In-memory grant rule.** The in-memory `IGrantStore.SetAsync` enforces the same owner rule as the trigger, throwing `InvalidOperationException` on a mismatch. It needs `ITargetStore` injected for that.

- [ ] **Step 1: Write the failing tests.**
  - `InMemoryStoresTests`:
    - `GrantStore_Set_TargetOwnedByOtherCustomer_Throws`
    - `UserStore_FindByUpn_CaseInsensitive`
    - `UserStore_FindByObjectId_ReturnsBound`
    - `AuditStore_Query_FiltersByCustomerAndResult`
  - `PgSchemaTests.SchemaSql_HasOwnerTrigger` asserts the schema SQL constant contains `grants_owner_check`.
  - `PgSchemaTests.AuditStoreSql_NoUpdateOrDelete` asserts that none of `PgAuditStore`'s SQL constants contain `UPDATE` or `DELETE`.
- [ ] **Step 2: Run them; expect FAIL.**
- [ ] **Step 3: Implement the stores, schema, `AuditService` and DI registration.** PG stores use parameterised commands only.
- [ ] **Step 4: Run them; expect PASS.**
  - Then `dotnet run`. If VPN and the `_dev` database are available, the log should show the schema created.
  - If not, write in the task report that this check moves to Task 13.
- [ ] **Step 5: Commit:** "Add PostgreSQL schema, stores and append-only audit".

---

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

### Task 6: Storage backends (Files + Blob)

**Files:**
- Create: `Services/Storage/IStorageBackend.cs`, `Services/Storage/StorageClientFactory.cs`, `Services/Storage/FileShareBackend.cs`, `Services/Storage/BlobBackend.cs`, `Services/Storage/StorageErrorMapper.cs`, `Services/Storage/FakeStorageBackend.cs`. The fake lives in the app, not the test project, because Task 10 registers it for local UI work. It is only registered when `Storage:UseFake=true` **and** `IsDevelopment()`, and the startup guard `DevSignIn.AssertSafe` also throws when `Storage:UseFake=true` outside Development.
- Modify: `CloudSelfService.csproj` (add `Azure.Identity`, `Azure.Storage.Files.Shares`, `Azure.Storage.Blobs`), `Program.cs`
- Test: `StorageErrorMapperTests.cs`, `StorageClientFactoryTests.cs`, `FakeStorageBackendTests.cs`

**Interfaces:**

```csharp
public interface IStorageBackend {
    Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct);
    Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct);           // null = not found
    Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct);
    Task CreateFolderAsync(StorageTarget t, string path, CancellationToken ct);
    Task MoveAsync(StorageTarget t, string from, string to, bool isFolder, CancellationToken ct);
    Task DeleteAsync(StorageTarget t, string path, bool isFolder, CancellationToken ct);           // recursive for folders
    Task BeginUploadAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct);
    Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset, Stream data, long length, byte[] md5, CancellationToken ct);
    Task CompleteUploadAsync(StorageTarget t, string stagingPath, string finalPath, IReadOnlyList<string> blockIds, bool overwrite, CancellationToken ct);
    Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct);
    Task ProbeAsync(StorageTarget t, CancellationToken ct);
}
public interface IStorageBackendResolver { IStorageBackend For(StorageTarget t); }   // by t.Kind
public sealed class StorageClientFactory {   // singleton; caches clients per (account, kind, suffix); one shared DefaultAzureCredential
    public ShareClient Share(StorageTarget t);
    public BlobContainerClient Container(StorageTarget t);
    public static Uri ServiceUri(StorageTarget t);              // https://{account}.{file|blob}.{EndpointSuffix}/
    internal static ShareClientOptions BuildShareOptions();     // ShareTokenIntent = Backup
}
public enum StorageFailureKind { NotFound, AlreadyExists, NotAuthorised, Unreachable, Conflict, Other }
public sealed class StorageFailureException : Exception {
    public StorageFailureException(StorageFailureKind kind, string userMessage, Exception? inner = null);
    public StorageFailureKind Kind { get; }
}
public static class StorageErrorMapper { public static StorageFailureException Map(Exception ex); }
```

**Behaviour:**
- **Files:**
  - List uses `GetFilesAndDirectoriesAsync().AsPages(continuation, pageSize)` and returns one page.
  - Create folder creates any missing parents.
  - Move uses `RenameAsync` with `ReplaceIfExists=false`.
  - Deleting a folder is recursive: files first, then subfolders deepest-first.
  - Begin upload calls `CreateAsync(maxSize: size)` at the staging path.
  - Each chunk goes through `UploadRangeAsync(new HttpRange(offset,length), data, new ShareFileUploadRangeOptions { TransactionalContentHash = md5 })`.
  - Complete renames staging → final with `ReplaceIfExists = overwrite`.
  - Abort deletes the staging file and ignores NotFound.
- **Blob:**
  - List uses `GetBlobsByHierarchyAsync(delimiter:"/", prefix: folder==""?null:folder+"/").AsPages(continuation, pageSize)`.
  - Create folder uploads a zero-byte marker `{path}/.folder`. Listings hide `.folder` but keep its folder visible. (Spec §8 leaves this open; this is my call.)
  - Move copies each blob under the prefix (or the single blob) with `SyncCopyFromUriAsync` (managed-identity bearer; `StartCopyFromUri` + poll above 256 MiB), then deletes the source.
  - Deleting a folder deletes every blob under the prefix.
  - Begin upload does nothing, and staging is unused.
  - Each chunk is staged with `StageBlockAsync(blockId, data, transactional MD5)` on the **final** name. Uncommitted blocks are invisible, so nothing shows until commit.
  - Complete calls `CommitBlockListAsync`, adding the condition `IfNoneMatch = ETag.All` when `!overwrite`.
  - Abort does nothing.
- **Error mapping:**
  - 404 → NotFound.
  - 409 with code `ResourceAlreadyExists`/`BlobAlreadyExists`, or 412 → AlreadyExists.
  - 403, or `AuthenticationFailedException` → NotAuthorised: "The portal is not authorised on this storage account. Contact Advanced support."
  - Status 0, `HttpRequestException`, `SocketException`, or a timeout not caused by caller cancellation → Unreachable: "Storage is temporarily unreachable. Please try again shortly."
  - An MD5 mismatch (400 `Md5Mismatch`) → Conflict: "A piece of the upload was corrupted in transit and will be retried."
  - Anything else → Other: "Something went wrong talking to storage."
  - Exception text never appears in user messages.
- **`FakeStorageBackend`:**
  - An in-memory tree per target id that implements every method with the same semantics, including MD5 verification and the `.folder` hiding.
  - Two switches, `ThrowUnreachable` and `ThrowNotAuthorised`, throw the mapped exceptions.
  - It records calls so tests can assert "no bytes written".

- [ ] **Step 1: Write the failing tests.**
  - `StorageErrorMapperTests`:
    - one test per mapping rule, built from `new RequestFailedException(status, msg, errorCode, null)`
    - `Map_UserMessageNeverContainsExceptionText`
  - `StorageClientFactoryTests`:
    - `ServiceUri_GovSuffix_UsesSuffix` (`core.usgovcloudapi.net` → `https://acct.file.core.usgovcloudapi.net/`)
    - `BuildShareOptions_SetsBackupIntent`
  - `FakeStorageBackendTests`:
    - `WriteChunk_Md5Mismatch_ThrowsConflict`
    - `Complete_OverwriteFalse_ExistingFile_AlreadyExists`
    - `List_FolderMarkerHidden`
    - `Delete_Folder_Recursive`
    - `List_PageSizeOne_ReturnsContinuation`
  - `DevSignInGuardTests.AssertSafe_UseFakeStorageInProduction_Throws` (added to the Task 5 test file).
- [ ] **Step 2: Run them; expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them; expect PASS.**
- [ ] **Step 5: Add a live check.** Add `LiveStorageTests.Live_FilesAndBlob_RoundTrip` with `[Fact(Skip = "live: needs VPN + dev test account")]`. It runs list → create folder → 9 MiB+1 upload in 3 chunks → download → SHA-256 equal → move → delete, against both kinds, using targets read from env vars `CSSP_LIVE_FILES_ACCOUNT`/`CSSP_LIVE_SHARE` and `CSSP_LIVE_BLOB_ACCOUNT`/`CSSP_LIVE_CONTAINER`. Run it unskipped only once Task 13's account exists, and record the result in the task report.
- [ ] **Step 6: Commit:** "Add Azure Files and Blob backends over managed identity with error mapping".

---

### Task 7: StorageAccessService — the single gate

**Files:**
- Create: `Services/Access/StorageAccessService.cs`
- Modify: `Program.cs` (register it as scoped, and register `IStorageBackendResolver`)
- Test: `StorageAccessServiceTests.cs`

**Interfaces:**
- Consumes: `AccessPolicy`, `StoragePath` (Task 3); the stores and `AuditService` (Task 4); `IStorageBackendResolver` and `StorageFailureException` (Task 6).
- Produces:

```csharp
public enum AccessOutcome { Ok, Denied, NotFound, Invalid, AlreadyExists, Unavailable }
public sealed record AccessResult(AccessOutcome Outcome, string? Message = null);
public sealed record AccessResult<T>(AccessOutcome Outcome, T? Value, string? Message = null);
public sealed record ReachableTarget(StorageTarget Target, Customer? Customer, AccessLevel Level);

public sealed class StorageAccessService {
    public Task<IReadOnlyList<ReachableTarget>> ListReachableTargetsAsync(Guid userId, CancellationToken ct = default);
    public Task<AccessLevel> GetLevelAsync(Guid userId, Guid targetId, CancellationToken ct = default);
    public Task<AccessResult<StorageListing>> ListAsync(Guid userId, Guid targetId, string folder, string? continuation, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult<(StorageTarget Target, StorageItem Item)>> AuthorizeDownloadAsync(Guid userId, Guid targetId, string path, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> CreateFolderAsync(Guid userId, Guid targetId, string parent, string name, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> MoveAsync(Guid userId, Guid targetId, string from, string to, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> DeleteAsync(Guid userId, Guid targetId, string path, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult<StorageTarget>> AuthorizeAsync(Guid userId, Guid targetId, StorageOperation op, string path, AuditContext ctx, CancellationToken ct = default); // used by Task 9
    public const int PageSize = 200;
}
```

**Rules the tests pin.**

Every public method that touches a target follows the same sequence:
1. Reload the user, customer, target and grant from the stores. Nothing is cached between calls.
2. Normalise each path with `StoragePath`. An invalid path returns `Invalid` with the path error and writes no audit row.
3. Compute the effective level. If it isn't enough for the operation, write an audit row with `Denied` and return `Denied`. The backend is never called.
4. Otherwise call the backend.
5. Map a `StorageFailureException` to an outcome: NotFound→`NotFound`, AlreadyExists→`AlreadyExists`, NotAuthorised/Unreachable/Other→`Unavailable` (with the mapper's message), Conflict→`Unavailable`.
6. Audit `Success` or `Failed` with the exception's user message.

`Denied` returns the message "You don't have access to do that." To a customer, a target that exists but isn't theirs looks exactly like one that doesn't exist: the result is `Denied`, never `NotFound`, so they can't probe for other customers' targets.

Operation-specific behaviour:
- **Listing:** passes `PageSize` and the continuation token through. It is **not** audited per page; only a denied list is audited.
- **`MoveAsync`:** is a rename when the parent of `from` equals the parent of `to`, and a move otherwise. Both need Contribute and both are audited as `Move`. `from` and `to` must both be non-root. Moving a folder into its own subtree returns `Invalid`.
- **`DeleteAsync`:** the root path returns `Invalid`.
- **`ListReachableTargetsAsync`:**
  - Staff with role ≥ Staff see every active target, each with its customer.
  - A customer user sees only targets of their own active customer that carry a grant with level ≥ Read.
  - Results are ordered by customer name, then target display name.

- [ ] **Step 1: Write the failing tests.**

Use the in-memory stores and `FakeStorageBackend`, with a fixture seeding:
- customers A and B;
- targets TA (owned by A, a FileShare) and TB (owned by B, a BlobContainer);
- users: a Read user and a Full user at A, one user at B, and staff with role None, Staff and StaffAdmin.

Tests:
- `ListReachable_CustomerUser_OnlyOwnGrantedTargets`
- `ListReachable_Staff_AllActiveTargets`
- `ListReachable_StaffRoleNone_Empty`
- `List_CustomerAOnTargetB_Denied_AndAudited_AndBackendNotCalled`
- `Delete_ReadUser_Denied`
- `Delete_FullUser_Succeeds_AndAuditsSuccess`
- `Delete_Root_Invalid`
- `CreateFolder_InvalidName_Invalid_NoAudit`
- `Move_IntoOwnSubtree_Invalid`
- `Move_SameParent_AuditedAsMove`
- `List_GrantRevokedBetweenCalls_SecondCallDenied`: remove the grant in the store between two calls.
- `List_UserDeactivatedBetweenCalls_SecondCallDenied`
- `List_BackendUnavailable_ReturnsUnavailableWithReadableMessage` (Review Focus 4)
- `List_PassesContinuationToken` (Review Focus 5): the fake backend has 250 items; the first call returns 200 plus a token, and the second call with that token returns the remaining 50.
- `AuthorizeDownload_PathTraversal_Invalid`: `"../TB/secret.txt"`.
- [ ] **Step 2: Run them. Expect FAIL.**
- [ ] **Step 3: Implement `StorageAccessService`.**
- [ ] **Step 4: Run them. Expect PASS.**
- [ ] **Step 5: Invoke the `unit-test-reviewer` agent** on `Services/Access/` and add any gaps it finds.
- [ ] **Step 6: Commit:** "Add StorageAccessService as the single authorised gate to storage".

---

### Task 8: Download endpoint

**Files:**
- Create: `Endpoints/DownloadEndpoints.cs`, `CloudSelfService.Tests/Infrastructure/TestAppFactory.cs`
- Modify: `Program.cs` (call `app.MapDownloadEndpoints()`), `CloudSelfService.Tests.csproj` (add `Microsoft.AspNetCore.Mvc.Testing`)
- Test: `DownloadEndpointTests.cs`

**Interfaces:**
- Consumes: `StorageAccessService.AuthorizeDownloadAsync`, `IStorageBackendResolver`, `RangeHeader`, `ContentDispositionHeader`.
- Produces:
  - `GET /api/files/{targetId:guid}/download?path=…`, which requires authorization.
  - `TestAppFactory : WebApplicationFactory<Program>`. It runs in the Development environment with `Auth:Provider=Dev` and the in-memory stores, swaps `FakeStorageBackend` in as the backend, and exposes `CreateClientAs(string devPrincipal)`. That helper calls `/dev/signin?as=…` and keeps the cookie. Task 9 reuses it.

**Behaviour:**
- `AuditContext.SourceIp` is `HttpContext.Connection.RemoteIpAddress`. `CorrelationId` is `HttpContext.TraceIdentifier`.
- Status codes: `Denied`→403, `NotFound`→404, `Invalid`→400, `Unavailable`→503. Each returns plain text with the message. A folder path returns 400.
- **Success headers:**
  - `Content-Type: application/octet-stream`
  - `Content-Disposition` from `ContentDispositionHeader.Attachment(Leaf(path))`
  - `X-Content-Type-Options: nosniff`
  - `Accept-Ranges: bytes`
  - `Content-Length`
- **Range:** a valid single `Range` returns 206 with `Content-Range: bytes start-end/total`. A syntactically valid range that can't be satisfied returns 416 with `Content-Range: bytes */total`. An unparseable or multi-range header is ignored, and the full file is sent with 200.
- **Streaming:** copy the stream from `OpenReadAsync` to `Response.Body` in 80 KiB chunks. The file is never buffered whole.
- **Audit:** write one `Download` row with `Success` once the copy finishes, or `Failed` if the client disconnects mid-stream (operation cancelled). `Bytes` is the number actually written.

- [ ] **Step 1: Write the failing tests.**
- `Download_CustomerOwnFile_200_AttachmentOctetStream`
- `Download_OtherCustomersTarget_403`
- `Download_Anonymous_RedirectsOrUnauthorized`: this asserts no 200 and no file bytes.
- `Download_Range_206_WithContentRange`: with a 1000-byte file and `bytes=100-199`, expect 100 bytes and `Content-Range: bytes 100-199/1000`.
- `Download_RangeBeyondEnd_416`
- `Download_NameWithSpecialCharacters_HeaderEncoded` (Review Focus 3): the file is `"Q3 #1 50% a+b & c's Ünï.txt"`, and the header contains a `filename*=UTF-8''` value that decodes back to the same name.
- `Download_Folder_400`
- `Download_BackendUnreachable_503_ReadableMessage`
- [ ] **Step 2: Run them. Expect FAIL.**
- [ ] **Step 3: Implement `DownloadEndpoints` and `TestAppFactory`.**
- [ ] **Step 4: Run them. Expect PASS.**
- [ ] **Step 5: Invoke `security-auditor`** on the endpoint and fix what it finds.
- [ ] **Step 6: Commit:** "Add streaming, range-capable download endpoint".

---

### Task 9: Chunked, resumable upload API

**Files:**
- Create: `Services/Transfers/UploadSessionService.cs`, `Services/Transfers/UploadCleanupService.cs` (`BackgroundService`), `Services/Data/UploadSessionStore.cs` (interface, PG implementation and in-memory implementation), `Endpoints/UploadEndpoints.cs`
- Modify: `Program.cs`, `Endpoints/AuthEndpoints.cs` (add `GET /api/antiforgery`)
- Test: `UploadSessionServiceTests.cs`, `UploadEndpointTests.cs`

**Interfaces:**
- Consumes: `StorageAccessService.AuthorizeAsync`, `ChunkMath`, `IStorageBackendResolver`, `AuditService`, `TestAppFactory`.
- Produces:

```csharp
public sealed record UploadSession(Guid Id, Guid UserId, Guid TargetId, string FinalPath, string StagingPath,
    long Size, int ChunkSize, bool Overwrite, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status); // Status: Open|Completed|Aborted
public interface IUploadSessionStore {
    Task CreateAsync(UploadSession s, CancellationToken ct = default);
    Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default);
    Task MarkChunkAsync(Guid id, int index, CancellationToken ct = default);          // idempotent
    Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default);
    Task SetStatusAsync(Guid id, string status, CancellationToken ct = default);
    Task<IReadOnlyList<UploadSession>> ListExpiredOpenAsync(DateTimeOffset now, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
public sealed record BeginUploadRequest(Guid TargetId, string Folder, string FileName, long Size, bool Overwrite);
public sealed record BeginUploadResponse(Guid SessionId, int ChunkSize, int ChunkCount, int ParallelChunks, IReadOnlyList<int> Received);
public sealed class UploadSessionService {
    public Task<AccessResult<BeginUploadResponse>> BeginAsync(Guid userId, BeginUploadRequest req, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult<IReadOnlyList<int>>> StatusAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    public Task<AccessResult> PutChunkAsync(Guid userId, Guid sessionId, int index, Stream body, long? contentLength, string? contentMd5Base64, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> CompleteAsync(Guid userId, Guid sessionId, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> AbortAsync(Guid userId, Guid sessionId, AuditContext ctx, CancellationToken ct = default);
}
```

**Endpoints.** All require authorization and validate the antiforgery token (sent in the `RequestVerificationToken` header, fetched from `GET /api/antiforgery`). All return JSON. Status codes map as in Task 8, plus `AlreadyExists`→409.
- `POST /api/uploads` takes a `BeginUploadRequest` body. Returns 200 with a `BeginUploadResponse`.
- `GET /api/uploads/{id:guid}` returns `{ received: int[] }`.
- `PUT /api/uploads/{id:guid}/chunks/{index:int}` takes a raw body and a `Content-MD5` header. Returns 204.
- `POST /api/uploads/{id:guid}/complete` returns 204.
- `DELETE /api/uploads/{id:guid}` returns 204.
- `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` is set to `ChunkSize` on the chunk route only. The Kestrel default stays in force everywhere else.

**Rules the tests pin:**
- **Begin:**
  - Validates `FileName` with `TryValidateName` and `Folder` with `TryNormalizePath`.
  - `Size` must be ≥ 0 and ≤ `Transfers:MaxUploadBytes`; otherwise `Invalid`, with the message "File is larger than the {N} GB limit."
  - The final path must not exist unless `Overwrite` is true; otherwise `AlreadyExists`.
  - The operation checked is `Upload`, or `Overwrite` when the target exists and `Overwrite` is true.
  - Files staging path: `{folder}/.{name}.cssupload-{sessionId:N}`. Blob: the staging path equals the final path.
  - Calls `BeginUploadAsync` and returns `ParallelChunks` from config.
  - **Resume lookup.** First look for an open, unexpired session owned by the same user with the same target, final path and size. If one exists, return it with its `Received` list instead of creating a new one. That lookup is what makes a page reload resumable.
- **PutChunk:**
  - Loads the session. It must belong to `userId`, be `Open` and be unexpired; otherwise `NotFound`.
  - **Calls `AuthorizeAsync` again on every chunk** (Review Focus 2).
  - `index` must be in range, `contentLength` must equal `ChunkMath.ExpectedLength`, and `Content-MD5` must be present as 16 bytes of base64. Any failure is `Invalid`.
  - Writes through the backend at `offset = index * ChunkSize` with `BlockId(sessionId, index)`, then calls `MarkChunkAsync`.
  - Chunks are not audited individually.
- **Complete:**
  - Every index in `0..ChunkCount-1` must be received; otherwise `Invalid` ("Upload is missing N pieces.").
  - Re-authorises, calls `CompleteUploadAsync` with block ids in index order, and sets the status to `Completed`.
  - Audits one `Upload` row with `Bytes = Size`.
  - A zero-byte file has `ChunkCount` 0 and completes immediately (Review Focus 1). For blobs, commit an empty block list.
- **Abort:** calls `AbortUploadAsync`, sets the status to `Aborted`, and audits `Upload` as `Failed` with the error "Cancelled by user".
- **`UploadCleanupService`:**
  - Every 30 minutes, for `ListExpiredOpenAsync(now)`: call `AbortUploadAsync` (ignoring errors), set the status to `Aborted`, then delete the session rows 7 days after expiry.
  - Expose `internal Task SweepAsync(DateTimeOffset now, CancellationToken ct)` for tests.

- [ ] **Step 1: Write the failing tests.**

`UploadSessionServiceTests` uses in-memory stores and the fake backend:
- `Begin_ReadUser_Denied`
- `Begin_TooLarge_Invalid`
- `Begin_ExistingFileNoOverwrite_AlreadyExists`
- `Begin_ExistingFileOverwrite_RequiresContribute_Ok`
- `Begin_SameFileAgain_ReturnsSameSessionWithReceived`
- `PutChunk_WrongLength_Invalid`
- `PutChunk_MissingMd5_Invalid`
- `PutChunk_BadMd5_UnavailableAndChunkNotMarked`
- `PutChunk_OtherUsersSession_NotFound`
- `PutChunk_GrantRevokedAfterBegin_Denied` (Review Focus 2). Assert the fake backend recorded no write.
- `PutChunk_SameIndexTwice_Idempotent`
- `Complete_MissingChunk_Invalid`
- `Complete_AllChunks_FileExistsWithExactBytes`: 9 MiB + 1 byte, 3 chunks sent out of order (2, 0, 1); assert the SHA-256 matches.
- `Complete_ZeroByteFile_CreatesEmptyFile` (Review Focus 1)
- `Complete_AuditsSingleUploadRow`
- `Abort_RemovesStaging_AuditsFailed`
- `Sweep_ExpiredOpenSession_Aborted`

`UploadEndpointTests` goes through `TestAppFactory`:
- `Upload_EndToEnd_CustomerContribute_Succeeds`
- `PutChunk_WithoutAntiforgery_400`
- `PutChunk_BodyLargerThanChunk_413`
- `Begin_Anonymous_NotAuthorised`
- [ ] **Step 2: Run them. Expect FAIL.**
- [ ] **Step 3: Implement the services, stores, endpoints and background service.**
- [ ] **Step 4: Run them. Expect PASS.**
- [ ] **Step 5: Invoke `security-auditor`** on the upload surface (antiforgery, body limits, session ownership, path handling) and fix its findings. Then invoke `unit-test-reviewer` on `Services/Transfers/`.
- [ ] **Step 6: Commit:** "Add chunked, resumable, MD5-verified upload API with expiry cleanup".

---

### Task 10: Files home and browser pages

**Files:**
- Create: `Pages/Files/Browse.razor` (+ `.razor.css`), `Shared/UploadPanel.razor` (+ `.razor.css`), `Shared/ConfirmDialog.razor`, `Shared/NameDialog.razor`, `wwwroot/js/uploader.js`, `Pages/Help.razor`
- Modify: `Pages/Index.razor` (+ `.razor.css`), `Shared/NavMenu.razor` (Files, Help), `Shared/Icon.razor` (add icons), `App.razor` (load `uploader.js`)
- Test: `UploaderScriptTests.cs` (static), `BrowseRouteTests.cs`

**Interfaces:**
- Consumes: `CurrentUser`, `StorageAccessService` (list, create folder, move, delete, reachable targets, level), and the Task 8/9 endpoints.
- Produces: `window.csspUploader.start(dotnetRef, targetId, folder, inputElementId, overwrite)`. It calls back with `[JSInvokable] OnProgress(string fileName, long sent, long total)`, `OnFileDone(string fileName, bool ok, string? message)` and `OnConflict(string fileName)`.

**Behaviour:**
- **`/` (Files home).**
  - Customers: one card per reachable target, showing display name, kind badge (`File share` / `Blob container`) and level badge (`Read` / `Contribute` / `Full`).
  - Staff: the same cards grouped under customer headings, with internal targets under "Advanced internal". A filter box narrows by customer or target name.
  - No reachable targets: the empty state reads "No storage has been shared with you yet. Contact Advanced support if you expected to see something here."
- **`/files/{TargetId:guid}/{*Path}`.**
  - **Layout:** breadcrumb (target name › folders); a table with name, size (humanised), modified (local time), and row actions; folders first, then files, alphabetical.
  - **Load more:** appears when `ContinuationToken` is non-null (Review Focus 5).
  - **Download:** a plain `<a href="/api/files/{id}/download?path={Uri.EscapeDataString(path)}" download>` link, which bypasses the circuit.
  - **Actions shown by level:**
    - Read: download.
    - Contribute: adds Upload (button and drag-and-drop zone), New folder, Rename/Move (the `NameDialog` takes a new relative path; staying in the same folder is a rename).
    - Full: adds Delete, which needs `ConfirmDialog` and, for a folder, typing its name.
  - **Results:** every action goes through `StorageAccessService`. Results show in an inline `.alert` with the message; `Unavailable` gets the alert in `--warning` styling. Any `Denied` result means the page's cached level was stale, so it re-fetches the level and refreshes the action set.
  - **Unreachable targets:** a target the user can't reach renders `AccessDeniedContent` in place. Don't navigate (this is the recipe trap).
  - **Uploads:** `UploadPanel` lists each file with progress, pause, resume and cancel.
  - **Overwrite:** `OnConflict` opens `ConfirmDialog` ("{name} already exists. Replace it?"). On confirm it restarts that file with `overwrite = true`.
- **`uploader.js`.**
  - Gets the antiforgery token from `/api/antiforgery`, then for each selected file calls `POST /api/uploads`.
  - Skips chunk indices already in `received`, and sends the rest with at most `parallelChunks` in flight.
  - Computes MD5 for each chunk. WebCrypto has no MD5, so vendor `spark-md5` 3.0.2 under `wwwroot/lib/spark-md5/` with its licence, read from `File.slice`.
  - Retries a chunk up to 5 times with exponential backoff (1, 2, 4, 8, 16 s) on network failure or 5xx/409-Conflict. A 403 or 404 stops that file.
  - Calls `complete` once all chunks are sent. A 409 from `POST /api/uploads` calls `OnConflict`.
  - Reports progress through `dotnetRef.invokeMethodAsync('OnProgress', …)`, throttled to 4 calls per second.
  - Uses no inline event handlers and no `eval`, so it is CSP-safe.
- **`/help`.** Shown to everyone. Sections:
  - Signing in: guest invites, redeem the invitation email, use your organisation's work account.
  - What Read, Contribute and Full allow (the table from spec §5.3).
  - Large uploads: resume after a dropped connection by selecting the same file again.
  - Contacting Advanced support.

- [ ] **Step 1: Write the failing tests.**
  - `UploaderScriptTests` reads `wwwroot/js/uploader.js` from disk and asserts it:
    - contains `RequestVerificationToken`
    - contains `Content-MD5`
    - contains no `eval(` and no `innerHTML =`
    - contains the retry delays `[1000, 2000, 4000, 8000, 16000]`
  - `BrowseRouteTests` (via `TestAppFactory`):
    - `Browse_OtherCustomersTarget_RendersAccessDenied_Not500`
    - `Home_CustomerRead_ShowsOnlyOwnTarget`
- [ ] **Step 2: Run them and confirm they FAIL.**
- [ ] **Step 3: Implement the pages, components and script.** Re-run the static guards from Task 2. Every new class needs a rule, and every icon needs a switch case.
- [ ] **Step 4: Run them and confirm they PASS.**
- [ ] **Step 5: Verify in the running app** with `Auth:Provider=Dev`, in-memory stores and `FakeStorageBackend`. Register the fake backend when `Storage:UseFake=true` in Development only, and guard it the same way as DevSignIn.
  - Sign in as `customer-full`: browse, create a folder, upload a 20 MB file, then a 0-byte file.
  - Kill the network mid-upload in devtools, restore it, and re-select the file. It should resume.
  - Rename, move, download (checking the filename), and delete.
  - Sign in as `customer-read`: only download is offered.
  - Check light and dark at 1440 px and 375 px. Expect no console errors and no CSP violations.
- [ ] **Step 6: Commit:** "Add Files home, file browser and resumable upload UI".

---

### Task 11: Staff admin — customers, users, targets, grants, staff, audit

**Files:**
- Create:
  - `Services/AdminService.cs`
  - `Pages/Admin/Customers.razor`, `Pages/Admin/CustomerDetail.razor`, `Pages/Admin/Targets.razor`, `Pages/Admin/Staff.razor`, `Pages/Admin/AuditLog.razor`, each with a `.razor.css`
  - `docs/gov-cloud-guest-setup.md`
- Modify: `Shared/NavMenu.razor` (an Admin group shown only to StaffAdmin or higher; Staff shown only to Owner), `Shared/Icon.razor`
- Test: `AdminServiceTests.cs`

**Interfaces:**
- Consumes: stores, `AuditService`, `IStorageBackendResolver.ProbeAsync`, `CurrentUser`.
- Produces:

```csharp
public sealed class AdminService { // every method takes the acting user id, re-checks role, and audits
    public Task<AccessResult<Customer>> SaveCustomerAsync(Guid actorId, Customer c, CancellationToken ct = default);          // StaffAdmin
    public Task<AccessResult<AppUser>> AddCustomerUserAsync(Guid actorId, Guid customerId, string upn, string displayName, CancellationToken ct = default); // StaffAdmin
    public Task<AccessResult> SetUserActiveAsync(Guid actorId, Guid userId, bool active, CancellationToken ct = default);      // StaffAdmin; staff users: Owner only
    public Task<AccessResult<StorageTarget>> SaveTargetAsync(Guid actorId, StorageTarget t, CancellationToken ct = default);  // StaffAdmin
    public Task<AccessResult<string>> TestTargetAsync(Guid actorId, Guid targetId, CancellationToken ct = default);           // StaffAdmin; ProbeAsync
    public Task<AccessResult> SetGrantAsync(Guid actorId, Guid customerId, Guid targetId, AccessLevel level, CancellationToken ct = default); // StaffAdmin; None = remove
    public Task<AccessResult> SetStaffRoleAsync(Guid actorId, Guid staffUserId, StaffRole role, CancellationToken ct = default); // Owner
}
```

**Rules the tests pin.**
- Each method re-loads the actor and requires the role noted above, so a stale page can't act with a revoked role. Every change writes an audit row whose operation names the change, for example `Admin.SetGrant` with path `"{customer} → {target} = {level}"`.
- **`AddCustomerUserAsync`**
  - The UPN must contain `@` and must not belong to the staff domain. Otherwise it returns `Invalid` with "Advanced staff sign in with their own account and don't need to be added."
  - The UPN must be unique.
  - It creates `Kind=Customer` with a null `EntraObjectId`.
- **`SaveTargetAsync`**
  - Account name must match `^[a-z0-9]{3,24}$`.
  - Container name: `^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$` for both kinds.
  - Subscription ID must be a GUID.
  - Changing the owning customer of a target that already has grants returns `Invalid` ("Remove this target's grants before moving it to another customer.").
- **`SetGrantAsync`** refuses when the target's owner ≠ `customerId` (`Invalid`), on top of the DB trigger.
- **`SetStaffRoleAsync`**
  - Can't change the actor's own role.
  - Can't set `Owner`, because owners come from config only.
- **`TestTargetAsync`** returns the probe result's user message on failure, and "Reachable — the portal can list this {file share/container}." on success.

**Pages.**
- **`/admin/customers`**: a table (name, cloud, cross-tenant status, user count, target count, active) and an Add customer form.
- **`/admin/customers/{id}`** has three sections:
  - Users: add by UPN, activate/deactivate, last sign-in.
  - Storage: the targets this customer owns, each with a level dropdown (`None`/`Read`/`Contribute`/`Full`).
  - Cross-tenant: shown only when `Cloud = AzureGov`. It has the checklist from the doc, a status selector and the home tenant ID.
- **`/admin/targets`**: all targets, an add/edit form and a **Test access** button.
- **`/admin/staff`** (Owner only): staff users with a role dropdown. Config owners are shown read-only.
- **`/admin/audit-log`**: filters for user, customer, target, operation, result and date range. It shows the newest 500 rows and has a CSV export link (a `GET /api/admin/audit.csv` minimal endpoint under the StaffAdmin policy, with CSV-injection-safe cells that prefix `'` to values starting with `= + - @`).
- All pages carry `[Authorize(Policy = Policies.StaffAdmin)]`, except Staff, which carries `Policies.Owner`.

**`docs/gov-cloud-guest-setup.md`** is a customer-IT checklist. Customer side:
1. Entra admin center → External Identities → Cross-tenant access settings → Microsoft cloud settings → enable **Microsoft Azure Commercial**.
2. Organizational settings → Add organization → paste the Advanced tenant ID (lookup by domain does not work across clouds).
3. Allow outbound B2B collaboration for the users or groups who need the portal.
4. Send Advanced the users' **UPNs**, not their email addresses.

Then Advanced does the mirror image, invites the users by UPN, and the users redeem the invitation. The doc keeps a placeholder for our tenant ID rather than hard-coding it, and cites Microsoft's "Cross-cloud settings" page.

- [ ] **Step 1: Write the failing tests** (`AdminServiceTests`, in-memory):
  - `SetGrant_TargetOwnedByOtherCustomer_Invalid`
  - `SetGrant_ByStaffRole_Denied`
  - `SetGrant_None_RemovesGrant`
  - `AddCustomerUser_StaffDomainUpn_Invalid`
  - `AddCustomerUser_DuplicateUpn_Invalid`
  - `SaveTarget_BadAccountName_Invalid`
  - `SaveTarget_ChangeOwnerWithGrants_Invalid`
  - `SetStaffRole_Self_Invalid`
  - `SetStaffRole_ToOwner_Invalid`
  - `SetStaffRole_ByStaffAdmin_Denied`
  - `SetUserActive_StaffUserByStaffAdmin_Denied`
  - `EveryMutation_WritesAuditRow`
  - `AuditCsv_FormulaPrefix_Escaped`, which tests a pure `CsvCell.Escape(string)` helper placed in `AdminService.cs`
- [ ] **Step 2: Run them; expect FAIL.**
- [ ] **Step 3: Implement the service, pages, CSV endpoint and doc.**
- [ ] **Step 4: Run them; expect PASS.** Then verify in the running app (Dev sign-in):
  - as `staff-owner`: add a customer and user, register a target, grant Contribute, and use Test access (it returns the fake backend's result);
  - as `staff`: the Admin nav is absent and `/admin/customers` renders Access Denied in place;
  - as `customer-full`: `/admin/audit-log` renders Access Denied.
- [ ] **Step 5: Security review.** Invoke `security-auditor` on the admin surface and CSV export, then `unit-test-reviewer` on `AdminService`.
- [ ] **Step 6: Commit:** "Add staff admin for customers, users, targets, grants, staff roles and audit log".

---

### Task 12: Container, app-owned web tests and release docs

**Files:**
- Modify: `Dockerfile` (add the test gate and `gosu` final stage, copying RM's Dockerfile with names swapped), `docker-entrypoint.sh` (RM's, with `CloudSelfService.dll`), `CLAUDE.md`, `CHANGELOG.md`, `.claude/rules/{code-style.md,security.md,testing.md}`. In `code-style.md` the changelog rule becomes one file: drop the MainLayout `_changelog` list, matching RM. Its stack line becomes sumi, with no MudBlazor or Bootstrap. In `security.md` the restart rules stay, and rules are added for the storage gate and the upload route., `.claude/memory/{app-purpose.md, auth-placeholder.md→auth-model.md, MEMORY.md}`, `.claude/settings.json`
- Create: `.dockerignore` (from `D:\SC\Developer-Standards\templates\.dockerignore`, plus `*.msg`, `webapp-tests/`), `README.md`, `webapp-tests/conftest.py`, `webapp-tests/test_cloudselfservice.py`, `webapp-tests/requirements.txt`

**Behaviour:**
- **Dockerfile:**
  - Restore the app and test csproj, run `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj -c Release --no-restore`, then `dotnet publish` **without** `--no-restore`.
  - The final stage runs as `appuser` through `gosu`.
  - `ENV ASPNETCORE_URLS=http://+:8080`.
- **Web tests:**
  - Self-contained `conftest.py`. It starts nothing itself; it targets `CSSP_BASE_URL` (default `http://localhost:5300`) and signs in through `/dev/signin?as=…`.
  - Selectors use `data-testid` attributes only; add them in Tasks 10/11 markup where missing.
  - Tests:
    - `test_customer_read_sees_only_download`
    - `test_customer_full_upload_rename_delete_roundtrip` (needs `Storage:UseFake=true`)
    - `test_stranger_access_denied_copy`
    - `test_staff_none_pending_panel`
    - `test_staff_cannot_open_admin`
    - `test_theme_toggle_persists`
    - `test_no_console_errors_on_home`
- **CLAUDE.md** is rewritten in RM's shape: what the app is, commands, config table (all keys from Task 4/5/9), architecture, security checks (single storage gate, no service-signin, Dev sign-in guard, upload chunk route WAF exemption), and container build with `$version = "1.0.0"`.
- **CHANGELOG.md** top entry is `## [1.0.0] - TBD` and covers the rebuild and the storage portal, with glyph sections per the suite changelog standard. Read `D:\SC\Personal\.claude\memory\reference_changelog_standard.md` for the exact section labels and order.

- [ ] **Step 1: Docker build.** Run `podman build -t cloudselfservice:1.0.0-local --build-arg APP_VERSION=1.0.0-local .` and expect the build to succeed with the test step green. **Do not tag or push.**
- [ ] **Step 2: Start the container** with `podman run --rm -d -p 5301:8080 -e ASPNETCORE_ENVIRONMENT=Production -e Auth__Provider=Dev cloudselfservice:1.0.0-local`. Expect the container to exit, with the log showing "Dev sign-in cannot run outside Development". This proves the guard works in the real image. Then run it again with `Auth__Provider=Entra` and placeholder Entra values, and expect `/login` to return 200.
- [ ] **Step 3: Web tests.** Run the app locally in Dev mode with the fake backend, then `cd webapp-tests; python -m pytest test_cloudselfservice.py -v`. Expect all to pass.
- [ ] **Step 4: Write the docs and changelog** as above. Run `dotnet build` and the full `dotnet test`, and expect 0 errors and 0 failures.
- [ ] **Step 5: Full-branch security review.** Invoke `security-auditor` on the whole branch diff, fix its findings, and re-run the tests.
- [ ] **Step 6: Commit:** "Container test gate, app-owned web tests, docs and v1.0.0 changelog".

---

### Task 13: Infrastructure source edits (ZZ_Pulumi — edit only, never apply)

This task edits the Orchestrator repo's `ZZ_Pulumi/`, **not** the app repo. Commits go to the Orchestrator repo, and only when Dan authorises.

**Files:**
- Modify: `ZZ_Pulumi/Pulumi.ias-ai.yaml` (data only; no comments beyond section dividers), `ZZ_Pulumi/Models/AppDefinition.cs`, `ZZ_Pulumi/Resources/AppGatewayResources.cs`, `ZZ_Pulumi/IasAiStack.cs`
- Create: `ZZ_Pulumi/Models/StorageAccessTarget.cs`, `ZZ_Pulumi/Resources/StoragePortalResources.cs`

**What to add:**
1. **App entry** `CLOUDSELFSERVICE` with these fields:
   - `shortCode: CSS`
   - `hostname: cloudselfservice.infinitycis.com`
   - `containerImage: auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice`
   - `containerVersion: 1.0.0`
   - `pgDatabaseName: cloudselfservice`
   - `authProvider: Entra`
   - `storageMode: PGSQL`
   - `entraClientId: ""` (Dan fills it in after the app registration)
   - `authorizationOwners: djennings@advancedutility.com`
   - `memoryLimitMb: 2048`

   Check whether a `CSS` shortCode collides with an existing one first. If it does, use `CSP`.
2. **`AppDefinition.StorageAccessTargets`**: a `List<StorageAccessTarget>` with xmldoc. Each entry has:
   - `subscriptionId`
   - `resourceGroup`
   - `accountName`
   - `services` (`["file","blob"]`)
   - `dataRole` (`Contributor` or `Reader`)

   For each entry, `StoragePortalResources` creates the following, using a cross-subscription provider the same way the hub peering does:
   - a private endpoint per service in the suite spoke's storage subnet;
   - `privatelink.file.core.windows.net` / `privatelink.blob.core.windows.net` private DNS zones, if absent, linked to the suite VNet, with zone groups on the endpoints;
   - role assignments for the app's system-assigned identity: **Storage File Data Privileged Contributor** (`69566ab7-960f-475b-8e7c-b3118f30c6bd`) and **Storage Blob Data Contributor** (`ba92f5b4-2d11-453d-a403-e96b0029c9fe`). Use the Privileged Reader / Blob Data Reader IDs when `dataRole` is `Reader`. Verify every role GUID against `az role definition list --name "<role>"` output that Dan supplies, or Microsoft's built-in roles page, before committing; never guess.
3. **Dev test storage account** `auseusdevstgcssptest` (or the naming convention's equivalent; follow `Utilities.ResourceName`). It has:
   - shared-key access disabled
   - public network access disabled
   - one share `cssp-test` and one container `cssp-test`
   - file and blob private endpoints in the suite spoke
   - data roles for the app identity, and for an Entra group whose object ID comes from a new config value `cloudSelfServiceDevGroupObjectId`
4. **WAF**: a per-URI `WebApplicationFirewallPolicy` bound through a path-based rule on the CloudSelfService listener for `/api/uploads/*`, with `RequestBodyCheck = false`. It is the same policy as the shared one in every other respect, and the shared policy stays unchanged.

   If the current App Gateway uses only basic rules for this listener, add a URL path map for this app alone. Record the alternative in the task report and **stop for Dan's decision** if that change would alter other apps' rules.
5. A `_dev` PostgreSQL database and role for `cloudselfservice`, using the existing per-app pattern (should follow automatically from `pgDatabaseName`; confirm in `DatabaseResources.cs`).

- [ ] **Step 1: Build.** Run `dotnet build ZZ_Pulumi` and expect 0 errors. **Do not run any `pulumi` command.**
- [ ] **Step 2: Summarise for Dan in the task report:**
  - the resources that `pulumi preview` (run by Dan from IAS-AI after `git pull origin main`) should show;
  - the manual Entra steps: the app registration with redirect URIs `https://cloudselfservice.infinitycis.com/signin-oidc` and `http://localhost:5300/signin-oidc`, and the client secret in the app's Key Vault per the suite pattern;
  - Dan's follow-ups:
    - fill `entraClientId`;
    - list the first customers' storage accounts under `storageAccessTargets`;
    - run the live checks deferred from Tasks 4 and 6.
- [ ] **Step 3: Commit to the Orchestrator repo**, with Dan's authorisation: "ZZ_Pulumi: Cloud Self Service app, storage-portal private endpoints and RBAC, upload WAF exemption, dev test account".

---

## Coverage check (self-review)

| Spec section | Task |
|---|---|
| §5.1 rebuild to standard | 1, 2, 5, 12 |
| §5.2 identity, guests, staff auto-create with no role, fallback seam (`IdentityResolver`) | 5 |
| §5.3 roles and levels, per-request checks | 3, 5, 7, 11 |
| §5.4 single storage gate, Files backup intent, blob folders, path safety, errors | 3, 6, 7 |
| §5.5 downloads (attachment, range), chunked resumable MD5 uploads, WAF | 8, 9, 10, 13 |
| §5.6 data model incl. owner rule, append-only audit, upload sessions/chunks | 4, 9 |
| §5.7 pages | 10, 11 |
| §6 infra dependencies | 13 (Entra app registration and Defender for Storage are listed for Dan as manual follow-ups) |
| §7 onboarding + Gov checklist doc | 11 |
| §9 calls: no service-signin, one owner per target, Staff=Contribute, attachment-only, overwrite confirm, placeholders deleted | 5, 3/4, 3, 8, 9/10, 1 |
