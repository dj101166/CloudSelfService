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

### 🛡️ Security
- Hardened HTTP security headers: added Permissions-Policy and CSP form-action 'self'; HSTS max-age set to 1 year.
