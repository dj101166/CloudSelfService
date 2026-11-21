# Security — Advanced Cloud Self Service

## Who signs in
- Microsoft Entra only. Users are Advanced staff (recognised by `Entra:StaffEmailDomain`; auto-created with no role) and customers (B2B guests, including cross-cloud Azure Government guests, admitted only when pre-registered). Owner comes from `Authorization:Owners`, never from the database.
- There is no `/auth/service-signin` and no shared-secret or API-key sign-in. Do not add one.
- Every `@page` must have `[Authorize]` or a policy attribute, except `/login`, `/signed-out`, `/access-denied`. `Routes.razor` uses `AuthorizeRouteView` inside `CascadingAuthenticationState`.
- Sessions are revalidated against the database (`Auth:RevalidateSeconds`) so deactivation and demotion take effect mid-session.

## Single storage gate
- Every storage read or write goes through `StorageAccessService`, which checks the caller's grant and validates the path. Never create a storage client anywhere else.
- No storage key, SAS token or storage URL may reach a browser. Downloads are always `Content-Disposition: attachment` with `application/octet-stream`.
- Files access uses `ShareTokenIntent.Backup`. Storage credentials come from managed identity / `DefaultAzureCredential`; never hard-code them.

## Development-only features
- `Auth:Provider=Dev` (`/dev/signin`) and `Storage:UseFake=true` are Development only. `DevSignIn.AssertSafe` must keep throwing at startup in any other environment. Never register the dev endpoints or personas otherwise.

## Security headers
Set in `Program.cs` and must not be removed or loosened:
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY`, with CSP `frame-ancestors 'none'`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Permissions-Policy`, HSTS 365 days
- CSP: `default-src 'self'`, no `'unsafe-eval'`, `connect-src 'self'`, `img-src 'self' data:`, `object-src 'none'`, `base-uri 'self'`, `form-action 'self'`
- File pages are `Cache-Control: no-store`.

## Upload route
- `PUT /api/uploads/{id}/chunks/{index}` takes 4 MiB bodies. It stays antiforgery-protected, rate limited, session-bound and size-checked. The App Gateway WAF needs a body-inspection exemption for this route; do not widen it beyond the route.
- Client IP for audit comes from forwarded headers, trusted only because the App Gateway is the single ingress.

## Destructive actions (restart / stop) — for future features
- App Service restart buttons must require appropriate RBAC before executing.
- All restart/stop actions must be logged to an audit trail.
- Never expose a restart endpoint without auth protection.

## Dockerfile
- Final stage runs as `appuser` (UID 1001) through `gosu` — never as root for the app process.
- Do NOT use `--no-restore` on `dotnet publish` — it breaks the .NET 10 static-web-asset pipeline.
- The build runs the test suite; do not weaken that gate.
