# Code style — Advanced Cloud Self Service

## Stack
- .NET 10 Blazor Server + MudBlazor 9 + Bootstrap 5 (cards, grid) + Open Iconic (sidebar icons)
- Pages in `Pages/`, shared components in `Shared/`, services in `Services/`

## CSS — tokens only, never hard-code colours
Use `var(--accent-primary)`, `var(--surface-1)`, `var(--surface-2)`, `var(--text-color)`, `var(--border-color)`, `var(--bg-page)`. Hard-coded hex values are only acceptable in `wwwroot/css/site.css` where those tokens are defined.

## Comments
Write no comments by default. Only add one when the WHY is non-obvious: a hidden constraint, a subtle invariant, or a workaround for a specific bug. Never describe WHAT the code does.

## Page titles
`<PageTitle>Page Name — Advanced Cloud Self Service</PageTitle>` per page; `<PageTitle>Advanced Cloud Self Service</PageTitle>` in MainLayout.

## Authorization (when auth is enabled)
Every page must carry `@attribute [Authorize]` or a specific policy attribute. Never add an unauthenticated page without explicit approval. See CLAUDE.md for instructions on enabling auth.

## Mandatory post-edit checklist
Work through every item before declaring a task done.

1. **Build** — run `dotnet build`. Confirm 0 errors.
2. **Changelog** — did these changes add a feature, fix a bug, change user-visible behaviour? Write an entry in both `CHANGELOG.md` and the `_changelog` list in `Shared/MainLayout.razor`. When in doubt, add an entry.
3. **Security review** — did the changes touch any of: `@page` routes, auth/role logic, `Program.cs`, Azure API calls, middleware? If yes — `Agent(subagent_type: "security-auditor", prompt: "Review the changes just made for security issues")`.
