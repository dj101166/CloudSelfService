# Task 13 report: ZZ_Pulumi infrastructure for Cloud Self Service

**Status:** DONE_WITH_CONCERNS. Nothing was stopped. The concerns are listed under "Risks to verify at first deploy".

- **Repo:** Orchestrator (`D:\SC\Personal`)
- **Branch:** `feat/cloudselfservice-storage-portal`, created from `master` at `3e1a765`
- **Commit:** `ade932f` "ZZ_Pulumi: Cloud Self Service app, storage portal, upload WAF exemption"
- Not pushed, not merged, no sync. No `pulumi`, `az` or `podman` command was run.

**Build and tests:**
- `dotnet build ZZ_Pulumi`: 0 errors, 0 warnings.
- `dotnet test ZZ_Pulumi.Tests`: 85 passed, 0 failed. 24 of those tests are new.

**Line endings:** every file keeps its original endings. All files are LF except `Resources/AppGatewayResources.cs`, which was already mixed: 4 CRLF lines in `GlobalConfiguration`. Those 4 CRs are restored byte-for-byte, so the diff contains content changes only.

---

## What was added

| File | Change |
|---|---|
| `Pulumi.ias-ai.yaml` | Adds the `CLOUDSELFSERVICE` app entry. Data only, no comments. Fields: `shortCode CSS`, hostname, image, `1.0.0`, `pgDatabaseName cloudselfservice`, Entra, PGSQL, `entraClientId ""`, owners, 2048 MB, `storagePortal: true`, `storagePortalDevAccount: false`, and empty `storageAccessTargets`. (`storageAllowedSubscriptions` was removed in fix round 1.) |
| `Models/StorageAccessTarget.cs` (new) | Fields: `subscriptionId`, `resourceGroup`, `accountName`, `services` (default `[file, blob]`), `dataRole` (`Contributor` or `Reader`). Also a `ResourceId` helper and `EndpointSuffix = core.windows.net` (R44). |
| `Models/AppDefinition.cs` | Adds `StoragePortal`, `StorageAccessTargets`, `StorageAllowedSubscriptions` and `StoragePortalDevAccount`, each with xmldoc. |
| `Configuration.cs` | Adds `cloudSelfServiceDevGroupObjectId` (optional). Adds `privateLinkDnsZoneIds` (optional, for reusing existing zones; see DNS below). Runs `StoragePortalPlan.Validate` at preview. |
| `Validation/StoragePortalPlan.cs` (new) | Preview-time validation (extended in fix round 1): GUID target subscriptions; account name 3–24 lowercase alphanumeric with no duplicates; services limited to file and blob; role limited to Contributor or Reader; portal only on a single-container .NET Entra app; dev account requires the group ID. Also holds the role-definition GUID map. |
| `Resources/StoragePortalResources.cs` (new) | Private endpoints, zones and role assignments (details below). |
| `Resources/AppGatewayResources.cs` | Path-based rule and per-URI WAF policy for storage-portal apps only. The shared policy settings were moved into a `WafSettings` helper with the same values, so the shared policy shows no diff. |
| `Resources/AppServiceResources.cs` | Storage-portal settings (below). |
| `IasAiStack.cs` | Withholds `ConnectionStrings__Admin` and `Storage__ConnectionString` for portal apps. Adds the new key-vault secrets. Constructs `StoragePortalResources` after DNS. Adds two stack outputs: `storagePortalEndpointIps` and `storagePortalDevAccountName`. |
| `ZZ_Pulumi.Tests/StoragePortalPlanTests.cs` (new) | 24 tests for the validator and the role GUIDs. |

**ShortCode `CSS`:** no collision with the existing 20 short codes (AS, AV, RM, ACA, CC, ED, RR, TG, ST, CG, DD, IA, FB, DA, CP, NX, TT, SD, VA, LT).

### App settings the deployed app receives

All values are versioned Key Vault references, following the suite pattern.

- **Standard .NET set:** `ASPNETCORE_ENVIRONMENT=Production`, the `DOCKER_*` settings, `WEBSITES_PORT=8080`, the memory limits, `DATA_ROOT=/mnt/artifacts`, `TZ`, `ConnectionStrings__Default` (app role `app_css`), `Auth__Provider=Entra`, `Entra__Instance`, `Entra__TenantId`, `Entra__CallbackPath=/signin-oidc`.
- **`Entra__ClientId`:** only injected once `entraClientId` is filled.
- **`Entra__ClientSecret`:** only injected once `entraClientSecrets.CSS` is set.
- **`Authorization__Owners`:** deliberately not the suite's `Authorization__Owners__0`. The app reads `config["Authorization:Owners"]` as one comma-separated string, and that lookup returns null when only `__0` exists, so the app would start with no Owner. This is a bug I found against the suite convention; it is fixed for portal apps only.
- **`Storage__AllowedEndpointSuffixes__0=core.windows.net`** (R44).
- **`Storage__AllowedSubscriptions__N`:** derived (fix round 1), one per distinct target subscription, plus the suite subscription when the dev account is on. Indexed form, because the app uses `Get<string[]>()`.
- **`AllowedHosts=cloudselfservice.infinitycis.com`** (L4).
- **Withheld from this app:**
  - `ConnectionStrings__Admin` (pgadmin). No migration is needed, and a portal should not hold the server admin login.
  - `Storage__ConnectionString` (shared key). The app never reads it.
- **Also injected, but ignored by the app:** `Storage__Mode`, `Storage__AccountName`, `Screenshot__ServiceKey`, `Entra__AllowedEmailDomain`. They are part of the standard .NET path and are harmless.
- **`APP_VERSION`:** baked into the image as a build-arg, as in every other app. It is not an app setting.

### Data Protection keys

The suite pattern is the per-app Azure Files share mounted at `/mnt/artifacts`, plus `DATA_ROOT`. That is kept; the app gets the mount and `DATA_ROOT`.

**App follow-up:** `Program.cs` does not call `AddDataProtection()`. Add `.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DATA_ROOT, "dp-keys")))` when `DATA_ROOT` is set, following `ReleaseMetrics/Program.cs` lines 24–37. Without it, every restart or redeploy invalidates auth cookies and antiforgery tokens: users are signed out, and in-flight uploads fail their antiforgery check.

### Storage portal resources

`StoragePortalResources` creates nothing with the current YAML, because there are no targets and the dev account is off.

**Private DNS zones.** I checked source and state: no `privatelink.file` or `privatelink.blob` zone exists in the suite stack. The only private zone is the PostgreSQL one, owned and linked by `DatabaseResources`. I followed that pattern: the zones are created in the suite RG and linked to the suite VNet.
- **NxDomainRedirect on the links.** Without it, linking `privatelink.blob.core.windows.net` would make every other app in the VNet get NXDOMAIN for any storage account that has a private endpoint elsewhere. With it, names missing from our zone fall back to public resolution. This uses `ResolutionPolicy`, which is available in AzureNative 3.23 (API 2024-06-01).
- **Hub zone override.** I cannot see the hub. If a central zone is already linked to the suite VNet, the link create fails, because a VNet can link only one zone of a given name. In that case set `ias-ai-infra:privateLinkDnsZoneIds: { file: <zoneId>, blob: <zoneId> }` and no zones are created here.

**Per target and per listed service:**
- A private endpoint `AUS-EUS-DEV-PEP-CSS-{account}-{svc}` in the suite storage subnet, with automatic approval.
- A zone group `PDZ-…`.
- A `RandomUuid` plus a `RoleAssignment` for the app's system identity on the account. It uses a cross-subscription provider `PRV-STG-{sub}`, one per non-suite subscription, built the same way as the hub peering provider.

**Role GUIDs.** Verified on 2026-11-21 against Microsoft's built-in roles page (learn.microsoft.com/azure/role-based-access-control/built-in-roles/storage):

| Role | GUID |
|---|---|
| Storage File Data Privileged Contributor | `69566ab7-960f-475b-8e7c-b3118f30c6bd` |
| Storage File Data Privileged Reader | `b8eda974-7b85-4f76-af95-65846b26df6d` |
| Storage Blob Data Contributor | `ba92f5b4-2d11-453d-a403-e96b0029c9fe` |
| Storage Blob Data Reader | `2a2b9908-6ea1-4ae2-8e65-a410df84e7d1` |

The two Contributor GUIDs match the brief.

**Dev test account** (opt-in with `storagePortalDevAccount: true`):
- Name `auseusdevstgcssptest`, from `Utilities.StorageAccountName`. The name is fixed, with no random suffix, so it can go in local settings.
- StorageV2 LRS, shared key off, `DefaultToOAuthAuthentication`, public network access Disabled, network ACL Deny with no bypass, TLS 1.2.
- File share `cssp-test` (100 GiB quota) and blob container `cssp-test`.
- File and blob private endpoints.
- The Contributor-level roles for both the app identity and the `cloudSelfServiceDevGroupObjectId` group.
- If the global name is taken, `up` fails on create and the suffix needs changing.

**Subnet capacity.** The storage subnet is a /28, which gives 11 usable IPs, one per endpoint. The dev account uses 2. That leaves room for 4 customers on both services, or 9 on one. Plan a larger subnet before customer 5.

### WAF (brief item 4)

The CloudSelfService listener's rule is `PathBasedRouting` with url path map `AUS-EUS-DEV-UPM-CSS`:
- **Default:** the app's pool and settings, under the shared policy.
- **Path rule `/api/uploads/*`:** same pool and settings, with a new per-URI policy `AUS-EUS-DEV-WAF-CSSUPL`. That policy is identical to the shared one except `RequestBodyCheck=false` and `RequestBodyEnforcement=false`.

Why each part is needed:
- **Both switches must be off.** On CRS 3.2, body inspection and size enforcement are independent. With enforcement on, the maximum body is 2 MB, so 4 MiB chunks would still be rejected in Prevention mode. The file-upload limit does not apply, because the chunks are `application/octet-stream`, not multipart.
- **`/api/uploads/*` is the narrowest path the gateway can express.** Path maps match by prefix, and `*` is allowed only at the end, so `/api/uploads/*/chunks/*` cannot be written. The rule also covers the small JSON status, complete and abort calls. Begin (`POST /api/uploads`) stays under the shared policy.
- **The app still enforces the limits itself:** a 4 MiB cap, an authenticated session and an antiforgery check on every chunk.

Effect on other apps: none. Their Basic rules, priorities and the shared policy are unchanged. The gateway only gains a `urlPathMaps` array, which is set only when a portal app exists. Since nothing else changes, I did not stop on this item. The WAF is still in Detection mode today, so uploads would work without the exemption; it matters when `wafMode` moves to Prevention.

### Gateway timeout

Backend HTTP settings already use `RequestTimeout = 3600` for every app, including this one, because of SignalR. No change was needed. See "Risks to verify" for the 4-minute caveat.

### PostgreSQL (brief item 5)

These follow automatically from `pgDatabaseName` and PGSQL mode:
- Databases `cloudselfservice` and `cloudselfservice_dev`.
- Roles `app_css` and `app_css_dev`, each with CONNECT and CREATE on its own database and ALL on schema `public`.
- `devConnectionStrings` gains a `CLOUDSELFSERVICE` entry.

**Append-only audit is not enforceable by grants here.** The app creates its tables at startup, so `app_css` owns `audit_events`. An owner always has UPDATE, DELETE and TRUNCATE, and it can re-grant them to itself, so a Pulumi `REVOKE` would be meaningless. A grant also cannot reference a table that does not exist when Pulumi runs.

**App follow-up:** in `PgsqlContext.EnsureSchemaAsync`, add a statement-level trigger:

```sql
CREATE OR REPLACE FUNCTION audit_events_append_only_fn() RETURNS trigger AS $$
BEGIN RAISE EXCEPTION 'audit_events is append-only'; END $$ LANGUAGE plpgsql;
-- create if absent (same pg_trigger check as the existing triggers):
CREATE TRIGGER audit_events_append_only BEFORE UPDATE OR DELETE OR TRUNCATE
  ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION audit_events_append_only_fn();
```

This guards against app bugs and injected DML. It does not stop a compromised owner, which could drop the trigger. The strong version needs two roles: an owner or migrator role that runs `EnsureSchema`, and a runtime role with only `INSERT, SELECT` on `audit_events`. That means a second Pulumi role and a second connection string. It is a phase-2 decision for Dan.

`DevData/dev-db-map.json` and `scrub/` were not changed. There is no prod data to refresh yet. Add a `CSS` entry, plus `scrub/cloudselfservice.sql`, once prod has data.

---

## What `pulumi preview` should show

Run it from IAS-AI after `git pull origin main`. The VPN is needed because the PostgreSQL provider must connect.

### Created now (current YAML)

**Key Vault**
- 1 vault `auseusdevkvcss…`
- The Key Vault Administrator `RandomUuid` and `RoleAssignment` (`RID/RAS-CSSADM`)

**Storage**
- 1 account `auseusdevstgcss…`
- Table `appdata` (`TBL-CSS`)
- Artifacts file share (`FSH-CSS`)

**PostgreSQL**
- `Database`: `PDB-CSS`, `PDB-CSSDEV`
- `RandomPassword`: `PPW-CSS`, `PPW-CSSDEV`
- `postgresql:Role`: `PRL-CSS` (`app_css`), `PRL-CSSDEV` (`app_css_dev`)
- `postgresql:Grant`: `PGD-CSS`, `PGS-CSS`, `PGD-CSSDEV`, `PGS-CSSDEV`

**Key Vault secrets** (`AUS-EUS-DEV-SEC-CSS…`): 22 secrets.
- Suffixes: PG, RU, RN, RP, AE, AS, WP, TZ, SS, AP, SM, SN, ML, WM, DR, EI, ET, EP, ED, AO, SES, AHN.
- Plus CI once `entraClientId` is set, EC once the secret is set, and SAS0…n once targets or the dev account exist.
- There are deliberately no PGA or ST secrets.

**App Service**
- `WebApp AUS-EUS-DEV-APP-CSS`
- `RandomUuid RID-CSS` and `RoleAssignment RAS-CSS` (Key Vault Secrets User)
- `WebAppApplicationSettings WAS-CSS`

**App Gateway**
- New `WebApplicationFirewallPolicy AUS-EUS-DEV-WAF-CSSUPL`
- `ApplicationGateway AUS-EUS-DEV-AGW-AI` updated in place. It adds probe, pool, HTTP settings and listener for CSS, rule `RRR-CSS` (PathBasedRouting, priority 300) and url path map `UPM-CSS`.

**DNS** (infinitycis.com provider)
- `CNM-CSS` (cloudselfservice CNAME)
- `TXT-CSS` (`asuid.cloudselfservice`)
- `WebAppHostNameBinding HNB-CSS`

**Stack outputs**
- New: `storagePortalEndpointIps` (empty) and `storagePortalDevAccountName` (empty).
- `devConnectionStrings` gains `CLOUDSELFSERVICE`.

**Expected for every existing app:** no diff, including the shared WAF policy. Treat any update on another app, or on the shared policy, as a red flag.

### When `storagePortalDevAccount: true` (and the group ID is set)

- **Zones:** `PrivateZone DNZ-FILE`, `DNZ-BLOB` and `VirtualNetworkLink DNL-FILE`, `DNL-BLOB`. Omitted if `privateLinkDnsZoneIds` is set.
- **Account:** `StorageAccount STG-CSSPTEST`, `FileShare FSH-CSSPTEST`, `BlobContainer BC-CSSPTEST`.
- **Per service (file, blob):**
  - `PrivateEndpoint PEP-CSS-PTEST-{svc}`
  - `PrivateDnsZoneGroup PDZ-CSS-PTEST-{svc}`
  - 2 × `RandomUuid` and 2 × `RoleAssignment`, one each for `…-APP` and `…-DEV`
- **Total:** 2 endpoints, 2 zone groups, 4 UUIDs and 4 role assignments.

### Per customer `storageAccessTargets` entry

- One `Provider PRV-STG-{subscription}` per new non-suite subscription.
- Per listed service:
  - `PrivateEndpoint PEP-CSS-{account}-{svc}`
  - `PrivateDnsZoneGroup PDZ-…`
  - `RandomUuid RID-…-APP` and `RoleAssignment RAS-…-APP`, scoped to the customer account
- The zones and links are created with the first target if the dev account has not already created them.

---

## Manual steps for Dan

### 1. Entra app registration

Register a single-tenant app in Advanced's tenant `d0748657-f5a8-4d8b-8a16-b848c23ea8cb`:
- **Web redirect URIs:**
  - `https://cloudselfservice.infinitycis.com/signin-oidc`
  - `http://localhost:5300/signin-oidc`
  - Also add `https://cloudselfservice.infinitycis.com/signout-callback-oidc` and `http://localhost:5300/signout-callback-oidc`. `/logout` signs out of Entra and returns through the handler's post-logout path.
- **Front-channel logout URL:** `https://cloudselfservice.infinitycis.com/signout-oidc` (the OIDC handler's `RemoteSignOutPath` default).
- **Token configuration:** add the optional ID-token claim **`acct`** (the app uses it to tell guests from members).
- **API permissions:** delegated `openid`, `profile`, `email` (the app requests only these).

### 2. Client secret

Use the suite pattern. The secret lives in the stack config, and Pulumi writes it into the app's Key Vault as `entra-client-secret`. From IAS-AI:

```
pulumi config set --path "ias-ai-infra:entraClientSecrets.CSS" --secret <value>
```

Then set `entraClientId` in the YAML. **The app refuses to start without the client ID and secret** (`Program.cs` throws). Do both before the first `pulumi up`, or the site will crash-loop until they are set.

### 3. Container image

Push `cloudselfservice:1.0.0` to ACR before `up`. `containerVersion` is `1.0.0`.

### 4. Gov-cloud customers

Per Gov-cloud customer, do Advanced's side of `CloudSelfService/docs/gov-cloud-guest-setup.md`:
- Enable Microsoft Azure Government in Cross-tenant access settings, Microsoft cloud settings.
- Add the customer's tenant ID with inbound B2B collaboration.
- Invite users by UPN.

### 5. Optional

Defender for Storage malware scanning on the customer accounts (spec §6.5). Not in this change.

---

## Dan's follow-ups

1. Fill `entraClientId` and set `entraClientSecrets.CSS` (above).
2. List the first customers' accounts under `storageAccessTargets`. Their subscriptions are added to `Storage:AllowedSubscriptions` automatically (fix round 1).
   - The deploying identity needs Owner, or Storage Account Contributor plus User Access Administrator, on each target account. That covers the private-endpoint approve action and the role write.
   - Turn off public network access on a customer account only after that customer is cut over.
3. **Dev test account.** Create or choose the dev Entra group. Set `ias-ai-infra:cloudSelfServiceDevGroupObjectId: <objectId>`, then flip `storagePortalDevAccount: true`. The suite subscription is then added to `Storage:AllowedSubscriptions` automatically (fix round 1).
4. **VPN DNS for dev.** The privatelink zones are linked to the suite VNet only, so VPN clients will not resolve them unless the hub DNS forwards to Azure DNS. Either link the zones to the hub VNet, which is a cross-subscription change in a hub-owned area, or have developers add hosts entries from `pulumi stack output storagePortalEndpointIps`. The certificate names (`*.file.core.windows.net`) rule out a public A record in infinitycis.com, the trick the PostgreSQL server uses.
5. **Deferred live checks:**
   - **Task 4 (PG schema on `_dev`):**
     1. Get the `CLOUDSELFSERVICE` value from `pulumi stack output devConnectionStrings --show-secrets`.
     2. Set it as `CSSP_TEST_PG`.
     3. Run `dotnet test`. The 5 `PgStoresIntegrationTests` should run and pass.
   - **Task 6 (`LiveStorageTests`):** on the VPN, `az login` as a member of the dev group, then wait a few minutes for RBAC to propagate.
     1. Set `CSSP_LIVE_FILES_ACCOUNT=auseusdevstgcssptest`, `CSSP_LIVE_SHARE=cssp-test`, `CSSP_LIVE_BLOB_ACCOUNT=auseusdevstgcssptest`, `CSSP_LIVE_CONTAINER=cssp-test`.
     2. Temporarily remove the `Skip` on `Live_FilesAndBlob_RoundTrip` and run it.
     3. Also confirm the ETag "H" round trip and `SyncCopyFromUri` `IfMatch` that Task 6 left unverified.
6. **Before the first deploy,** run `TRUNCATE upload_sessions, upload_chunks;` in `cloudselfservice_dev`.
7. **At deploy, spot-check the audit IP on reconnect:** sign in, drop the connection, act, and confirm the audit row's source IP.
8. **App follow-ups** (app repo, not edited here):
   - Data Protection key persistence to `DATA_ROOT/dp-keys`.
   - The `audit_events` append-only trigger.
9. **Workspace checklist:** run the `security-auditor` review on this change. I was told not to dispatch subagents.

---

## Risks to verify at first deploy

- **Long downloads and the 4-minute cap.** The Application Gateway limits table gives "Request timeout maximum to external backend: 4 minutes" (24 hours for a private backend). The suite's backends are the App Services' public `azurewebsites.net` FQDNs, so the 3600 s setting may be capped at 4 minutes for this app. Verify by downloading a multi-GB file that takes more than 4 minutes. If it is cut off:
  - Microsoft does not say whether the timeout measures until the first response byte or the whole response.
  - The fix is to reach the App Service through a private endpoint (a private backend). That is a suite-wide architecture change, so I did not make it.
- **`AllowedHosts` and platform pings.** `AllowedHosts=cloudselfservice.infinitycis.com` makes requests with the `azurewebsites.net` Host header return 400. The gateway probe sends the custom hostname, so it passes. App Service Always On and the container warm-up ping use the default hostname; any HTTP response normally counts for those. Confirm the site reports as started.
- **NxDomainRedirect** on the zone links. Confirm with `az network private-dns link vnet show … --query resolutionPolicy` after the first apply, per the "query the live resource" lesson in memory `reference_vnetrouteall_silently_dropped`.
- **Hub zones.** If the hub already links `privatelink.file` or `privatelink.blob` zones to the suite VNet, the link create fails. Use `privateLinkDnsZoneIds` instead.

## Stopped items

None. The WAF path map changes only the new app's rule and leaves every other app's rule and the shared policy untouched.

---

## Fix round 1

**Commit:** `5bdc0d3` "Derive storage-portal subscriptions and tighten its validation", on the same branch, on top of `ade932f`. Not pushed. No pulumi, az or podman command was run.

**Build and tests:**
- `dotnet build ZZ_Pulumi`: 0 errors, 0 warnings.
- `ZZ_Pulumi.Tests`: 108 passed, 0 failed (was 85).

**Line endings:** all touched files remain LF, with no CRs. `AppGatewayResources.cs` was not touched in this round.

### 1. `Storage:AllowedSubscriptions` is derived

`StoragePortalPlan.AllowedSubscriptions(app, suiteSubscriptionId)` returns:
- the distinct `storageAccessTargets` subscription IDs, lowercased, in first-seen order;
- then the suite subscription, when `storagePortalDevAccount` is on and it is not already listed.

`IasAiStack` (KV secrets `SAS{i}`) and `AppServiceResources` (`Storage__AllowedSubscriptions__{i}`) both use it, so the two cannot disagree. The order is stable for a given YAML, so the secret URNs and setting indexes do not shuffle.

**`storageAllowedSubscriptions` is removed outright** from the YAML and from `AppDefinition`; I did not keep it as an optional additional list. I saw no real need for one:
- The app's identity holds data roles only on target accounts, so an extra subscription would only let an admin register storage the app cannot open.
- A hand-kept list is exactly the drift this fix removes.

If a need appears, for example registering a target before its Pulumi entry lands, it can come back as an additive list.

The rule "target subscription must be in the allow-list" is gone, since it is now true by construction. Target subscription IDs are still validated as GUIDs, because they are injected and the app refuses to start on a malformed entry.

### 2. Egress guard

`storagePortal` combined with `dedicatedEgress: true` fails preview with a message that names the reason: the privatelink zones are linked to the suite VNet only.

I chose the guard over a second zone link to the egress VNet (the PGSQLEGR pattern). It is simpler, and no portal app needs a dedicated outbound IP. The choice is recorded in `AppDefinition.StoragePortal` xmldoc, with the instruction to add egress links and remove the guard together if that ever changes.

### 3. `privateLinkDnsZoneIds` validation

Validation runs in `Configuration` before the app checks:
- Keys may only be `file` or `blob`.
- Each value must match `/subscriptions/{guid}/resourceGroups/{rg}/providers/Microsoft.Network/privateDnsZones/{zone}`:
  - ARM segments are case-insensitive.
  - The RG follows Azure's name rules: 1–90 characters of word characters, `-`, `.`, `(` or `)`, not ending in `.`.
  - The subscription must parse as a GUID.
  - Nothing may trail the zone name.
- The zone must be `privatelink.{key}.core.windows.net`, matching the key. A blob zone under `file`, or a usgovcloudapi zone, is rejected.

### 4. Tests added

23 tests in total (some are theories with several cases):
- **Malformed target subscription:** `not-a-guid`, empty, truncated.
- **Missing resource group:** empty, whitespace.
- **Rejected configurations:** duplicate service entry, two dev accounts, portal with dedicated egress.
- **Derived subscriptions:**
  - empty with no targets;
  - distinct and case-folded, in first-seen order;
  - dev account appends the suite subscription last;
  - suite subscription not duplicated when already a target's.
- **Zone IDs, valid:** absent or empty map; well-formed IDs, including an RG with dots and parentheses; case-insensitive ARM segments.
- **Zone IDs, invalid:**
  - unknown key;
  - zone for the other service;
  - Gov zone;
  - malformed values: a bare zone name, a non-GUID subscription, an empty RG, an RG ending in `.`, the public `dnszones` provider, trailing segments, and an empty string.

### Left as-is (per review)

- RandomUuid role-assignment names (suite pattern).
- The `AllowedHosts` custom-hostname check at deploy time.
- The documented first-`up` crash-loop until the Entra values are set.

## R48 access restriction

Status: STOPPED on the access restriction itself (shared networking would change); only the xmldoc was added.

Why stopped: NetworkingResources.SubnetAppGateway (SNT AGW) declares no ServiceEndpoints. A VNet-subnet
access rule on the portal App Service needs the Microsoft.Web service endpoint on that subnet, and the
subnet is shared by every app behind the gateway. Adding it modifies a shared resource. No suite app has
any ipSecurityRestrictions / scmIpSecurityRestrictions today (grep of Resources/ and Models/ is empty), so
there is no pattern to follow either.

Options (portal app only, SiteConfig on its WebApp):
1. Add `Microsoft.Web` to SubnetAppGateway ServiceEndpoints (one-line, non-disruptive but it edits the
   shared subnet and shows as an update in `pulumi preview`), then set IpSecurityRestrictions = one Allow
   rule with VnetSubnetResourceId = SubnetAppGateway.Id, IpSecurityRestrictionsDefaultAction = Deny,
   ScmIpSecurityRestrictionsUseMain = true. Cleanest; also the AGW then reaches the site over the VNet
   path, so the source is the subnet, not a public IP.
2. No networking change: Allow rule with IpAddress = <gateway frontend public IP>/32 (without a service
   endpoint the gateway reaches the *.azurewebsites.net backend over the internet and egresses from its
   public frontend IP, NOT private instance IPs), default Deny, ScmIpSecurityRestrictionsUseMain = true.
   Works only while that public IP is stable (Standard v2 static IP is) and needs the IP exposed from
   AppGatewayResources as an Output; breaks silently if the frontend IP is replaced.
3. Private Endpoint for the App Service + gateway backend pointed at it (largest change; needs a
   privatelink.azurewebsites.net zone).
Recommendation: option 1 (or 2 if the shared subnet must stay untouched).

Also added: xmldoc on AppDefinition.StoragePortal noting clientAffinityEnabled: true is required beyond
one instance (Blazor Server circuits) and that app rate limits become per-instance.
