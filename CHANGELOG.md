# Changelog — Advanced Cloud Self Service

## [0.1.0] - TBD

### ✨ Added
- Initial scaffold: Blazor Server (.NET 10, MudBlazor 9) skeleton matching the suite brand palette (cyan `#019EC0` accent, navy-to-teal sidebar, full dark-mode support).
- Topbar with full Advanced logo, version chip that opens release-notes panel, and a Light/Dark toggle persisted via `localStorage` key `cloudSelfServiceTheme`.
- Sidebar navigation: Dashboard (`/`), Service Health (`/service-health`), App Services (`/app-services`).
- Auth (GitHub OAuth / Microsoft Entra) intentionally NOT yet wired — scaffolded with placeholder comments for future addition.
