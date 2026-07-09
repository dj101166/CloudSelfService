# /publish

Build and push the Advanced Cloud Self Service container image, preceded by a full review. Follows the same pipeline as the orchestrator `/publish` but scoped to this single app.

## Pre-flight check

1. Read the top entry in `CHANGELOG.md`. If the date is **not** `TBD`, this version is already published — stop.
2. Verify `CLAUDE.md`'s `$version =` matches the top `CHANGELOG.md` entry version. Stop and report any mismatch.

## Pipeline

### Step 1 — Stamp release date + commit

a) Replace `TBD` with today's date (e.g. `June 4, 2026`) in **both** changelog files:
   - `CHANGELOG.md` top entry date
   - `Shared/MainLayout.razor` `_changelog` matching entry `"TBD"` string

b) Stage all uncommitted tracked changes (skip: `appsettings.Development.json` and any local-only data / export directories).

c) Commit: `Publish v{version} — <brief summary of changes>`

### Step 2 — Push to GitHub

`git push` this app's repo to its GitHub remote (`master`).

### Step 3 — Full review

Invoke this app's `/full-review` skill (all five phases: security audit, unit test gaps, web test gaps, unit test execution, web test execution).

**Blocking (stop publish):** Unresolved HIGH security finding, any unit test failure, any real web test failure.

**Non-blocking (ask user, then continue):** Unit test gaps or web test gaps found.

### Step 4 — Build and push container image

Use the exact `podman build / tag / push` commands from this app's `CLAUDE.md` Container Build section, substituting the current `$version`:

```powershell
podman build -t cloudselfservice:$version --build-arg APP_VERSION=$version .
podman tag cloudselfservice:$version auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:latest
podman push auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice:latest
```

### Step 5 — Bump CLAUDE.md + final commit + push

1. Bump `CLAUDE.md` `$version =` to the next patch version (e.g. `0.1.0` → `0.1.1`).
2. Commit: `Post-publish: bump CLAUDE.md to v{nextVersion}`
3. `git push`
