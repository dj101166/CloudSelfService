---
name: auth-placeholder
description: Auth is disabled during development; stubs ready for GitHub OAuth and Entra OIDC
metadata:
  type: project
---

Auth is intentionally disabled during initial development.

Current wiring:
- `App.razor` uses plain `<Router>` / `<RouteView>` (no CascadingAuthenticationState)
- Pages do NOT have `@attribute [Authorize]`
- `Program.cs` has no auth middleware; placeholder comment block shows where to wire it

**Why:** Owner wants to build and test the data-visualization features before adding auth friction.

**How to apply:** When auth is requested, follow the instructions in `CLAUDE.md` under "Auth placeholder". The `.claude/settings.json` page-authorization hook is also disabled and should be re-enabled at that time. Reference implementation: `EscapedDefects/Program.cs`.
