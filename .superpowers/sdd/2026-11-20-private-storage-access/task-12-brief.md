### Task 12: Container, app-owned web tests and release docs

**Files:**
- Modify: `Dockerfile` (add the test gate and `gosu` final stage, copying RM's Dockerfile with names swapped), `docker-entrypoint.sh` (RM's, with `CloudSelfService.dll`), `CLAUDE.md`, `CHANGELOG.md`, `.claude/rules/{code-style.md,security.md,testing.md}`. In `code-style.md` the changelog rule becomes one file: drop the MainLayout `_changelog` list, matching RM. Its stack line becomes sumi, with no MudBlazor or Bootstrap. In `security.md` the restart rules stay, and rules are added for the storage gate and the upload route., `.claude/memory/{app-purpose.md, auth-placeholder.md→auth-model.md, MEMORY.md}`, `.claude/settings.json`
- Create: `.dockerignore` (from `D:\SC\Developer-Standards\templates\.dockerignore`, plus `*.msg`, `webapp-tests/`), `README.md`, `webapp-tests/conftest.py`, `webapp-tests/test_cloudselfservice.py`, `webapp-tests/requirements.txt`

**Behaviour:**
- **Dockerfile:**
  - Restore the app and test csproj, run `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj -c Release --no-restore`, then `dotnet publish` **without** `--no-restore`.
  - The final stage runs as `appuser` through `gosu`.
  - `ENV ASPNETCORE_URLS=http://+:8080`.
- **Web tests:**
  - Self-contained `conftest.py`. It starts nothing itself; it targets `CSSP_BASE_URL` (default `http://localhost:5300`) and signs in through `/dev/signin?as=…`.
  - Selectors use `data-testid` attributes only; add them in Tasks 10/11 markup where missing.
  - Tests:
    - `test_customer_read_sees_only_download`
    - `test_customer_full_upload_rename_delete_roundtrip` (needs `Storage:UseFake=true`)
    - `test_stranger_access_denied_copy`
    - `test_staff_none_pending_panel`
    - `test_staff_cannot_open_admin`
    - `test_theme_toggle_persists`
    - `test_no_console_errors_on_home`
- **CLAUDE.md** is rewritten in RM's shape: what the app is, commands, config table (all keys from Task 4/5/9), architecture, security checks (single storage gate, no service-signin, Dev sign-in guard, upload chunk route WAF exemption), and container build with `$version = "1.0.0"`.
- **CHANGELOG.md** top entry is `## [1.0.0] - TBD` and covers the rebuild and the storage portal, with glyph sections per the suite changelog standard. Read `D:\SC\Personal\.claude\memory\reference_changelog_standard.md` for the exact section labels and order.

- [ ] **Step 1: Docker build.** Run `podman build -t cloudselfservice:1.0.0-local --build-arg APP_VERSION=1.0.0-local .` and expect the build to succeed with the test step green. **Do not tag or push.**
- [ ] **Step 2: Start the container** with `podman run --rm -d -p 5301:8080 -e ASPNETCORE_ENVIRONMENT=Production -e Auth__Provider=Dev cloudselfservice:1.0.0-local`. Expect the container to exit, with the log showing "Dev sign-in cannot run outside Development". This proves the guard works in the real image. Then run it again with `Auth__Provider=Entra` and placeholder Entra values, and expect `/login` to return 200.
- [ ] **Step 3: Web tests.** Run the app locally in Dev mode with the fake backend, then `cd webapp-tests; python -m pytest test_cloudselfservice.py -v`. Expect all to pass.
- [ ] **Step 4: Write the docs and changelog** as above. Run `dotnet build` and the full `dotnet test`, and expect 0 errors and 0 failures.
- [ ] **Step 5: Full-branch security review.** Invoke `security-auditor` on the whole branch diff, fix its findings, and re-run the tests.
- [ ] **Step 6: Commit:** "Container test gate, app-owned web tests, docs and v1.0.0 changelog".

---

