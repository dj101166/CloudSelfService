# Operations guide - Advanced Cloud Self Service

For the person deploying or running the app. Settings are App Service application settings (`Section__Key`) or `appsettings.json` keys.

## Configuration keys

### ConnectionStrings

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:Default` | none | PostgreSQL. Required outside Development: the app refuses to start without it ("ConnectionStrings:Default is required outside Development."). In Development, unset means in-memory stores and a startup warning: nothing persists. An unreachable database fails the start on purpose. The schema is created and upgraded at start. |

### Auth

| Key | Default | Notes |
|---|---|---|
| `Auth:Provider` | `Entra` | `Dev` is Development only; the app refuses to start with it elsewhere. |
| `Auth:IdleTimeoutMinutes` | 60 | Inactivity sign-out. |
| `Auth:IdleWarningSeconds` | 120 | Countdown shown before the idle sign-out. |
| `Auth:AbsoluteSessionHours` | 12 | A session ends this long after sign-in regardless of activity. |
| `Auth:RevalidateSeconds` | 60 | How often a live session re-checks that the user (and, for a customer, their customer) is still active, that the record is still bound to the signed-in object id, and that the absolute session lifetime has not passed. Roles are not cached: every page and action re-reads them. |

### Data Protection

| Key | Default | Notes |
|---|---|---|
| `DATA_ROOT` | none | A persistent directory (the Azure Files mount). Data Protection keys are stored in `dp-keys` beneath it, so sign-ins and in-flight uploads survive a restart. Unset means ephemeral keys and a startup warning. If the directory cannot be created the app logs a warning and continues with ephemeral keys. |

### Entra

| Key | Default | Notes |
|---|---|---|
| `Entra:TenantId`, `Entra:ClientId`, `Entra:ClientSecret` | empty | App registration in Advanced's tenant. Keep the secret in App Service settings or Key Vault. |
| `Entra:Instance` | `https://login.microsoftonline.com` | |
| `Entra:CallbackPath` | framework default | Must match the registered redirect URI. |
| `Entra:StaffEmailDomain` | `advancedutility.com` | Users with this domain are staff and are created with no role on first sign-in. |

### Authorization

| Key | Notes |
|---|---|
| `Authorization:Owners` | Comma-separated UPNs. Owner is derived from this list on every role check (page, menu and action) and is never stored, so an Owner's own record shows no role. At least one is needed to assign any staff role; outside Development the app logs "No Owners configured; nobody can assign staff roles." at start when the list is empty. |

### Storage

| Key | Default | Notes |
|---|---|---|
| `Storage:AllowedEndpointSuffixes` | `core.windows.net` | Admins can only register storage whose endpoint ends in one of these. Azure Government (`core.usgovcloudapi.net`) is off unless added. |
| `Storage:AllowedSubscriptions` | empty | If set, only these subscription IDs can be registered. A malformed entry fails the start. |
| `Storage:UseFake` | false | In-memory backend. Development only. |

### Admin and Support

| Key | Default | Notes |
|---|---|---|
| `Admin:TestAccessTimeoutSeconds` | 20 | Limit for the Test access button (5 s cooldown per admin, across all targets). |
| `Admin:AuditExportsPerMinute` | 10 | Audit CSV export rate limit. |
| `Support:Contact` | "your Advanced account team" | Shown on Help and Access Denied. |

### Transfers

| Key | Default | Notes |
|---|---|---|
| `Transfers:MaxUploadBytes` | 53687091200 (50 GiB) | Capped at 195.3 GiB, the Azure block limit; a higher value is reduced, with one warning at startup. |
| `Transfers:ParallelChunks` | 4 | Browser-side parallel 4 MiB pieces per upload. |
| `Transfers:SessionHours` | 24 | Lifetime of an upload session. |
| `Transfers:MaxOpenSessionsPerUser` | 8 | Unfinished uploads per user. |
| `Transfers:IdleSessionMinutes` | 120 | An upload with no piece for this long is abandoned. |
| `Transfers:CompletingTimeoutMinutes` | 30 | Time allowed for the final commit. |
| `Transfers:BlobLockMinutes` | 15 | Lock on a target file while one session writes it. |
| `Transfers:MaxConcurrentChunksPerUser` | 16 | In-flight pieces per user. |
| `Transfers:MaxConcurrentChunksTotal` | 64 | In-flight pieces across the app. |
| `Transfers:ChunksPerMinute` | 600 | Piece rate limit per user. |
| `Transfers:MinChunkBytesPerSecond` | 65536 | Pieces arriving slower than this are ended. |
| `Transfers:RequestsPerMinute` | 120 | Request rate per user for downloads and the upload Begin, Status, Complete and Abort calls. Pieces have their own limits above. |
| `Transfers:MaxConcurrentPerUser` | 4 | Concurrent requests per user across the same calls (downloads and upload Begin, Status, Complete, Abort). |
| `Transfers:ReauthorizeBytes` | 67108864 (64 MiB) | A running download re-checks access after this many bytes... |
| `Transfers:ReauthorizeSeconds` | 60 | ...or this many seconds. |

## Things an operator must know

- **Empty parent folders on Files.** Deleting or moving the last item in a folder can leave the (empty) parent folder behind on an Azure Files share. It is cosmetic; folders can be deleted by a Full user.
- **Abandoned one-piece uploads hold quota.** An upload that sends one piece and is then abandoned reserves the full logical file size against the user's quota until the session expires, up to 24 hours. This is bounded by the per-user byte cap and the open-session cap.
- **Audit fails open.** If the audit store cannot be written, the user's action still proceeds and the failure is logged. Watch the application log for audit write errors.
- **Idle sign-out is client-side.** The idle timer runs in the browser. An active upload counts as activity and keeps the session alive; the absolute session limit still applies.
- **IP trust depends on the App Gateway.** Forwarded headers are trusted without a proxy allow-list, so the audited client IP is only reliable if the App Gateway is the only way to reach the app. Do not expose the App Service directly.
- **Reconnect IP path.** The IP recorded for actions taken on a live circuit after a reconnect uses a different code path from the first request. Spot-check at deploy: sign in, drop the connection, act, and confirm the audit row's IP.
- **Clean the `_dev` database before the first deploy.** The `_dev` database must not hold Open upload sessions from pre-release commits. Run `TRUNCATE upload_sessions, upload_chunks;` there first.
- **The audit log is append-only in the database.** A trigger rejects UPDATE, DELETE and TRUNCATE on `audit_events`, even for the owning role. The `_dev` truncation above is unaffected, because the upload tables are not protected. If old audit rows ever need purging, a DBA must disable the trigger deliberately (`ALTER TABLE audit_events DISABLE TRIGGER audit_events_no_change`, and `audit_events_no_truncate` for TRUNCATE) and re-enable it afterwards.
- **Verify the key folder after deploy.** Check that a `dp-keys` folder appears on the Files share behind `DATA_ROOT`. If the mount is missing, the keys are silently written to the container disk and sign-ins will not survive a restart. The keys on the share are not encrypted (the suite pattern), so protect the share.
- **Test databases accumulate audit rows.** In a throwaway database used for the `CSSP_TEST_PG` tests, audit rows pile up because TRUNCATE is blocked. Drop the database to clean up.
- **Malware scan results are not shown (deferred).** Defender for Storage on-upload scanning records its verdict as a blob index tag (`Malware Scanning scan result`). Reading index tags needs the `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read` data action, which the app's role, Storage Blob Data Contributor, does not include (Storage Blob Data Owner does). Rather than widen the managed identity to Owner on customer accounts, the app does not read or display the tag in v1.0.0. Scanning itself is unaffected: Defender's alerts and any automatic response configured there still apply. To revisit, grant a custom role adding only `blobs/tags/read` and read the tag in the blob listing.
- **WAF.** The App Gateway WAF needs a body-inspection exemption for `PUT /api/uploads/{id}/chunks/{index}` (4 MiB bodies).
- **Port.** In a container the app listens on 8080 (`ASPNETCORE_URLS`); locally on 5300 via `Kestrel:Endpoints:Http:Url`.

## Test access diagnostics

Test access (staff admin) and the application log show why the portal could not reach a storage account. Customers only ever see the general "not authorised" message.

| Diagnostic | Meaning | Fix |
|---|---|---|
| `AuthorizationPermissionMismatch` | The managed identity has no data role on the account. | Add the account to `storageAccessTargets` in Pulumi and apply; allow 5-10 minutes for role propagation. |
| `AuthorizationFailure` | The account's network rules blocked the request. | Give the account a private endpoint the portal can reach (`storageAccessTargets`) and confirm its private DNS resolves. |
| Could not obtain an Azure token | No managed identity is available, or a local developer is not signed in to Azure. | Enable the App Service managed identity; locally run `az login`. |

## Customers in Azure Government

See `gov-cloud-guest-setup.md` for the checklist to send the customer's IT administrator.
