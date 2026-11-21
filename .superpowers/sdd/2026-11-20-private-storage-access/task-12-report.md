# Task 12 report

Commit ceae75c on feat/private-storage-access.

## Done
- Dockerfile: restores app + test csproj, `dotnet test -c Release --no-restore` gate, publish without --no-restore. Added `mkdir -p /run/sshd` (the entrypoint's sshd exited 255 without it; template bug, found by the Step 2 run).
- .dockerignore: removed ThirdPartyNotices/ (R42b), added webapp-tests/ (*.msg already present).
- spark-md5 banner prepended (R42a); UploaderScriptTests asserts it.
- webapp-tests/ (conftest, test file, requirements, pytest.ini); AppMenu theme items got data-testid theme-light/dark/system.
- CHANGELOG v1.0.0 rewritten as one coherent note (Changed/Added/Security), no em-dashes; CLAUDE.md, README.md, docs/operations.md, rules (R7, R15 CSP note), memory (app-purpose, auth-model replacing auth-placeholder, MEMORY.md; user-profile MEMORY.md synced).
- settings.json NOT edited (R6): `Bash(dotnet test*)` is still not in the allow list.

## Tests
- dotnet build 0 warnings/0 errors; dotnet test 1110 passed / 15 skipped / 0 failed.

## Podman
- Build 1 ok (tests inside build: 1110 passed). Run Dev/Production: container exited 255 on sshd, fixed (/run/sshd). Build 2 ok (1110 passed).
- Production + Auth__Provider=Dev: exited 139 with "Unhandled exception. System.InvalidOperationException: Dev sign-in cannot run outside Development".
- Production + Entra placeholder values: stayed Up; PID 1 = `dotnet` as appuser; /login returned 200 with X-Frame-Options DENY and the tightened CSP (checked from inside the container via /dev/tcp, because the host's published port 5301 refused connections from Windows localhost - a podman-machine port-forward issue, not the app).
- Containers and image cloudselfservice:1.0.0-local removed. No tag/push/login.

## Web tests (app on 5397, Dev + fake storage)
7 passed in 7 s. Notes: the home page never reaches networkidle (Blazor long-polling fallback), so tests use domcontentloaded + expect(); the console test ignores the dev-only /_framework/dotnet-browser-tools/hot-reload-settings.json 404 (dotnet run only). Test for stranger uses /dev/signin?as=stranger and asserts Access Denied copy ("Contact Advanced support"). App processes killed; port 5397 free.

## Concerns
- A response header `Content-Security-Policy: frame-ancestors 'self'` is emitted in addition to the app's CSP (seen in the container). Browsers enforce the intersection, so 'none' still wins, but something (framework antiforgery/Blazor) adds a second header; worth a glance in the security audit.
- Container run emits no Application Insights/other config checks; Entra values were placeholders so real OIDC redirect not exercised.
- Security-auditor not run (R5).

## Fix round 1
- Web tests: unique `roundtrip-<uuid8>.txt` per run with best-effort cleanup in `finally`; shares selected by new `data-target-name` hook on target cards (Index.razor). Two consecutive runs against a fresh app: 7 passed, 7 passed.
- Program.cs: `AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = "'none'")`; pinned in PageAuditContextTests.Program_RegistersPageAuditContextForPersistence. (Live header not re-inspected after the change; only the static pin test covers it.)
- CHANGELOG: placeholder pages moved to Removed; config keys removed from customer-facing bullets.
- Build 0 warnings/0 errors; 1110 passed / 15 skipped. Processes killed.
