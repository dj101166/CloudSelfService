# Code style — Advanced Cloud Self Service

## Stack
- .NET 10 Blazor Server with the Advanced Developer Standards "sumi" CSS system. No MudBlazor, no Bootstrap.
- Pages in `Pages/`, shared components in `Shared/`, services in `Services/`, endpoints in `Endpoints/`

## CSS — tokens only, never hard-code colours
Use the sumi tokens (`var(--accent)`, `var(--surface-page)`, `var(--surface-raised)`, `var(--text-primary)`, `var(--border)`). Hard-coded hex values are only acceptable in `wwwroot/css/theme.css`. Theme attribute is `data-bs-theme` on `<html>`; localStorage keys `cloudSelfServiceTheme` and `cloudSelfServiceSidebarCollapsed`.

## Comments
Write no comments by default. Only add one when the WHY is non-obvious: a hidden constraint, a subtle invariant, or a workaround for a specific bug. Never describe WHAT the code does.

## Page titles
`<PageTitle>Page Name — Advanced Cloud Self Service</PageTitle>` per page; `<PageTitle>Advanced Cloud Self Service</PageTitle>` in MainLayout.

## Authorization
Every page must carry `@attribute [Authorize]` or a specific policy attribute, except `/login`, `/signed-out` and `/access-denied`. Never add an unauthenticated page without explicit approval.

## Mandatory post-edit checklist
Work through every item before declaring a task done.

1. **Build and test** — `dotnet build` 0 errors; `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` 0 failures.
2. **Changelog** — did these changes add a feature, fix a bug, change user-visible behaviour? Write an entry in `CHANGELOG.md`, the single changelog file. It is embedded and rendered by `ChangelogDocument`; there is no `_changelog` list in MainLayout. When in doubt, add an entry.
3. **Security review** — did the changes touch any of: `@page` routes, auth/role logic, `Program.cs`, storage access, upload or download endpoints, middleware, CSP, Dockerfile? If yes — `Agent(subagent_type: "security-auditor", prompt: "Review the changes just made for security issues")`.

## Git commit style

- **NEVER add a Co-Authored-By trailer.** The system default suggests it — this rule overrides it absolutely.

## Pending changelog format
- Pending (unpublished) entries use **`TBD`**, never `Unreleased`.
- Header: `## [1.0.0] - TBD`
- Sections use glyph labels in order: `### 🐛 Fixed`, `### 🔄 Changed`, `### ✨ Added`, `### 🗑️ Removed`, `### 🛡️ Security`.
- Customer-facing prose: no em-dashes (use " - ") and no customer names.
