# CLAUDE.md — Advanced Cloud Self Service

## What this project is

Blazor Server (.NET 10, MudBlazor 9) application that lets clients view their Advanced-hosted environment: SLA uptime, service health status, and self-service controls (e.g. restart an Azure App Service).

**Status:** Initial scaffold — data sources (Azure Resource Management / Application Insights) not yet connected.

## Commands

```powershell
dotnet run            # http://localhost:5300
dotnet build
```

## Container build

```powershell
$version = "0.1.0"
podman build -t cloudselfservice:$version --build-arg APP_VERSION=$version .
podman tag cloudselfservice:$version auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:latest
podman push auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:latest
```

## Architecture

- **Pages/**: `Index.razor` (Dashboard), `ServiceHealth.razor`, `AppServices.razor`
- **Shared/**: `MainLayout.razor` (topbar + sidebar), `NavMenu.razor`
- **Services/**: `AppVersionService.cs` (surfaces `APP_VERSION` env var in topbar)
- **wwwroot/**: `css/site.css` (brand palette + dark mode), `js/theme-manager.js`

## Auth placeholder

Auth is intentionally disabled during initial development. The `App.razor` uses a plain `<Router>` with `<RouteView>` (no `CascadingAuthenticationState` or `AuthorizeRouteView`). Pages do **not** carry `@attribute [Authorize]`.

### To enable GitHub OAuth

1. Add `Microsoft.AspNetCore.Authentication.OpenIdConnect` to the `.csproj`.
2. Add config to `appsettings.Development.json` (git-ignored):
   ```json
   { "GitHub": { "ClientId": "", "ClientSecret": "", "Organization": "Advanced-Utility" } }
   ```
3. Follow `EscapedDefects/Program.cs` for the full OAuth round-trip wiring.
4. Add `@attribute [Authorize]` to all pages.
5. Replace `<RouteView>` with `<AuthorizeRouteView>` (and `<CascadingAuthenticationState>` wrapper) in `App.razor`.
6. Update `.claude/settings.json` to re-enable the page-authorization hook.

### To enable Microsoft Entra OIDC

Same pattern as GitHub above but follow the Entra section in `EscapedDefects/Program.cs`.

## Configuration (appsettings.json or Azure App Service settings)

| Key | Purpose |
|---|---|
| `APP_VERSION` | Shown in topbar version chip |
| `ASPNETCORE_ENVIRONMENT` | `Development` locally, `Production` in container |

Future keys (add when connecting data sources):
- `Azure:SubscriptionId`, `Azure:TenantId` — for Resource Management API calls
- `ApplicationInsights:ConnectionString` — for SLA / uptime data

## Code style

- CSS tokens only — never hard-code hex in component code.
- Page titles: `<PageTitle>Page Name — Advanced Cloud Self Service</PageTitle>`
- Once auth is enabled: every page must carry `@attribute [Authorize]` or a policy attribute.

## Mandatory post-edit checklist

1. **Build** — run `dotnet build`. Confirm 0 errors.
2. **Changelog** — add an entry if the change adds a feature, fixes a bug, or changes user-visible behaviour. Update both `CHANGELOG.md` and the `_changelog` list in `Shared/MainLayout.razor`.
3. **Security review** — if auth, routes, `Program.cs`, or Azure API calls change: `Agent(subagent_type: "security-auditor", ...)`.

## Plan files

Plans are saved automatically into `.claude/plans/` by the `save-plan-to-project` PostToolUse hook.
