# Security — Advanced Cloud Self Service

## Auth gates
- Auth is currently disabled (development scaffold). When enabled, every `@page` must have `[Authorize]` or a policy attribute.
- `App.razor` must switch from `<RouteView>` to `<AuthorizeRouteView>` wrapped in `<CascadingAuthenticationState>` when auth is activated.

## Azure API access
- Never hard-code Azure credentials or subscription IDs in source code. Use `appsettings.json` / environment variables / managed identity.
- In production, use managed identity (no stored credentials). In development, use `DefaultAzureCredential` with `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` env vars or `az login`.

## Security headers
The following headers are set in `Program.cs` and must not be removed:
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: SAMEORIGIN`
- `Referrer-Policy: strict-origin-when-cross-origin`

## Destructive actions (restart / stop)
- App Service restart buttons must require appropriate RBAC before executing.
- All restart/stop actions must be logged to an audit trail.
- Never expose a restart endpoint without auth protection.

## Dockerfile
- Final stage runs as `appuser` (UID 1001) — never as root.
- Do NOT use `--no-restore` on `dotnet publish` — it breaks the .NET 10 static-web-asset pipeline.
