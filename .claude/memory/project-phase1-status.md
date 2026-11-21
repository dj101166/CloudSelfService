---
name: project-phase1-status
description: Phase 1 (private storage portal) shipped as v1.0.0 on 2026-11-21 and was validated against a private-only storage account; open go-live items before customer cut-over.
metadata:
  type: project
---

Phase 1 shipped as **v1.0.0 on 2026-11-21** and is deployed. Image `cloudselfservice:1.0.0` is in ACR, the app is on `master`, the Pulumi entry is live, and IAS-AI is synced (commit `89bf074aa`).

**Validated 2026-11-21 (Dan), private-only proof of concept:**
- Test account: `auscacprdstacrdm11a6ce42` in RG `AUS-CAC-PRD-RG-RDMIG`, registered under `storageAccessTargets` with `services: [file, blob]`.
- With the account's public network access **Disabled**:
  - the Azure portal and Storage Explorer were blocked;
  - the app still browsed, uploaded and refreshed file share and blob container content.
- The path works end to end: private endpoints in the suite spoke, privatelink DNS zones linked to the suite VNet, and data roles on the managed identity.

**Open before real customers are cut over** (detail in `docs/operations.md` and the build's `.superpowers/sdd/.../task-13-report.md`):
1. **App Service access restriction (R48/R49).**
   - **Unresolved, and it blocks go-live.** Direct `*.azurewebsites.net` access bypasses the App Gateway WAF and lets a caller forge the audited `X-Forwarded-For` IP.
   - Dan is choosing between three options:
     - `Microsoft.Web` service endpoint on the shared gateway subnet plus a subnet allow rule (recommended);
     - the gateway's public IP /32;
     - an App Service private endpoint.
   - Every option touches shared networking.
2. ~~**Long downloads.**~~ **Closed 2026-11-21:** Dan downloaded a 60 GB backup through the app with nothing cut off, so the App Gateway 240 s external-backend cap concern did not materialise for streaming downloads.
3. **Malware scan badge.** Deferred (R47): Storage Blob Data Contributor lacks `blobs/tags/read`. Showing it needs a custom role with only that action.
4. **Storage subnet capacity.** The /28 has 11 usable IPs, and each service per account takes one. The test account uses 2, so about 4 more customers fit on both services before the subnet must be enlarged.
5. **`session-guard.js` drift (R50).** Only this app's copy treats a redirected keepalive as session-ended. Consistency-checker Check 34 flags it until it is propagated to the other suite apps.

**Next-phase candidates (both parked):**
- in-app download progress, agreed as option A (see [[project-download-progress-idea]]);
- the Files home as a subscription → storage account → share/container tree, with staff-named subscriptions (see [[project-files-tree-idea]]).

**Phase 2 (automation API): not started.** All storage access already sits behind `StorageAccessService`, so the API reuses that gate.

**Why:** a future session needs to know what is proven, what is deployed, and what still blocks customer cut-over, without re-reading the build ledger.

**How to apply:**
- Before onboarding a customer or flipping a customer account to private-only, check item 1 is closed.
- To add an account: add it to `storageAccessTargets`, `pulumi up`, wait 5-10 minutes for RBAC, then use Test access. Test access now names the cause of a failure: `AuthorizationPermissionMismatch` means a missing role, `AuthorizationFailure` means network rules.
- Portal records are one per share or container. The YAML `services` list is per account.

See [[app-purpose]] and [[auth-model]].
