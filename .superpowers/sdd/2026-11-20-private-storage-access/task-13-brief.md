### Task 13: Infrastructure source edits (ZZ_Pulumi — edit only, never apply)

This task edits the Orchestrator repo's `ZZ_Pulumi/`, **not** the app repo. Commits go to the Orchestrator repo, and only when Dan authorises.

**Files:**
- Modify: `ZZ_Pulumi/Pulumi.ias-ai.yaml` (data only; no comments beyond section dividers), `ZZ_Pulumi/Models/AppDefinition.cs`, `ZZ_Pulumi/Resources/AppGatewayResources.cs`, `ZZ_Pulumi/IasAiStack.cs`
- Create: `ZZ_Pulumi/Models/StorageAccessTarget.cs`, `ZZ_Pulumi/Resources/StoragePortalResources.cs`

**What to add:**
1. **App entry** `CLOUDSELFSERVICE` with these fields:
   - `shortCode: CSS`
   - `hostname: cloudselfservice.infinitycis.com`
   - `containerImage: auscacprdcregsvc73a13ca2.azurecr.io/cloudselfservice`
   - `containerVersion: 1.0.0`
   - `pgDatabaseName: cloudselfservice`
   - `authProvider: Entra`
   - `storageMode: PGSQL`
   - `entraClientId: ""` (Dan fills it in after the app registration)
   - `authorizationOwners: djennings@advancedutility.com`
   - `memoryLimitMb: 2048`

   Check whether a `CSS` shortCode collides with an existing one first. If it does, use `CSP`.
2. **`AppDefinition.StorageAccessTargets`**: a `List<StorageAccessTarget>` with xmldoc. Each entry has:
   - `subscriptionId`
   - `resourceGroup`
   - `accountName`
   - `services` (`["file","blob"]`)
   - `dataRole` (`Contributor` or `Reader`)

   For each entry, `StoragePortalResources` creates the following, using a cross-subscription provider the same way the hub peering does:
   - a private endpoint per service in the suite spoke's storage subnet;
   - `privatelink.file.core.windows.net` / `privatelink.blob.core.windows.net` private DNS zones, if absent, linked to the suite VNet, with zone groups on the endpoints;
   - role assignments for the app's system-assigned identity: **Storage File Data Privileged Contributor** (`69566ab7-960f-475b-8e7c-b3118f30c6bd`) and **Storage Blob Data Contributor** (`ba92f5b4-2d11-453d-a403-e96b0029c9fe`). Use the Privileged Reader / Blob Data Reader IDs when `dataRole` is `Reader`. Verify every role GUID against `az role definition list --name "<role>"` output that Dan supplies, or Microsoft's built-in roles page, before committing; never guess.
3. **Dev test storage account** `auseusdevstgcssptest` (or the naming convention's equivalent; follow `Utilities.ResourceName`). It has:
   - shared-key access disabled
   - public network access disabled
   - one share `cssp-test` and one container `cssp-test`
   - file and blob private endpoints in the suite spoke
   - data roles for the app identity, and for an Entra group whose object ID comes from a new config value `cloudSelfServiceDevGroupObjectId`
4. **WAF**: a per-URI `WebApplicationFirewallPolicy` bound through a path-based rule on the CloudSelfService listener for `/api/uploads/*`, with `RequestBodyCheck = false`. It is the same policy as the shared one in every other respect, and the shared policy stays unchanged.

   If the current App Gateway uses only basic rules for this listener, add a URL path map for this app alone. Record the alternative in the task report and **stop for Dan's decision** if that change would alter other apps' rules.
5. A `_dev` PostgreSQL database and role for `cloudselfservice`, using the existing per-app pattern (should follow automatically from `pgDatabaseName`; confirm in `DatabaseResources.cs`).

- [ ] **Step 1: Build.** Run `dotnet build ZZ_Pulumi` and expect 0 errors. **Do not run any `pulumi` command.**
- [ ] **Step 2: Summarise for Dan in the task report:**
  - the resources that `pulumi preview` (run by Dan from IAS-AI after `git pull origin main`) should show;
  - the manual Entra steps: the app registration with redirect URIs `https://cloudselfservice.infinitycis.com/signin-oidc` and `http://localhost:5300/signin-oidc`, and the client secret in the app's Key Vault per the suite pattern;
  - Dan's follow-ups:
    - fill `entraClientId`;
    - list the first customers' storage accounts under `storageAccessTargets`;
    - run the live checks deferred from Tasks 4 and 6.
- [ ] **Step 3: Commit to the Orchestrator repo**, with Dan's authorisation: "ZZ_Pulumi: Cloud Self Service app, storage-portal private endpoints and RBAC, upload WAF exemption, dev test account".

---

## Coverage check (self-review)

| Spec section | Task |
|---|---|
| §5.1 rebuild to standard | 1, 2, 5, 12 |
| §5.2 identity, guests, staff auto-create with no role, fallback seam (`IdentityResolver`) | 5 |
| §5.3 roles and levels, per-request checks | 3, 5, 7, 11 |
| §5.4 single storage gate, Files backup intent, blob folders, path safety, errors | 3, 6, 7 |
| §5.5 downloads (attachment, range), chunked resumable MD5 uploads, WAF | 8, 9, 10, 13 |
| §5.6 data model incl. owner rule, append-only audit, upload sessions/chunks | 4, 9 |
| §5.7 pages | 10, 11 |
| §6 infra dependencies | 13 (Entra app registration and Defender for Storage are listed for Dan as manual follow-ups) |
| §7 onboarding + Gov checklist doc | 11 |
| §9 calls: no service-signin, one owner per target, Staff=Contribute, attachment-only, overwrite confirm, placeholders deleted | 5, 3/4, 3, 8, 9/10, 1 |
