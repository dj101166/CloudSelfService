# Changelog - Advanced Cloud Self Service

## [1.0.0] - TBD

First release. Advanced Cloud Self Service is now a private storage access portal: customers and Advanced staff sign in with Microsoft and browse, upload and download files held in Advanced-hosted Azure storage, without ever being given a storage key, a SAS link or a direct storage address.

### 🔄 Changed
- The application has been rebuilt on the Advanced Developer Standards interface. It has a light, dark and system theme chosen from the account menu, a collapsible sidebar, and a release notes panel opened from the version chip in the header.

### ✨ Added
- Signing in now survives a restart or new release of the portal, so people are no longer signed out and uploads in progress are no longer interrupted.
- Sign in with Microsoft (Entra, single tenant). Advanced staff are admitted on first sign-in with no role and see an "access pending" panel until an administrator grants one. Customers (invited guests, including guests from Azure Government organisations) are admitted only when Advanced has already registered them. Anyone else lands on an Access Denied page that tells them to contact Advanced support. Every sign-in, admitted or refused, is written to the audit log.
- An account menu showing who is signed in, with Sign out. An idle session is signed out after a period of inactivity (60 minutes by default) with a countdown warning, and every session ends after 12 hours regardless. A deactivated or demoted user loses access within a minute, even mid-session.
- A dropped server connection recovers by itself (the page reloads) instead of leaving a stuck "disconnected from server" dialog, including after a container stop and start.
- Files home: one card per share or container you can reach, with its kind and your access level. Advanced staff see every customer's, grouped by customer (internal storage under "Advanced internal"), with a filter box.
- File browser: breadcrumb, folders then files with size and local modified time, "Load more" for very large folders, and actions by access level. Read allows download. Contribute adds upload by button or drag and drop, new folder, and rename or move. Full adds delete, with the folder's name typed to confirm a folder delete.
- Chunked, resumable uploads of files up to 50 GB. Each 4 MiB piece is checked by MD5, an interrupted upload resumes where it stopped when the same file is selected again, and abandoned uploads are cleaned up after 24 hours. The upload panel shows progress with pause, resume and cancel, asks before replacing an existing file, and retries a dropped piece before pausing.
- The upload size limit is adjustable by administrators up to 195.3 GiB, the most Azure can commit to one blob. A higher setting is reduced to that cap.
- Downloads stream through the portal as attachments with range support, and access is re-checked during a long download.
- Help page: signing in as an invited guest, what Read, Contribute and Full allow, large uploads, and contacting Advanced support.
- Staff admin: Customers (add, edit, users by UPN, per-share access levels, and the cross-tenant checklist for Azure Government customers), Storage targets (register shares and containers, with a Test access button), Staff (Owner only: assign Staff or Staff Admin; Owners are set by the deployment) and an Audit log with filters and a CSV export. Every admin change is audited. `docs/gov-cloud-guest-setup.md` is the checklist to send an Azure Government customer's IT.
- A container build that runs the full test suite and fails if any test fails, an operations guide (`docs/operations.md`), and an app-owned browser test suite in `webapp-tests/`.

### 🗑️ Removed
- The Dashboard, Service Health and App Services placeholder pages, replaced by the Files home and the file browser.

### 🛡️ Security
- The audit log can no longer be altered or deleted. The database itself refuses to change or remove audit entries, even for the portal's own account.
- Every storage operation passes through one access check. Customers see only what Advanced has granted them, at the level it was granted, and file paths are validated so nobody can step outside their share.
- No storage key, SAS token or direct storage address ever reaches a browser. Downloads are always attachments with a generic content type.
- Hardened HTTP security headers: framing denied, a strict content security policy that allows only the portal's own scripts, connections and images, Permissions-Policy, form-action 'self', and HSTS for one year. All file pages are marked no-store.
- Development sign-in and the fake storage backend are refused outside the Development environment, and the application will not start if either is switched on in production.
- Admin hardening: deactivating or demoting a user can no longer be undone by a sign-in racing it. Owner is derived from configuration and never stored. CSV export cells are all quoted and neutralised against spreadsheet formulas. Registering storage can be limited to approved Azure subscriptions, and only an Owner can register internal storage. UPNs compare in one normalised form (NFKC, lowercase) in code and database. A customer's first sign-in must come from their recorded home tenant, and not while cross-tenant setup is pending. Test access times out after 20 s with a 5 s cooldown, the audit export is limited to 10 per minute and marks truncation, and the database refuses repointing a granted target's storage. Azure Government storage endpoints are not allowed by default.
- Customer-user UPNs and new file names containing an unpaired UTF-16 surrogate or private-use character are rejected.
- Upload and download requests are rate limited per user and overall, and uploads that stall below a minimum speed are ended.
