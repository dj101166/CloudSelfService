# CLAUDE.md — Advanced Cloud Self Service

## What this project is

Blazor Server (.NET 10) private storage access portal. Advanced customers (Microsoft Entra B2B guests, including cross-cloud Azure Government guests) and Advanced staff sign in with Microsoft and browse, upload and download files held in Advanced-hosted Azure Files shares and Blob containers. The browser never receives a storage key, SAS token or storage URL: every byte streams through the app, behind one access check.

**Status:** Phase 1 (file portal) is complete and release-ready as v1.0.0. Phase 2 (an automation API for programmatic transfers) is not started. UI is the Advanced Developer Standards "sumi" system (plain CSS tokens, no MudBlazor, no Bootstrap).

Design and plan: `docs/superpowers/specs/2026-11-20-private-storage-access-design.md`, `docs/superpowers/plans/2026-11-20-private-storage-access.md`. Operators: `docs/operations.md`. Azure Government customers: `docs/gov-cloud-guest-setup.md`.

## Commands

```powershell
dotnet run                                   # http://localhost:5300 (Kestrel:Endpoints:Http:Url)
dotnet build
dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj   # name the project: a bare `dotnet test` runs nothing
```

PostgreSQL integration tests skip unless `CSSP_TEST_PG` holds a connection string to a throwaway database. Live-storage tests skip without their own variables.

### Local run without Entra or Azure

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Auth__Provider = "Dev"        # /dev/signin?as=<persona>
$env:Storage__UseFake = "true"     # in-memory storage with seeded sample files
dotnet run --no-launch-profile
```

Personas: `staff-owner`, `staff-admin`, `staff`, `staff-none`, `customer-read`, `customer-contribute`, `customer-full`, `stranger`. Each goes through the real admission code with a synthetic identity.

### Web tests (app-owned)

`webapp-tests/` is a self-contained Playwright pytest suite (`data-testid` selectors only). It starts nothing: run the app as above, then:

```powershell
cd webapp-tests
$env:CSSP_BASE_URL = "http://localhost:5300"   # default
python -m pytest test_cloudselfservice.py -v
```

## Configuration

Set in `appsettings.json`, `appsettings.Development.json` (git-ignored; copy from `.sample`) or as App Service settings / env vars (`Section__Key`). The full annotated list is in `docs/operations.md`.

| Key | Purpose |
|---|---|
| `ConnectionStrings:Default` | PostgreSQL. Required outside Development (the app refuses to start). In Development, unset means in-memory stores (data lost on restart) with a warning |
| `Auth:Provider` | `Entra` (default) or `Dev` (Development only; the app refuses to start otherwise) |
| `Auth:IdleTimeoutMinutes` / `IdleWarningSeconds` / `AbsoluteSessionHours` / `RevalidateSeconds` | Session lifetime, and how often the live circuit re-checks the user and customer are active, the oid binding and the absolute lifetime |
| `Entra:TenantId` / `ClientId` / `ClientSecret` / `Instance` / `CallbackPath` / `StaffEmailDomain` | OIDC registration; staff are recognised by email domain |
| `Authorization:Owners` | Comma-separated UPNs that are Owner. Config-authoritative, derived on every role check, never stored. Empty outside Development logs a startup warning |
| `Storage:AllowedEndpointSuffixes` / `AllowedSubscriptions` | Which storage endpoints and subscriptions admins may register |
| `Storage:UseFake` | In-memory backend (Development only) |
| `Admin:TestAccessTimeoutSeconds` / `AuditExportsPerMinute` | Admin guard rails |
| `Support:Contact` | Shown on Help and Access Denied |
| `Transfers:*` | Upload and download limits, concurrency, rate limits (`RequestsPerMinute` / `MaxConcurrentPerUser` cover downloads and upload Begin/Status/Complete/Abort; chunk PUTs have their own), reauthorisation cadence |
| `DATA_ROOT` | Directory (Azure Files mount) where Data Protection keys persist under `dp-keys`. Unset means ephemeral keys: every restart signs everyone out |
| `APP_VERSION` | Build arg / env var shown in the topbar version chip |

## Architecture

- **Pages/**: `Index` (Files home), `Files/Browse`, `Help`, `Login`, `SignedOut`, `AccessDenied`, `Admin/{Customers,CustomerDetail,Targets,Staff,AuditLog}`.
- **Shared/**: layout, nav, account menu, upload panel, dialogs, changelog dialog.
- **Authorization/**: Entra OIDC wiring, policies, role handler, session revalidation, `DevSignIn`.
- **Services/**: `Access/` (`StorageAccessService` is the single storage gate, `StoragePath`), `Storage/` (Azure and fake backends), `Identity/` (incl. `StaffAccess`, the effective-role rule), `Data/` (PostgreSQL and in-memory stores), `AdminService`, `StartupChecks`, `ChangelogDocument`.
- **Endpoints/**: `DownloadEndpoints`, `UploadEndpoints` (`PUT /api/uploads/{id}/chunks/{index}`), auth endpoints, rate limits.
- **wwwroot/**: `css/` (sumi tokens in `theme.css`), `js/cloudselfservice.js` (theme, sidebar), `js/uploader.js`, `lib/spark-md5/` (vendored, licence in `ThirdPartyNotices/`).
- **CloudSelfService.Tests/**: xUnit; the Dockerfile runs it.

## Security checks (review these on every change)

- **Single storage gate.** All storage reads and writes are decided by `StorageAccessService`. Only `DownloadEndpoints`, `UploadSessionService`, `UploadCleanupService` and `AdminService` (Test access) also hold the raw backend, and they act only after a `StorageAccessService` decision and only on the target it returned. `StorageGateArchitectureTests` fails if any other type takes `IStorageBackendResolver`, `IStorageBackend` or `StorageClientFactory`; extending its allow-list is a security review decision. Never construct a storage client elsewhere. Files access uses `ShareTokenIntent.Backup`.
- **Effective staff role.** Read it only through `StaffAccess` (`Effective`, `IsPending`, `Reaches`). Owner is derived from `Authorization:Owners` and never stored, so the stored `StaffRole` alone must never gate or label anything.
- **No `/auth/service-signin`.** There is no shared-secret or API-key sign-in on this app.
- **Dev sign-in and fake storage are Development-only.** `DevSignIn.AssertSafe` throws at startup otherwise; the built image was verified to exit with "Dev sign-in cannot run outside Development" on `Auth__Provider=Dev` in Production.
- **Upload chunk route.** `PUT /api/uploads/{id}/chunks/{index}` carries 4 MiB bodies and needs a WAF exemption (body-inspection limit) at the App Gateway; it is antiforgery-protected, rate limited and session-bound.
- **No keys or URLs to the browser.** Downloads are `Content-Disposition: attachment` and `application/octet-stream`.
- **Every `@page`** has `[Authorize]` or a policy, except `/login`, `/signed-out`, `/access-denied`.
- **Client IP trust** relies on the App Gateway being the only ingress (forwarded headers are trusted unconditionally).

### Deliberate CSP tightening (consistency-checker exemption)

This app deliberately sets a stricter CSP than the suite baseline: **no `'unsafe-eval'`**, `connect-src 'self'`, `img-src 'self' data:`, plus `frame-ancestors 'none'`, `form-action 'self'`, `object-src 'none'`, `base-uri 'self'`, and `X-Frame-Options: DENY`. The consistency-checker must not "fix" these toward the suite baseline.

## Container build

The Dockerfile restores the app and test projects, runs `dotnet test` (a failing test fails the build), then `dotnet publish` (never `--no-restore`). The final stage runs as `appuser` through `gosu`.

```powershell
$version = "1.0.0"
podman build -t cloudselfservice:$version --build-arg APP_VERSION=$version .
podman tag cloudselfservice:$version auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:latest
podman tag cloudselfservice:$version auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:$version
podman push auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:latest
podman push auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:$version
```

## Code style

- sumi CSS tokens only (`var(--accent)`, `var(--surface-page)`, ...); no hard-coded hex outside `wwwroot/css/theme.css`.
- Page titles: `<PageTitle>Page Name — Advanced Cloud Self Service</PageTitle>`.
- Every page carries `@attribute [Authorize]` or a policy attribute.

## Mandatory post-edit checklist

1. **Build and test** — `dotnet build` 0 errors, `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` 0 failures.
2. **Changelog** — one file, `CHANGELOG.md` (embedded and rendered by `ChangelogDocument`). Pending entries use `TBD`. No em-dashes or customer names in the text.
3. **Security review** — after auth, routes, `Program.cs`, upload or download endpoints, middleware, CSP or Dockerfile changes: `Agent(subagent_type: "security-auditor", ...)`.
4. **Test coverage** — after `Services/` changes: `unit-test-reviewer`.

## Plan files

Plans are saved automatically into `.claude/plans/` by the `save-plan-to-project` PostToolUse hook.
