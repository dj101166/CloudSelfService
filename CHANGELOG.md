# Changelog — Advanced Cloud Self Service

## [0.1.1] - TBD

### 🐛 Fixed
- **Reconnect dialog no longer gets stuck after a stop/start** — when the app's container is stopped and started the live connection drops and the server-side session is lost, which previously left the "Reconnecting to the server…" dialog up until the tab had to be closed. The page now waits for the server to come back and reloads itself automatically to restore a working session.

## [0.1.0] - July 13, 2026

### ✨ Added
- Initial scaffold: Blazor Server (.NET 10, MudBlazor 9) skeleton matching the suite brand palette (cyan `#019EC0` accent, navy-to-teal sidebar, full dark-mode support).
- Topbar with full Advanced logo, version chip that opens release-notes panel, and a Light/Dark toggle persisted via `localStorage` key `cloudSelfServiceTheme`.
- Sidebar navigation: Dashboard (`/`), Service Health (`/service-health`), App Services (`/app-services`).
- Auth (GitHub OAuth / Microsoft Entra) intentionally NOT yet wired — scaffolded with placeholder comments for future addition.
- A dropped Blazor server connection now auto-recovers (auto-reload) instead of showing the "disconnected from server" dialog that required a manual page refresh.

### 🛡️ Security
- Hardened HTTP security headers: added Permissions-Policy and CSP form-action 'self'; HSTS max-age set to 1 year.
