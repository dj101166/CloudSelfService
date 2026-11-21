# Changelog — Advanced Cloud Self Service

## [1.0.0] - TBD

Initial (unreleased) scaffold. This app has never been published, so all work to date is collected here as the pending v1.0.0.

### ✨ Added
- Initial scaffold: Blazor Server (.NET 10, MudBlazor 9) skeleton matching the suite brand palette (cyan `#019EC0` accent, navy-to-teal sidebar, full dark-mode support).
- Topbar with full Advanced logo, version chip that opens release-notes panel, and a Light/Dark toggle persisted via `localStorage` key `cloudSelfServiceTheme`.
- Sidebar navigation: Dashboard (`/`), Service Health (`/service-health`), App Services (`/app-services`).
- Sign in with Microsoft (Entra, single tenant). Advanced staff are admitted on first sign-in with no role and see an "access pending" panel until an administrator grants one; customers (B2B guests) are admitted only when Advanced has already registered them, and anyone else lands on an Access Denied page telling them to contact Advanced support. Every sign-in, admitted or refused, is written to the audit log.
- Account menu shows who is signed in and offers Sign out; an idle session is signed out after `Auth:IdleTimeoutMinutes` with a countdown warning.
- A dropped Blazor server connection auto-recovers (auto-reload) instead of showing the "disconnected from server" dialog, and the reconnect dialog self-heals after a container stop/start rather than staying stuck until the tab is closed.
- Chunked, resumable uploads of files up to 50 GB: each 4 MiB piece is MD5-checked by storage, an interrupted upload resumes where it stopped when the same file is selected again, and abandoned uploads are cleaned up after 24 hours.
- The upload size limit (`Transfers:MaxUploadBytes`) is capped at 195.3 GiB, the most Azure can commit to one blob (50,000 blocks of 4 MiB); a higher configured value is reduced to the cap with a startup warning.
- Files home: one card per share or container you can reach, with its kind and your access level. Advanced staff see every customer's, grouped by customer (internal storage under "Advanced internal"), with a filter box.
- File browser: breadcrumb, folders then files with size and local modified time, "Load more" for very large folders, and actions by access level - download (Read); upload by button or drag and drop, new folder, rename and move (Contribute); delete, with the folder's name typed to confirm a folder delete (Full).
- Upload panel with progress, pause, resume and cancel. Replacing an existing file asks first. A dropped connection retries each piece, then pauses; Resume, or selecting the same file again, carries on from where it stopped.
- Help page: signing in as an invited guest, what Read, Contribute and Full allow, large uploads, and contacting Advanced support.
- Staff admin: Customers (add, edit, users by UPN, per-share access levels, and the cross-tenant checklist for Azure Government customers), Storage targets (register shares and containers, with a Test access button), Staff (Owner only: assign Staff or Staff Admin; Owners come from configuration) and an Audit log with filters and a CSV export. Every admin change is audited. `docs/gov-cloud-guest-setup.md` is the checklist to send an Azure Government customer's IT.

### 🛡️ Security
- Hardened HTTP security headers: added Permissions-Policy and CSP form-action 'self'; HSTS max-age set to 1 year.
- Customer-user UPNs and new file names containing an unpaired UTF-16 surrogate or private-use character are now rejected; an audit-export filter dated 9999-12-31 no longer fails.
