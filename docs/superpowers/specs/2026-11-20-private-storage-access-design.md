# Private Storage Access Portal — Design (Phase 1)

**App:** Advanced Cloud Self Service (`CloudSelfService`)
**Date:** 2026-11-20
**Status:** Draft for approval

## 1. Intent and why

The Azure storage accounts behind every customer stamp are publicly reachable today. Rapid7
flags public storage as a **critical** vulnerability, the CIT agreement does not allow critical
findings to be exempted while an alternative exists, and GovRAMP certification requires them
closed. The fix — private endpoints and public network access disabled — has been deferred for
over a year because a previous attempt cut off every human route to the data plane: Azure
Storage Explorer, SAS tokens, SAS URLs and mapped (SMB) drives all stop working once storage is
private. The application tier kept working; people did not.

This feature makes Cloud Self Service the **single public, signed-in front door** to that
storage. The app runs on the shared suite plan, reaches each stamp's storage through private
endpoints placed in the suite's own spoke network, using its own managed identity, and gives customers and Advanced staff a browser-based
replacement for Storage Explorer. Customers never hold a key or a SAS token again.

The architecture follows the outline from Microsoft (Krishna Venkataraman, July 27–28 2026):
Entra sign-in → public portal → managed identity + VNet integration → storage private endpoint.

### Who it serves

- **Customer users** — ~13 production customers (4–5 live, the rest in implementation). Today
  they use Storage Explorer with SAS tokens (most) to move data-transfer files, application logs
  and artifacts on **file shares**. 30-day retention applies. Blob access is not used today but
  is expected.
- **Advanced staff** — support and DevOps, who currently regenerate SAS tokens for every active
  customer after each 90-day key rotation, and who lost data-plane access in the last attempt.

### What success looks like

Storage accounts can be switched to private-endpoint-only with no customer or staff loss of
the ability to browse, download and upload their files. SAS regeneration after key rotation
stops being a support task.

## 2. Goals, non-goals, anti-goals

**Goals (phase 1)**

1. Rebuild the parked scaffold onto the current suite standard (section 5.1).
2. Entra sign-in for staff (members) and customers (B2B guests, including Azure Government
   tenants via cross-cloud B2B).
3. Staff can register customers, their users, and the file shares / blob containers each
   customer may reach, with an access level per grant.
4. Customers browse, preview-by-download, upload, create folders, rename and (when granted)
   delete within exactly the shares/containers granted to their customer — nothing else.
5. Every data and admin action is audited.

**Non-goals (phase 1)**

- **Automation / machine access** (key-based mount scripts, SAS automation, third-party
  integrations). Parked; phase 2 adds an OAuth-secured API. Phase 1 must not *preclude* it
  (section 5.4 keeps storage access behind one service).
- A desktop sync client or Windows service.
- The scaffold's original placeholder features — SLA uptime, service health, App Service
  restart. They return as later features; their placeholder pages are removed now.
- Billing, quotas, compliance-certification dashboards, organisation subscription tiers (all
  present in Microsoft's generic mockups; not wanted).
- Folder-level or file-level permissions inside a share/container.
- Queues, tables, Data Lake.
- The "option 2" fallback identity tenant (section 5.2) — designed for, not built.

**Anti-goals (a failure even if it technically works)**

- A customer can see, list or touch **any** share/container not granted to their customer —
  including by path manipulation, a guessed URL, or a stale session after a grant is revoked.
- A customer is ever handed a storage key, SAS token or direct storage URL.
- Large transfers stall or kill the Blazor circuit, or are buffered whole into server memory.
- The app's own sign-in becomes the reason storage cannot go private (e.g. Gov-cloud customers
  locked out with no path forward).

## 3. Constraints

- **Suite standard:** .NET 10 Blazor Server, Blazor Web App render model, Developer Standards
  "sumi" skin, Entra OIDC, PostgreSQL with an isolated `_dev` clone, session guard, changelog
  rules, container image to `auscacprdcregsvc73a13ca2.azurecr.io`. See workspace `CLAUDE.md`
  and the migration recipe in memory `project_developer_standards_ui_migration`.
- **Network:** hub-and-spoke. Each stamp is a spoke VNet peered to the hub
  (`AUS-CAC-PRD-VNET-SVC`). Peering is not transitive, so the suite spoke cannot reach a stamp
  spoke through the hub. The hub also appears to be in a different region from the suite's
  shared East US App Service plan, and regional VNet integration needs plan and VNet in the same
  region. **Decision (Dan, 2026-11-20): the app runs on the shared suite plan in the suite spoke
  (`10.253.38.0/24`), and each storage account gets an additional private endpoint (file, blob)
  in the suite spoke's storage subnet.** Cost is roughly $7/month per endpoint. Accepted
  consequence: customer file bytes transit East US between the stamp's storage and the browser.
- **Azure Files over OAuth:** with shared-key auth disabled, the app's identity needs
  **Storage File Data Privileged Reader/Contributor** (plain "SMB Share" roles do not work over
  REST), and every request must carry the backup intent header (`ShareTokenIntent.Backup` in
  the .NET SDK). These roles bypass NTFS ACLs. REST API version ≥ 2022-11-02 for file/dir
  operations; listing shares over OAuth needs ≥ 2024-11-04.
- **Cross-cloud B2B (Azure Government customers):** supported commercial ↔ Azure Government
  as of Microsoft's 2026-07-27 docs, but **both tenants** must enable the other cloud in
  Cross-tenant access settings, add each other by tenant ID, and set inbound/outbound access.
  Invites must use the user's **UPN**, not email. Cross-cloud guests cannot use the common
  endpoint — the app's sign-in must use our tenant-specific authority (which it does).
- **Multitenant ("any Microsoft account") sign-in does not work across national clouds** — this
  is why guests, not a multitenant app, is the model.
- **Pulumi is never applied from here.** Infra changes are edited in `ZZ_Pulumi` source only and
  applied by Dan from the IAS-AI mirror.

## 4. Approaches considered

| | Approach | Trade-off | Outcome |
|---|---|---|---|
| **A** | **One Blazor app with built-in streaming transfer endpoints.** Portal, staff admin and minimal-API upload/download endpoints in one image; all storage access behind one service. | One image/deployment, matches the suite, fastest to customers. Phase 2's API either joins this app or is split out later — a move, not a rewrite, because storage access is already isolated. | **Chosen** |
| B | Portal + separate File Transfer API now (Krishna's diagram literally). | Proven API ready for phase 2 and a tight security boundary, but two images, two deployments and service-to-service auth — paying phase 2's cost before phase 2 is designed. | Rejected for phase 1 |
| C | Vue 3 + TypeScript SPA over an ASP.NET Core API (Developer Standards SPA default). | Only non-Blazor app in the suite; loses the shared shell, auth and session handling. | Rejected |

Sign-in options considered: **(1) B2B guests in our tenant — chosen**; (2) a separate
customer-only External ID tenant with local accounts / email one-time codes — **the fallback**
if a customer will not complete the cross-tenant setup; (3) multitenant "any Microsoft login" —
rejected because it cannot serve Azure Government users from a commercial-cloud app.

## 5. Design

### 5.1 Rebuild to the suite standard

The scaffold is three placeholder pages, so this is a rebuild, not a migration. Follow the
already-migrated apps (ReleaseMetrics is the closest analogue — Entra, PG, roles, audit) and
read Nexus literally for the sumi shell, per the migration recipe:

- Render model: `App.razor` + `Routes.razor`, `AddRazorComponents().AddInteractiveServerComponents()`,
  `AddCascadingAuthenticationState`. No `_Host.cshtml`.
- **`Routes.razor` `NotAuthorized` branches**: anonymous → `/login`; authenticated policy
  failure → Access Denied rendered in place. Role handler refuses an unidentified principal
  before its first `await`. Pin both with tests (the recipe's host-killing-crash trap).
- Drop MudBlazor, Bootstrap, Open Iconic, `site.css`, `theme-manager.js`. Add `theme.css`
  (sumi, from Nexus), `app.css`, `Icon.razor`, `AppMenu`, `ChangelogDialog` rendering
  `CHANGELOG.md` via `ChangelogDocument` (one-file release notes), `ReconnectModal`.
- Header + collapsible sidebar shell.
- Session guard with the inactivity timeout (now that auth is live) and `/keepalive`.
- Security headers/CSP per the suite standard (memory `reference_security_header_csp_standard`).
- PostgreSQL via Npgsql with a `_dev` clone and isolated `app_{sc}_dev` role; `appsettings.Development.json.sample`;
  dev HTTP port via `Kestrel:Endpoints:Http:Url`.
- `CloudSelfService.Tests` (xUnit) with the four static guards (`ComponentStyleContractTests`,
  `StylesheetIntegrityTests`, `IconNameContractTests`, `NotAuthorizedRoutingTests`) and a
  `dotnet test` gate in the Dockerfile.
- Dockerfile final stage as `appuser` via `gosu`; no `--no-restore`; `.dockerignore`.
- Repo stays in the suite's flat layout (no `apps/` restructure — no suite app has done that).
- The two `.msg` emails in the repo root stay untracked (they contain external contacts);
  this design doc replaces them as the record.

### 5.2 Identity and sign-in

- One **single-tenant** Entra app registration in our (commercial) tenant; authority is
  tenant-specific (`https://login.microsoftonline.com/{tenantId}/v2.0`), which cross-cloud
  guests require.
- **No email-domain gate** (unlike the staff-only apps). Admission depends on who signs in:
  - **Staff** — a **member** of our tenant (`acct` = 0 / no guest marker) whose UPN is in the
    `advancedutility.com` domain. **Decision (Dan): auto-create a user record on first sign-in
    with no staff role and therefore no data access**, the same first-sign-in flow as the other
    suite apps. A Staff Admin or Owner then assigns Staff / Staff Admin. A role-less staff user
    sees a "your access is pending" page, not customer data.
  - **Customer** — any other principal (in practice, a B2B guest). Admitted **only if a staff
    member already created a customer user record** for them. Unknown principals land on an
    Access Denied page that says to contact Advanced support. No self-registration.
- Staff vs customer is decided from the token claims, never from anything the user enters.
- **User identity key:** the `oid` claim (the object ID in *our* tenant — stable for guests too).
  Staff pre-register a customer user by email/UPN; on first sign-in the app matches on
  `preferred_username` / `email` (case-insensitive) and binds the `oid`. After that, the `oid`
  is authoritative and an email change in the home tenant does not break the link.
- **Inviting guests** happens in Entra (portal or Graph), not in this app for phase 1. The admin
  UI shows the exact steps and, for Gov-cloud customers, the cross-tenant checklist (section 7).
- **Fallback (option 2) seam:** sign-in maps any accepted principal through one
  `IdentityResolver` into an internal user record. A second OIDC scheme (External ID tenant)
  can later feed the same resolver without touching access control.

### 5.3 Roles and access levels

**Staff roles** (app-level, stored in the DB; Owners from config, as in ReleaseMetrics):

| Role | Data access | Admin |
|---|---|---|
| (none) | nothing — default for a newly auto-created staff record | none |
| Staff | **Contribute** on every registered share/container, any customer | none |
| Staff Admin | **Full** on every registered share/container | manage customers, users, targets, grants; view audit log |
| Owner (config) | as Staff Admin | plus manage staff roles |

**Customer access** is granted **per customer, per share/container**, at one level, and applies
to every user of that customer:

| Level | Can do |
|---|---|
| Read | browse, download |
| Contribute | Read + upload (incl. overwrite), create folder, rename/move within the same share/container |
| Full | Contribute + delete (files and empty or non-empty folders) |

Delete is built and tested even though no customer has had it via SAS to date.

**Authorization is checked on every operation**, server-side, in one place (5.4) — never by
hiding UI. A revoked grant or deactivated user takes effect on the **next request**: no grant
is cached in the circuit or the cookie.

### 5.4 Storage access service

One service, `StorageAccessService`, is the **only** code that touches storage. Every call
takes `(user, targetId, path, operation)`, resolves the user's effective level for that target,
refuses if insufficient, executes, and writes the audit row. Pages and transfer endpoints call
it; nothing else holds a storage client. This is the seam phase 2's API reuses.
The few other callers that hold the raw backend (`DownloadEndpoints`, `UploadSessionService`,
`UploadCleanupService` and `AdminService`'s Test access) act only after a `StorageAccessService`
decision and only on the target it returned; `StorageGateArchitectureTests` fails the build if
any other type takes a backend, resolver or client factory.

- **Clients:** `Azure.Storage.Files.Shares` with `ShareTokenIntent.Backup`, and
  `Azure.Storage.Blobs`; one client per account, cached. Credential: `DefaultAzureCredential`
  (managed identity in production; developer identity or `AZURE_CLIENT_ID`/`SECRET` in dev).
  Storage endpoint suffix is per target, so a Gov-hosted storage account is not ruled out later.
- **Blob "folders"** are virtual (`/`-delimited prefixes); folder create = zero-byte marker
  blob or none (builder's call, see section 8); rename = copy + delete.
- **Path safety:** every path is normalised and must stay inside the target's root — reject
  `..`, absolute paths, backslashes, control characters, and names Azure Files forbids. Pure
  function, unit-tested with hostile inputs.
- **Errors:** storage 403/404/network failures map to user-readable messages; the private
  endpoint being unreachable shows "storage temporarily unreachable", never a stack trace.

### 5.5 Transfers

Uploads and downloads do **not** go through the Blazor circuit.

- `GET /api/files/{targetId}/download?path=…` — authorises, then **streams** storage → response
  (`Content-Disposition: attachment`, never inline, to avoid serving customer HTML/SVG on our
  origin). Supports `Range` for resumed downloads.
- **Uploads are chunked and resumable in phase 1.** Dan confirmed customers move files of
  several GB or more. Flow:
  1. `POST /api/uploads` `{targetId, path, size, lastModified}` — authorises (Contribute+) and
     the overwrite rule, creates an **upload session** row, and prepares storage: for Azure
     Files, create the file at a hidden staging name (`.{name}.cssupload-{sessionId}`) with the
     full length; for blobs, nothing (uncommitted blocks are invisible).
  2. `PUT /api/uploads/{sessionId}/chunks/{index}` — raw `application/octet-stream` body,
     fixed chunk size (default 4 MiB, which is the Azure Files per-range maximum), with a
     `Content-MD5` header. The app re-checks the grant, then streams the chunk to storage as a
     **Put Range** (Files) or **Put Block** (blob) passing the MD5, so storage itself rejects a
     corrupted chunk. The session records which chunks landed.
  3. `GET /api/uploads/{sessionId}` — returns the chunks received, so the browser resumes after
     a dropped connection or a page reload (the browser re-selects the same file, matched on
     name + size + lastModified).
  4. `POST /api/uploads/{sessionId}/complete` — Files: rename staging → final name. Blob:
     **Put Block List**. Writes the single audit row for the upload (bytes, duration, result).
  - Sessions expire after 24 hours. A hosted cleanup job deletes expired staging files and
    session rows.
  - Several chunks upload in parallel (default 4). Progress, pause/resume and cancel happen in a
    small JS module. Max file size is configurable (`Transfers:MaxUploadBytes`, default 50 GiB)
    and enforced at session creation.
  - The checksum Krishna asked for is per-chunk MD5, verified by storage on every chunk. A
    whole-file hash is not computed in phase 1 (it cannot be built from out-of-order parallel
    chunks without re-reading the file).
- **Ingress firewall.** The shared App Gateway WAF policy sets `MaxRequestBodySizeInKb = 128`
  with `RequestBodyEnforcement = true` (currently Detection mode). In Prevention mode that
  would reject every 4 MiB chunk. Infra needs a **per-URI (or per-listener) WAF policy** that
  disables body inspection for `PUT /api/uploads/*/chunks/*` only, leaving every other app and
  route under the shared policy (section 6).
- **Malware scanning** is an infra concern: enable Microsoft Defender for Storage on-upload
  malware scanning on the target accounts (section 6). The app surfaces the scan result tag on
  blobs where present; it does not run its own scanner.
  - **Note (deferred, R47):** not implemented in v1.0.0. Defender writes the verdict as the blob
    index tag `Malware Scanning scan result`, and reading index tags needs the
    `blobs/tags/read` data action, which Storage Blob Data Contributor (the role in section 6)
    does not include. Displaying it would mean granting Storage Blob Data Owner or a custom role
    on customer accounts; that is left for a later decision. Scanning itself is unaffected.

### 5.6 Data model (PostgreSQL)

| Table | Holds |
|---|---|
| `customers` | id, name, active, notes, home tenant ID, cloud (Commercial / AzureGov), cross-tenant setup status |
| `users` | id, kind (Staff / Customer), customer_id (null for staff), email/UPN, display name, entra `oid` (bound on first sign-in), staff role, active, created/last-sign-in |
| `storage_targets` | id, **owning customer_id** (null = internal / staff-only target), display name, subscription ID, resource group, account name, kind (FileShare / BlobContainer), share/container name, endpoint suffix, active |
| `grants` | customer_id, target_id, level (Read / Contribute / Full), granted_by, granted_at — **a grant is only allowed to the target's owning customer** (enforced in the service and by a DB check), so one customer's share can never be granted to another |
| `upload_sessions` | id, user id, target id, final path, staging name, size, chunk size, overwrite, created/expires, status |
| `upload_chunks` | session id, chunk index (one row per received chunk; safe under parallel chunk PUTs) |
| `audit_events` | timestamp, user id + display, customer, target, operation, path, bytes, result (Success / Denied / Failed), error, source IP, correlation ID |

Audit rows are append-only from the app (no update/delete path). Retention: indefinite in
phase 1; revisit if volume warrants.

### 5.7 Pages

| Route | Who | What |
|---|---|---|
| `/` | everyone | **Files home** — the shares/containers the user can reach, grouped by customer (staff see all customers; customers see their own) |
| `/files/{targetId}/{**path}` | everyone with access | Browser: breadcrumb, folder/file table (name, size, modified), download, and — by level — upload (button + drag-drop), new folder, rename, delete with confirm |
| `/admin/customers` | Staff Admin | customers list/detail, users, grants; Gov-cloud cross-tenant checklist status |
| `/admin/targets` | Staff Admin | register shares/containers; **"Test access"** lists the root to prove the private route + RBAC work |
| `/admin/staff` | Owner | staff roles |
| `/admin/audit-log` | Staff Admin | filterable audit log (user, customer, target, operation, result, date) |
| `/help` | everyone | how to sign in (incl. guest invite redemption), what each level allows, who to contact |
| `/login`, `/signed-out`, `/access-denied` | anonymous | as in the suite |

Target registration in phase 1 is **manual entry + Test access**; ARM-based discovery of
accounts/shares across the 19 subscriptions is a later nicety.

## 6. Infrastructure dependencies (outside this repo)

Edited in `ZZ_Pulumi` source (never applied from here) or done by DevOps; phase 1 cannot go live
for a stamp until that stamp's items are done:

1. A `ZZ_Pulumi` app entry for Cloud Self Service on the **shared suite plan**, VNet-integrated
   into the suite spoke like every other app, with a system-assigned managed identity and the
   suite's App Gateway ingress.
2. Per customer/stamp storage account (cross-subscription): a **second private endpoint for
   `file` and for `blob` in the suite spoke's storage subnet**, with records in
   `privatelink.file.core.windows.net` / `privatelink.blob.core.windows.net` zones linked to the
   suite spoke VNet. The app identity is granted **Storage File Data Privileged Contributor** and
   **Storage Blob Data Contributor** on the account. Public network access is disabled only after
   that customer is cut over.
3. **WAF**: a per-URI policy exempting `PUT /api/uploads/*/chunks/*` from request-body
   inspection, so the shared policy can move to Prevention without blocking uploads.
4. **Dev/test storage (Dan's decision):** a dedicated non-customer storage account in the suite
   subscription, with shared-key access disabled, file + blob private endpoints in the suite
   spoke, one test share and one test container, and data roles for the dev identity. Dev runs
   over VPN and exercises the real private path. No developer identity gets roles on customer
   accounts.
5. Defender for Storage malware scanning on upload (recommended).
6. Entra: app registration (redirect URIs for prod + dev port), and **per Gov-cloud customer**
   the cross-tenant setup in our tenant.
7. PostgreSQL `cloudselfservice` database + `_dev` clone and roles on the shared flexible servers.

## 7. Customer onboarding and cutover

- Staff creates the customer, registers targets, sets grants, adds users (by UPN).
- Commercial-cloud customer: invite users as guests → they redeem → sign in.
- **Azure Government customer:** checklist shown in admin — customer IT enables
  "Microsoft Azure commercial cloud" in their Microsoft cloud settings and adds our tenant ID;
  we do the same for theirs; inbound/outbound settings set; users invited **by UPN**. Shipped as
  `docs/gov-cloud-guest-setup.md` so support can send it.
- Customers are notified ahead, test in the portal while public access is still on, confirm,
  then the account goes private (aligned to renewal dates per Krishna's notes). Cutover is
  operational, not a code feature.

## 8. Left to the builder

- Exact component breakdown, file layout within the suite structure, and task ordering
  (rebuild before feature, per the recipe's "render model first" rule).
- Blob folder-create representation; rename/move UI shape; table paging/virtualisation for
  large directories (Azure Files lists in pages — follow continuation tokens).
- Chunk size / parallelism tuning and timeout settings for long downloads behind the ingress.
- Whether `Test access` also checks write permission (e.g. create+delete a probe file).
- Wording/copy of the Help and Access Denied pages.
- Test inventory beyond the mandatory guards: at minimum `StorageAccessService` authorization
  matrix (role × level × operation), path normalisation, identity resolution (guest vs member,
  first-sign-in oid binding), and audit emission.

## 9. Calls made by Claude (not yet reviewed by Dan)

- **No `/auth/service-signin` shared-secret endpoint.** Every other suite app has one for its
  Playwright suite and AppSuite screenshots. On the app that is the front door to customer
  storage, it would be an anonymous route into a staff session. Instead the app owns its own web
  test suite (as TCGenesisArchitect does), signing in through a **Development-environment-only**
  test principal that cannot be enabled in Production (guarded at startup, with a test pinning
  it). AppSuite lists the app without a live screenshot.
- **Each target belongs to exactly one customer** (or none, for internal stamps), and grants may
  only go to the owner. This follows from "each customer's storage lives in their own
  subscription", and it enforces the anti-goal in the schema, not only in code.
- **Staff role defaults to Contribute, not Full.** Delete for staff requires Staff Admin.
- **Downloads are always `attachment`.** No in-browser preview in phase 1, so customer HTML/SVG
  never renders on our origin.
- **Upload overwrite** of an existing file needs Contribute and an explicit confirm in the UI.
  The API refuses overwrite unless the session was created with `overwrite: true`.
- **Old placeholder pages are deleted.** SLA, service health and App Service restart come back
  as later features.

## 10. Phase 2 (for orientation only — not designed here)

OAuth-secured REST API for customer automation (per-customer Entra app registrations / service
principals, read/write API permissions), reusing `StorageAccessService`; possibly fronted by API
Management. Then the restored placeholder features (SLA, service health, App Service restart).
