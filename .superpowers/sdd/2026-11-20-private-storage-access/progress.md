# SDD ledger — plan: docs/superpowers/plans/2026-11-20-private-storage-access.md

Spec: docs/superpowers/specs/2026-11-20-private-storage-access-design.md
Repo: D:\SC\Personal\CloudSelfService, branch `feat/private-storage-access` (created from master a7ec0c4).
Plan/spec commit: 54b604c.
Authorisation (Dan, 2026-11-20): local commits on the feature branch (and an Orchestrator-repo branch for Task 13). No merge, sync or push.
Commit style: multi-line imperative subject + bullet body; NEVER a Co-Authored-By trailer.

## Pre-flight scan

| Pair / task | Produces vs consumes | Finding |
|---|---|---|
| T1 → T2 | T1 render model + App.razor; T2 restyles App.razor / shell | consistent |
| T2 → T5, T10, T11 | Icon.razor switch, NavMenu, MainLayout | consistent; later tasks add icons/nav items |
| T3 → T4..T11 | records/enums, AccessPolicy, StoragePath, ChunkMath, headers | consistent |
| T4 → T5, T7, T9, T11 | stores, AuditService, schema incl. upload tables | T4 schema column `staging_name` vs T9 record `StagingPath` — R1 |
| T5 → T6 | DevSignIn.AssertSafe | T6 adds a UseFake guard test to T5's file but doesn't list DevSignIn.cs — R2 |
| T6 → T10 | FakeStorageBackend registration under `Storage:UseFake` | T6 and T10 both describe the registration/guard — R2 |
| T6 → T7, T8, T9 | IStorageBackend (WriteChunkAsync has finalPath) | consistent |
| T7 → T8 | AuthorizeDownloadAsync audit vs T8 endpoint audit of Download | double-audit risk — R3 |
| T7 → T9 | AuthorizeAsync audit vs per-chunk "not audited" | R3 |
| T8 → T9, T10 | TestAppFactory; download URL | consistent |
| T9 → T10 | upload endpoints / uploader.js | consistent |
| T11 | audit CSV endpoint has no file in its Files list | R4 |
| T5, T7, T8, T9, T11, T12 | steps say "invoke security-auditor / unit-test-reviewer" but implementers may not dispatch subagents | R5 |
| T5 | modifies `.claude/settings.json` permissions | R6 |
| T12 | security.md says X-Frame-Options SAMEORIGIN; code + plan say DENY | R7 |
| T13 | Orchestrator repo branch not named | R8 |
| T1, T2, T3, T6, T8, T10, T12, T13 | self-consistency (tests vs code vs files) | no contradictions found |
| T4 | Files list vs text (in-memory stores in app) | consistent after plan edit |
| T9 | Files vs interfaces | consistent |

## Rulings

- R1: Task 4 names the column `staging_path` to match `UploadSession.StagingPath` — the spec names neither; one name avoids a mapping bug — cost if wrong: a rename.
- R2: Task 6 owns FakeStorageBackend registration (`Storage:UseFake=true` AND Development) and the matching `AssertSafe` guard, and may edit `Authorization/DevSignIn.cs`; Task 10 only uses it — single owner avoids duplicate registration — cost if wrong: trivial move.
- R3: `AuthorizeDownloadAsync` and `AuthorizeAsync` audit only Denied (and Invalid is never audited); the Download/Upload Success/Failed rows are written once by the T8 endpoint / T9 Complete-Abort — spec §5.4 wants one audit row per operation — cost if wrong: duplicate or missing rows, caught by tests.
- R4: Task 11's CSV export goes in `Endpoints/AdminEndpoints.cs` — matches the Endpoints/ layout — cost: none.
- R5: Implementers do NOT run security-auditor / unit-test-reviewer. The controller dispatches them after the task review passes (security-auditor for T5, T8, T9, T11, T12; unit-test-reviewer for T7, T9, T11), and their findings enter the fix loop — template forbids implementer-spawned agents; CLAUDE.md still requires the audits — cost: one extra seat per listed task.
- R6: Skip the `.claude/settings.json` permission edit in Task 5; report to Dan as a suggestion — subagents should not change permission config — cost: an extra permission prompt for `dotnet test`.
- R7: X-Frame-Options stays DENY (Program.cs and plan Global Constraints); Task 12 updates `.claude/rules/security.md` to say DENY — DENY is stricter and already shipped — cost: none.
- R8: Task 13 works on a new Orchestrator branch `feat/cloudselfservice-storage-portal`, after checking that the Orchestrator working tree is clean in ZZ_Pulumi — cost: none.

## Model plan

Implementers: T1–T4, T6, T8, T10–T12 sonnet; T5, T7, T9, T13 opus (security-critical / infra judgment).
Reviewers: sonnet; opus for T5, T7, T9, T13 and the final review.

## Progress

Task 1: dispatched (BASE 54b604c, implementer sonnet)
Task 1: implementer DONE_WITH_CONCERNS (b65666a). Concerns: NavMenu stale links + site.css Mud rules -> Task 2 replaces shell (noted in T2 dispatch); /Error page gone.
- R9: No /Error page. UseExceptionHandler(createScopeForErrors: true) is accepted for T1; T2 shell must render a friendly error via the blazor-error-ui; revisit in final review — spec silent — cost: a raw 500 page in prod on an unhandled exception.
Task 1: review — spec ✅, 1 Important (/Error route dangling); fix round 1 dispatched to original implementer (R9 superseded: add Error.razor now).
Task 1: minor (deferred): MainLayout double blank line; _changelog text edited in place (shell replaced in T2); Routes NotFound LayoutView legacy (T5); Dockerfile test gate (T12).
Task 1: fix round 1/5 (2 addressed, 0 open — /Error page restored statically; smoke test asserts fallback; commits b65666a..101c72c)
Task 1: minor (deferred): App.razor + SmokeTests.cs whole-file EOL rewrite noise; future APP_VERSION env-var tests need [Collection].
Task 1: complete (commits 54b604c..101c72c, review clean)
Task 2: dispatched (BASE 101c72c, implementer sonnet)
Task 2: implementer DONE_WITH_CONCERNS (3e16bf6) — observations: AppMenu identity stub until T5; LoginLayout unused until T5; unused RM rules in app.css (verbatim copy).
Task 2: review — spec ✅ (minor deviation: header logo), 2 Important (comment guard misses *.razor.css; element-class check blind to C# literals); fix round 1 dispatched incl. minors (stale comments, csproj EOL, brand text, empty changelog).
Task 2: fix round 1/5 (2 addressed, 0 open — comment guard covers *.razor.css; element check reads C# literals; commits 3e16bf6..795054f)
Task 2: minor (deferred): modifier (--) check doesn't scan .cs files under Pages/Shared; literal regex has raw newline in verbatim string (EOL-fragile); AppMenu.razor.css comment references "Task 5"; remarks block misplaced above IsBemElement; interpolated class names invisible (documented limit).
Task 2: complete (commits 101c72c..795054f, review clean)
Task 3: dispatched (BASE 795054f, implementer sonnet)
Task 3: implementer DONE (feb584e)
Task 3: review — spec ✅, quality Approved, 0 Critical/Important.
Task 3: minor (deferred): no test that a customer user's StaffRole is ignored -> CARRY to T7 dispatch (add AccessPolicyTests case there); BlockId no index range guard; ChunkCount overflow near long.MaxValue (capped upstream); bidi controls (U+202A-202E, U+2066-2069) accepted in names (display spoof) -> final review; astral char fallback "__"; duplicate StaffRoleNone test row.
- R10: RangeHeader.TryParse returns bool and can't distinguish malformed (ignore→200) from unsatisfiable (416). Task 8 may change it to return a tri-state `RangeParseResult { None, Satisfiable, Unsatisfiable }` with updated T3 tests — T8's brief requires both behaviours — cost: small API change in a pure helper.
Task 3: complete (commits 795054f..feb584e, review clean)
Task 4: dispatched (BASE feb584e, implementer sonnet)
Task 4: implementer DONE_WITH_CONCERNS (5f39b6e) — PG SQL never executed; trigger drop/recreate each startup.
- R11: A local `postgres:17-alpine` podman image exists. The Task 4 fix round (or follow-up) adds an opt-in integration test class `PgStoresIntegrationTests` gated on env var `CSSP_TEST_PG` (skip when unset), run once against a throwaway local container on a random port, to execute EnsureSchema twice + each store round-trip + trigger rejection — Task 4's live check needs a real DB and the _dev DB doesn't exist yet — cost: one podman container, removed after.
Task 4: review — Needs fixes: CRITICAL Services/Data/* untracked (.gitignore `Data/` matches it); Important: target owner change leaves stale grants; trigger dropped/recreated each startup (race). Fix round 1 dispatched incl. minors + R11 real-PG integration tests.
Task 4: minor (deferred): DB role should REVOKE UPDATE/DELETE on audit_events -> T13 (Pulumi DB role grants) / final review.
Task 4: fix round 1 implementer DONE (6c627f1); real-PG integration tests 5/5 passed against postgres:17-alpine; re-review dispatched.
Task 4: fix round 1/5 (3 addressed, 0 open — Data/ ignore anchored + files tracked; target owner-change guard; non-destructive advisory-locked schema; commits 5f39b6e..6c627f1)
Task 4: minor (deferred): concurrent grant-insert vs target re-point race in PG triggers (needs FOR SHARE/UPDATE on target row) -> final review; FK 23503 not mapped to InvalidOperationException; integration tests leave rows (throwaway DB only); trigger existence check unscoped by table; in-memory target store accepts unknown customer_id.
Task 4: complete (commits feb584e..6c627f1, review clean)
Task 5: dispatched (BASE 6c627f1, implementer opus)
Task 5: implementer DONE_WITH_CONCERNS (a6adf53) — X-Forwarded-For trust (ruled); real Entra untested (no app reg yet) -> T13 follow-ups for Dan; demo shares renamed per persona; settings.json suggestion -> relay to Dan (R6).
Task 5: review (opus) — spec ✅ except §5.3 gap. Important: (1) cookie never re-validated -> deactivated/removed users keep sessions indefinitely; (2) plan-mandated: Owner promotion one-way.
- R12: Owner is config-authoritative, as in ReleaseMetrics and spec §5.3 "Owners from config": at sign-in persist Owner if in config, and demote a persisted Owner to StaffAdmin when no longer in config; AppRoleHandler treats a persisted Owner not in config as StaffAdmin — spec beats the brief's "set on every sign-in" wording — cost if wrong: an ex-owner keeps StaffAdmin until changed by another Owner.
- R13: Absolute session lifetime 12 h (`Auth:AbsoluteSessionHours`, default 12) stamped in the cookie, enforced in OnValidatePrincipal — bounds how long an Entra-side guest removal goes unnoticed — cost: daily re-sign-in for long sessions.
Task 5: fix round 1 implementer DONE (59bade0): SessionValidator + OnValidatePrincipal, absolute lifetime, config-authoritative Owner, page-attribute test; re-review (opus) dispatched. Note: port 5300 occupied by http.sys PID 4 on this machine (manual checks on 5397).
Task 5: fix round 1/5 (2 Important + minors addressed, 0 open; commits a6adf53..59bade0). New minor: dev staff-owner persona demoted to StaffAdmin (not in Authorization:Owners) -> fix with the security-auditor round.
- R14 (CARRY TO T7): every StorageAccessService operation must obtain the caller via CurrentUser-equivalent validation (SessionValidator re-read of user+customer Active, oid) per call — an open Blazor circuit never re-runs cookie validation, and MainLayout's Body renders briefly on in-circuit navigation before the session check — cost: one store read per op.
Task 5: minor (deferred): acct-claim hardening (#3); GET /logout CSRF sign-out (#8); bind not compare-and-set (last-writer-wins, oid check kills loser); page-attr test allow-list by filename, scans Pages/ only; session-guard keepalive follows 302 to 200 after rejection; newly-added Owner only after next sign-in.
Task 5: security-auditor dispatched (R5).
Task 5: security-auditor — 0 Critical/High. Medium: M1 circuit never revalidated + AppRoleHandler partial checks; M2 CSP permissive; M3 no .dockerignore (dev settings ship in image). Low: L1 cookie Secure/__Host-; L2 server idle never fires (polls slide); L3 PII in OIDC failure log; L4 AllowedHosts *; L5 no DataProtection key ring; L6 owner keyed on stale stored UPN; L7 tid guard; L8 Cache-Control/COOP; L9 Dockerfile test gate. Info I1-I7.
- R15: CSP for this app: drop 'unsafe-eval' (no charts/eval here), connect-src 'self', img-src 'self' data:. KEEP script-src 'unsafe-inline' (suite standard, Blazor.start/pre-paint inline scripts; nonce hardening deferred suite-wide). Record as a documented CloudSelfService exception for the consistency-checker in Task 12 docs — suite standard is enforced by Check 6; tightening is safe, loosening isn't — cost if wrong: a blocked resource visible at once in the browser console.
- R16: Fix in T5 round 2: M1 (RevalidatingServerAuthenticationStateProvider 1 min + AppRoleHandler uses SessionValidator), M2 per R15, M3 .dockerignore now, L1, L3, L6, L7, L8 (Cache-Control no-store on non-static + COOP), dev staff-owner persona. Defer: L2 (document client-side idle enforcement; revisit final review), L4 (AllowedHosts set via App Service config in T13 Pulumi), L5 (DataProtection — app has no DATA_ROOT mount; T13 decides key persistence; final review), L9 (Task 12 owns Dockerfile test gate), I1 (T12 security.md), I3 (NotFound in MainLayout for anon -> final review), I4 (managed identity/FIC for Entra — note for Dan), I5 sudo (suite pattern).
Task 5: fix round 2 implementer DONE (3814027): all 9 audit items; runtime-verified circuit revalidation and CSP; re-review (opus) dispatched.
Task 5: fix round 2/5 (9 addressed, 0 open; commits 59bade0..3814027)
Task 5: minor (deferred): wrong-tenant + missing-oid denials write no SignIn audit row (brief says every outcome) -> final review; renamed Owner with colliding UPN stored Owner/enforced StaffAdmin (fail closed); rename-race denial reason; __Host- cookie assumes empty PathBase; Auth:RevalidateSeconds not in appsettings.json; CSP verified in Dev only.
- R17 (CARRY TO T8): download responses set `Cache-Control: no-store` explicitly — the header middleware's no-store rule skips paths whose last segment contains a dot — cost: none.
Task 5: complete (commits 6c627f1..3814027, review clean, security audit addressed)
Task 6: dispatched (BASE 3814027, implementer sonnet)
Task 6: implementer DONE_WITH_CONCERNS (da9e8e5): >256 MiB blob move via StartCopyFromUri without auth (likely 403); folder move non-atomic; Files list lacks LastModified; nothing vs real Azure (deferred T13).
- R18: Large blob moves use server-side `StageBlockFromUri` with bearer `SourceAuthentication` in ≤4000 MiB ranges (SDK max per block is 4000 MiB) then CommitBlockList, replacing unauthenticated StartCopyFromUri — SAS is forbidden by spec, and same-account async copy without auth fails on a private account — cost: more requests per huge move.
Task 6: review — Needs fixes: CRITICAL R18 large blob move; Important: Files staging `.staging/` visible; Blob missing-folder lists empty; zero-byte download 416; blob folder move not resumable/no self-subtree guard; bearer token per move. Fix round 1 dispatched.
- R19: Staging path is caller-chosen (Task 9: `{folder}/.{name}.cssupload-{sessionId:N}`); backends hide names matching `^\..+\.cssupload-[0-9a-f]{32}$` in list/get — single convention, spec §5.5 — cost: none.
- R20: All backends order each listing page folders-first then name OrdinalIgnoreCase — UI consistency across kinds — cost: ordering is per page, not global, for multi-page folders.
Task 6: minor (deferred): implicit Blob folders vanish when emptied; DefaultAzureCredential authority not set for Gov-cloud storage suffixes -> final review/phase 2.
Task 6: fix round 1 implementer DONE (f51ec47); 349 passed/6 skipped; re-review dispatched.
Task 6: fix round 1/5 (6 addressed, 1 open + 1 new — resumable blob folder move deletes source on same-length dest (data loss); commits da9e8e5..f51ec47)
- R21: Blob mover resumes only over destinations it wrote itself (metadata `cssp_move_src` == source path, length equal); any other existing destination → AlreadyExists, source kept; folder move onto a file → AlreadyExists — spec anti-goal of losing customer data beats retry convenience — cost: a retry over a pre-existing identical copy needs manual cleanup.
Task 6: fix round 2 dispatched.
Task 6: fix round 2/5 (4 minors addressed, 1 open — stale cssp_move_src tag lets a recreated same-size source be deleted; commits f51ec47..e9c0698)
- R22: move tag bound to source ETag (`path|etag`), cleared best-effort after delete; single-file moves never resume (dest exists → AlreadyExists); tag skipped if >4096 bytes — closes stale-tag deletion — cost: a crash mid-folder-move of a source modified in between isn't resumable.
Task 6: minor (deferred): single-file blob move onto a virtual-folder prefix name creates a sibling blob (no collision check); empty-first-page-then-empty edge lists empty instead of NotFound.
Task 6: fix round 3 dispatched.
Task 6: fix round 3/5 (1 addressed, 0 open — move tag bound to source ETag, IfMatch-guarded copy/delete; commits e9c0698..df3bb55)
Task 6: minor (deferred): ClearTagAsync writes source-metadata snapshot over dest (narrow window); 412 on source-changed-mid-move shows "already exists" wording; fake tests don't exercise stale-tag version-mismatch path; fake Versions not pruned; oversize-tag blob not resumable.
Task 6: complete (commits 3814027..df3bb55, review clean). Live Azure verification deferred to Task 13 (LiveStorageTests skipped).
Task 7: dispatched (BASE df3bb55, implementer opus)
Task 7: implementer DONE_WITH_CONCERNS (d4100fe), 434 passed/6 skipped. Concerns: download NotFound/Unavailable not audited (R3 → T8 audits Failed); move/delete GetItem TOCTOU (backend fails safely); Files case-only rename unverified live; Denied audit Error holds internal reason (staff-only).
- R23 (CARRY TO T8): the download endpoint writes the Download audit row for every non-Denied outcome: Success, Failed (incl. NotFound/Unavailable/Invalid after authorisation, and client disconnect) — one row per request — cost: none.
Task 7: review (opus) — spec ✅; all attacks held. 1 Important (storage failure root cause never logged). Fix round 1 dispatched incl. minors (catch-all audit, case-aware subtree check, staging regex IgnoreCase, TimeProvider, 3 extra tests, xmldoc on store failures).
- R24 (CARRY TO T8, T9, T10, T11): StorageAccessService lets store (DB) exceptions propagate (fail closed). HTTP endpoints map them to 503 "The service is temporarily unavailable." with a logged error; Blazor pages catch them in the action handler and show the same message in an inline alert — never tear down the circuit — cost: none.
Task 7: fix round 1 implementer DONE (89d1ee4), 444 passed/6 skipped; re-review dispatched.
Task 7: fix round 1/5 (6 addressed, 0 open; commits d4100fe..89d1ee4)
Task 7: minor (deferred): routine NotFound/AlreadyExists logged at Warning with stack (noise); IsHiddenName FolderMarker compare case-sensitive on Files; Azure SDK exception redaction unverified; RecordAsync throwing in catch-all would mask original.
Task 7: complete (commits df3bb55..89d1ee4, review clean). unit-test-reviewer (R5) dispatched in parallel with Task 8 (disjoint files: tests for Services/Access only).
Task 8: dispatched (BASE 89d1ee4, implementer sonnet)
Task 8: implementer DONE (4b5e86b), 465 passed/6 skipped. Notes: folder-vs-malformed Invalid distinguished by message text; disconnect path untested (TestServer); extra ITargetStore lookup for audit CustomerId; 416 audited as Failed (accepted — it is a failed download attempt).
Task 7: unit-test-reviewer returned 20 gap cases (17 security-relevant) -> saved task-7-test-gaps.md; to be implemented after Task 8 review completes (single committer at a time).
- R25: audit Denied reasons stay distinct for foreign vs missing target (audit is staff-only); test pins both strings — cost: if a customer-facing audit view is ever built it must hide Error.
Task 8: review — spec ✅; 1 Important (non-StorageFailure exceptions at open → 500, no audit). Fix round 1 dispatched incl. minors (explicit not-a-file signal, null size, explicit count, normalized audit path, disconnect + mid-stream tests).
Task 8: fix round 1 implementer DONE (20ea334), 474 passed/6 skipped; re-review dispatched.
Task 8: fix round 1/5 (6 addressed, 0 open; commits 4b5e86b..20ea334)
Task 8: minor (deferred): non-aborted TaskCanceledException from GetItem in service escapes (timeout) -> final review; null-size path untested; disconnect test polls 5 s; AuthorizeDownload non-Ok Value convention (callers must check Outcome).
Task 8: review clean. security-auditor (R5, read-only) dispatched; Task 7 test-gap implementer dispatched (committing) in parallel — disjoint files (tests for Services/Access).
Task 7: test gaps implemented (653d554): 20 cases, 508 passed/6 skipped, no defects found.
Task 8: security-auditor — 0 Critical/High. M1 no per-user rate/concurrency limit. Low: L1 cross-site GET (Sec-Fetch-Site); L2 no re-authorisation during long streams; L3 audit fails open/written after transfer; L4 bidi/Cf chars in names; L5 no ETag/If-Range (stitched versions); L6 long-s case folding vs staging regex. Info: audit UserName from cookie not store; OpenRead warning lacks inner exception; SourceIp spoofable unless App Service restricted to gateway; Dockerfile test gate (T12).
- R26: Fix now (Task 8 round 2): M1 (named rate-limiter policy "transfers": per-user token bucket + concurrency 4, shared later by T9 upload endpoints), L1 (reject Sec-Fetch-Site cross-site/same-site on download), L2 (re-authorise every 64 MiB or 60 s mid-stream, abort on denial), L4 (reject Unicode Cf + C1 in names; sanitise in Content-Disposition), L5 (ETag/Last-Modified, If-Range, blob IfMatch / Files ETag compare), L6, audit UserName from store, inner exception in warning. Defer L3 (suite audit pattern fail-open; final review) and SourceIp/App Service access restriction (T13 Pulumi note) — cost: one more fix round.
Task 8: fix round 2 implementer DONE (96fda30), 540 passed/6 skipped; re-review (opus) dispatched.
Task 8: fix round 2/5 (6 addressed, 1 new Important — Cf/C1 ban applied to locating paths locks users out of existing files; commits 653d554..96fda30)
- R27: Cf/C1 rejected only for names being created (create-folder name, upload final name, move/rename destination); lenient for locating existing items; UI display sanitised via StoragePath.DisplayName (Cf/C1 → "_") — spec anti-goal of users losing access to their own data beats strictness on legacy names — cost: bidi-spoofed legacy names remain until renamed (display sanitised).
Task 8: minor (deferred): anonymous IP partition shares the App Service front-end IP (endpoint requires auth anyway).
Task 8: fix round 3 dispatched.
Task 8: fix round 3 implementer DONE (2e62d0a), 557 passed/6 skipped; re-review dispatched.
Task 8: fix round 3/5 (3 addressed, 0 open; commits 96fda30..2e62d0a)
Task 8: minor (CARRY TO T9): make `strict` a required parameter on StoragePath.TryValidateSegment (fail-open default); Move `to` with a new odd intermediate folder segment passes (strict-check destination folder segments that don't already exist, or require parent to exist). DisplayName must be used in T10 UI listings/breadcrumbs.
Task 8: complete (commits 89d1ee4..2e62d0a, review clean, security audit addressed)
Task 9: dispatched (BASE 2e62d0a, implementer opus)
Task 9: implementer DONE_WITH_CONCERNS (bf4e6fa), 642 passed/7 skipped; PG integration 6/6 vs podman postgres:17. Concerns: session stuck in Completing after crash leaves Files staging (sweep skips); concurrent Begins can create 2 sessions (harmless); fake blob abort fixed to match real (existing blob survives).
Task 9: review (opus) — CRITICAL: chunk writes pass Kestrel's non-seekable request body to the Azure SDK → every real chunk PUT fails (verified by reviewer probe vs exact SDK versions); fake hid it. Important: blob concurrent sessions on same name → stuck 503 loop; stuck Completing sessions orphan Files staging + lose Success audit. Fix round 1 dispatched.
- R28: Chunk bodies are read into a pooled ≤4 MiB buffer and passed to storage as a seekable stream; local MD5 check before storage; fake enforces seekability so tests pin the SDK contract — spec anti-goal is "not buffered WHOLE", a 4 MiB piece is fine — cost: ≤64 MiB per user at 16 concurrent chunks.
Task 9: minor (deferred): Begin overwrite:true on a non-existent name silently replaces a file that appears before Complete (no escalation; both ops need Contribute); a failed-then-retried Complete writes 2 Upload rows.
Task 9: fix round 1 implementer DONE_WITH_CONCERNS (0976cc9), 662 passed/7 skipped; PG 6/6. Not done: Files empty parent folder on abandoned upload (conflicts with R19 fixed staging path) — accepted, deferred. Begin "other user uploading" check non-atomic (loser gets clean 409).
- R29 (fold into T9 round 2 with re-review findings): upload 409 responses carry a machine code `{ error, code }` with code ∈ {"exists","in_progress","restart"} so uploader.js branches on code, not text — cost: tiny API change before any client exists.
Task 9: fix round 1/5 (3 addressed, 0 open; commits bf4e6fa..0976cc9). Reviewer verified buffered piece against real Blobs 12.30.1 / Files.Shares 12.28.0 via recording transport (no exception, correct Content-Length + Content-MD5).
- R30: "landed" = Files: staging gone AND final exists; Blob: first committed block id == BlockId(session,0) (zero-byte: metadata cssp_upload_session). Used by sweep settle and Complete's lost path — size-only could write a false Success and orphan staging — cost: one extra storage call per settle.
- R31: other-user Blob session blocks Begin only while active (created or chunk within Transfers:BlobLockMinutes=15) — prevents a 24 h name lock by an abandoned tab — cost: a stale session's late Complete hits restart.
Task 9: minor (deferred): Files empty parent folder left by abandoned upload into new folder — needs an R19 amendment (stage in deepest existing ancestor), not platform-impossible.
Task 9: fix round 2 dispatched (R29 codes, R30, R31, race hygiene, clearArray).
Task 9: fix round 2 implementer DONE (9e89e65), 688 passed/7 skipped; PG 6/6; re-review dispatched.
Task 9: fix round 2/5 (5 addressed, 0 open; commits 0976cc9..9e89e65). Reviewer verified blob commit carries x-ms-meta-cssp_upload_session + If-None-Match:* and GetBlockList uses blocklisttype=committed (recording transport, real SDK).
Task 9: minor (CARRY to combined round after security audit): Files landed check can't distinguish our final file from another's after EXTERNAL staging removal → stamp cssp_upload_session metadata on Files staging at Begin and require it on final; move UploadErrorCodes to Services/Transfers (layering); chunk-path storage NotFound → restart; possible unused ShortChunkMessage.
Task 9: minor (deferred): non-lost Complete failure branch writes Failed row unconditionally (needs >10 min failing commit); uploader.js must treat bare 401/302 as session expired.
Task 9: security-auditor (R5) dispatched read-only.
Task 9: security-auditor — 0 Critical. HIGH H1: unbounded Begin pre-allocates 50 GiB sparse Files staging (quota on logical size) → one user fills a share in minutes; hidden + unaudited; 24 h. MEDIUM: M1 failed staging delete orphans permanently (Aborted set before delete); M2 chunk PUTs no rate bucket (DB pool exhaustion). LOW: L1 slow body holds buffer ~4.8 h; L2 Begin/sweep-abort/overwrite not audited; L3 cleanup deletes stored path without staging-name check; L4 Begin body under 30 MB default limit.
- R32: Staging Files created at size 0 and grown per chunk (SetHttpHeaders size = max(current, offset+len)) — no reservation before data arrives; per-user cap `Transfers:MaxOpenSessionsPerUser`=8 and reserved bytes ≤ MaxUploadBytes per user; idle Open sessions (no chunk) expire after `Transfers:IdleSessionMinutes`=120; same user + same path + different size aborts the older session — closes H1 without hurting real uploads — cost: one extra storage call per chunk on Files (resize only when growing).
Task 9: fix round 3 implementer DONE_WITH_CONCERNS (9917347), 734 passed/7 skipped; PG 6/6. Concerns: Blob gets only caps + idle sweep (no share quota concept); cap check non-atomic (≤4 parallel Begins); min data rate set but untestable in TestServer; Files empty parent folder still deferred.
Task 9: fix round 3/5 (all H1/M1/M2/L1-L4/carried addressed, 0 Critical/Important; commits 9e89e65..9917347). Reviewer proved growth can't truncate written data (FOR UPDATE + monotonic staging_size) and verified Files SDK headers (create 0 + meta, SetHttpHeaders size only, rename + meta).
- R33: Idle sweep applies only to sessions that never received a chunk (last_activity_at IS NULL, created > IdleSessionMinutes ago) — R32 said "(no chunk)"; resuming a part-done upload after an overnight sleep must work within the 24 h lifetime — cost: an abandoned part-done upload holds its staging up to 24 h (bounded by caps).
Task 9: fix round 4 (cleanup) dispatched: R33; MinDataRate only on HTTP/1.1; Complete verifies staging length == size before rename; flaky PG grow test; MaxConcurrentChunksTotal default 64 (< Npgsql pool 100) + 30 s timeout on grow; refused-cleanup error logged once (mark row); _dev reset note for Task 13.
Task 9: fix round 4 implementer DONE (b6ae4e5), 746 passed/7 skipped; PG 6/6 x3; re-review dispatched.
Task 9: fix round 4/5 (8 addressed, 0 open; commits 9917347..b6ae4e5). Residual found: a timed-out grow applied late by Azure after a later grow can truncate a written piece; a subsequent full-size grow zero-fills it and Complete's size check passes → silent corruption (narrow race).
- R34: Files staging grows exactly ONCE per session, to session.Size, on the first chunk (single grow → a late duplicate sets the same size; truncation impossible); quota exposure bounded by R32(b) per-user byte cap and only once data flows — supersedes per-chunk growth — cost: full logical size reserved from the first piece.
Task 9: fix round 5/5 dispatched (R34 + abort session on chunk-level StagingIncomplete so Begin doesn't resume into a 416 loop).
Task 9: fix round 5 implementer DONE (81719c3), 750 passed/7 skipped; PG 6/6; re-review dispatched.
Task 9: fix round 5/5 (2 addressed, 0 open; commits b6ae4e5..81719c3)
Task 9: minor (deferred): Files empty parent folder left by abandoned upload into new folder (needs R19 amendment); one-piece-then-abandon holds full logical quota until idle/expiry (bounded by per-user byte cap; idle sweep only for never-chunked sessions — 24 h worst case); sync-over-async in one test helper.
Task 9: complete (commits 2e62d0a..81719c3, review clean, security audit addressed). unit-test-reviewer on Services/Transfers dispatched read-only.
Task 9: unit-test-reviewer returned 22 gaps (10 SEC) -> task-9-test-gaps.md. Likely code bug: MaxUploadBytes not capped at 50,000 blob blocks.
- R35: effective MaxUploadBytes clamped to ChunkSize × 50,000 (≈195.3 GiB) with a startup Warning — above it blob uploads can never commit — cost: none (spec default is 50 GiB).
Task 9 test gaps: dispatched (BASE 81719c3, sonnet).
Task 9: test gaps implemented (048434e): 22 items + R35 clamp; 781 passed/10 skipped; PG 11/11; no defects. (Implementer also added a CHANGELOG.md entry — fine; T12 consolidates the changelog.)
Task 10: dispatched (BASE 048434e, implementer opus — upgraded from sonnet: uploader.js resume/codes/parallel/MD5 is judgment-heavy).
Task 10: implementer DONE_WITH_CONCERNS (aab9c69), 847 passed/10 skipped; Playwright 43/43 measured on :5397 incl. resume, special-char download, read-only, cross-customer denied, load-more, light/dark 1440/375, no CSP violations. Concerns: callback signatures differ from brief (upload id, OnStatus, OnDenied, key); ~60+ dropped files hit 120/min Begin limit (waits, completes); audit SourceIp via IHttpContextAccessor in circuit; idle sign-out can cut long unattended uploads; Help contact generic.
- R36: An actively transferring upload counts as user activity: uploader.js notifies SessionGuard (activity ping) at most once a minute while bytes are flowing — idle timeout is for an unattended *idle* session, not an in-progress transfer; the 12 h absolute lifetime still applies — cost: an unattended browser stays signed in while uploading.
- R37: Help page support contact = configurable `Support:Contact` (default "your Advanced account team") — no address in repo — cost: none.
Task 10: review (opus) — compliant; no XSS; 1 Important (PageAuditContext reads HttpContext inside the circuit → empty SourceIp + possibly shared correlation id). Fix round 1 dispatched with all 11 minors + R36 + R37.
- R38: client IP captured during the static request and carried into the circuit via PersistentComponentState (fallback: data-protected token); correlation id = new Guid per action; unknown IP recorded as "unknown" — spec §5.6 requires source IP on audit rows — cost: none.
Task 10: fix round 1 implementer DONE (7516de9), 869 passed/10 skipped; runtime-verified audit IP ::1 + distinct correlation ids; re-review dispatched.
Task 10: fix round 1/5 (12 addressed, 0 open; commits aab9c69..7516de9)
Task 10: minor (deferred): ReloadLoadedPagesAsync falls back to page 1 if a leftover alert is set; _pagesLoaded not reset on Denied; synthetic scroll couples to session-guard.js event list (test pins it); audit IP relies on prerender.
Task 10: complete (commits 048434e..7516de9, review clean). security-auditor on the UI (read-only) dispatched in parallel with Task 11.
Task 11: dispatched (BASE 7516de9, implementer sonnet).
Task 11: implementer DONE_WITH_CONCERNS (5063753 — amended own commit only, history verified), 947 passed/10 skipped; Playwright 37/37. Concerns: /api/admin/audit.csv gives 302 (challenge) not 403 to non-admins (not served); demoted staff sees stale nav until re-render (actions refused); Gov status normalisation; OwnerConfig.Upns added.
Task 10: security-auditor (UI) — 0 Critical/High. MEDIUM: no-store skipped for /files/... pages whose last folder segment has a dot (prerendered listing cacheable). LOW: persisted audit IP can be suppressed/replayed (not forged) — prefer CircuitHandler on /_blazor connection; spark-md5 LICENSE (WTFPL, profane) publicly served — take MIT, move notices out of wwwroot; Dockerfile test gate (T12). Info: resume can mix two same-name+size files (no hash); stale target header on invalid path after target change (UI only); Retry-After uncapped.
- R39: Fix in a follow-up after Task 11 review (single committer): ShouldDisableCaching applies no-store to every path under /files/ and /api/ and to any path not under a static prefix or a known root asset list; CircuitHandler records the /_blazor connection's RemoteIpAddress (authoritative, updated on reconnect) with the persisted value as fallback; spark-md5 under MIT with notices in ThirdPartyNotices/ (only .min.js in wwwroot); resume refuses when a first-piece MD5 recorded at Begin differs; clear _target/_level on invalid path after target change; cap Retry-After at 300 s — cost: one small commit.
Task 11: review (opus) — compliant; escalation/cross-customer/SSRF/CSV solid. 1 Important: stale-role READS (audit log query + post-action reloads re-read data for a demoted admin in an open circuit). Fix round 1 dispatched with all 11 minors.
Task 11: fix round 1 implementer DONE (e3ccbbe), 974 passed/10 skipped; Playwright 37/37 + CSV href/audit; re-review dispatched.
Task 11: fix round 1/5 (12 addressed, 0 open; commits 5063753..e3ccbbe)
Task 11: minor (deferred): Loaded=false after denial → spinner if role restored mid-circuit (unreachable); AdminEndpointTests.cs EOL rewrite; IsValidUpn allows private-use chars.
Task 11: complete (commits 7516de9..e3ccbbe, review clean). Dispatched in parallel: R39 UI fixes (Task 10 implementer, committing), security-auditor on admin (read-only), unit-test-reviewer on AdminService (read-only).
Task 11: unit-test-reviewer returned 15+2 gaps (10 SEC) -> task-11-test-gaps.md. Real bug: IsValidUpn accepts lone surrogates.
- R40: reject Rune.ReplacementChar (lone surrogates) and PrivateUse in IsValidUpn; check StoragePath strict validation for the same pattern — cost: none.
- R41: add `staff-admin` dev persona — needed to test StaffAdmin-allowed paths — cost: none (Development only).
Task 11 test gaps: queued until the R39 commit lands (single committer).
Task 10: R39 follow-up DONE (88d4f13), 1005 passed/11 skipped; PG 12/12; Playwright 13/13. IP via SignalR hub filter (IHttpContextAccessor null in OnConnectionUpAsync under LongPolling — measured). Leaves CS0108 warning from Task 11 (Staff.razor) → fixed in Task 11 test-gap round.
Task 11 test gaps: dispatched (BASE 88d4f13, sonnet) incl. CS0108 fix. R39 scoped re-review dispatched (read-only).
Task 10: R39 re-review — all addressed, no Critical/Important (commits e3ccbbe..88d4f13). Verified directly: Dockerfile has no test gate yet; spark-md5.min.js has NO licence banner.
- R42 (CARRY TO T12): (a) prepend a one-line MIT banner `/*! spark-md5 v3.0.2 | (c) André Cruz | MIT */` to wwwroot/lib/spark-md5/spark-md5.min.js (MIT requires the notice to accompany copies; served file must carry it); (b) Task 12's Dockerfile test gate must not fail on excluded ThirdPartyNotices/ — remove `ThirdPartyNotices/` from .dockerignore (it's a few KB of text, harmless in the build stage, never copied to the final image since only /app/publish is copied) — cost: none.
Task 10: minor (deferred): static-prefix rule is folder-based (future page under /images etc. would be cacheable); hub filter registered globally (only hub is Blazor's); reconnect IP path untested end-to-end (manual check recommended at deploy); _folder left stale on invalid path (actions gated on _target).
Task 11: test gaps implemented (6a44e20): 1062 passed/11 skipped, 0 warnings. R40 also fixed the same lone-surrogate hole in StoragePath strict validation; R41 staff-admin persona; CS0108 fixed (duplicate inject). DEFECT FOUND+FIXED: AuditFilters.ToQuery overflowed on `to` = 9999-12-31 (CSV endpoint 500) → clamped; test added.
Task 11: security-auditor (admin) — 0 Critical/High. MEDIUM: M1 lost update — sign-in whole-row upsert can undo a concurrent deactivate/demote; M2 CSV formula bypass via ';' on semicolon-separator locales; M3 targets not checked against stated subscription (MI's whole RBAC scope reachable; Test access oracle). LOW: L1 .NET vs PG case-folding (İ/K) blocks staff first sign-in; L2 HomeTenantId/CrossTenantStatus not enforced at bind; L3 (already fixed in 6a44e20); L4 Test access no timeout; L5 CSV truncation silent + audit fail-open; L6 audit filters unindexed / no statement timeout; L7 repoint guard no DB backstop. Info: OwnerConfig.Upns mutable; Gov storage suffix can't work with a commercial MI token.
- R43: M1 → targeted UPDATEs (admin: active / staff_role only; sign-in: oid/last_sign_in/upn/display_name WHERE active AND oid unbound-or-equal, never writes active/staff_role); M2 → neutralise trigger chars after any in-cell ; , | tab and quote every cell; M3 (code part) → `Storage:AllowedSubscriptions` allow-list (empty = any, logged warning at startup) + creating or repointing an unowned (internal) target requires Owner; ARM resolution of account→subscription deferred (needs ARM Reader for the MI — T13 note for Dan); L1 → NFKC + ToLowerInvariant normalised UPN for compare/store index, ASCII-only domain, refuse UPNs in Owners list; L2 → on first bind, if customer.HomeTenantId set require token home tenant (GUID from `idp`, else `tid` for members) to match; refuse bind while CrossTenantStatus=Pending; L4 → 20 s probe timeout; L5 → CSV trailing "# truncated at N rows" line + filename suffix when truncated; L6 → indexes (user_id, at desc), (operation, at desc), command timeout 30 s on audit query, export policy 10/min; L7 → extend targets trigger to block account/container/kind/suffix change while grants exist; OwnerConfig.Upns returns a copy — cost: one fix round.
- R44: Remove `core.usgovcloudapi.net` from the default Storage:AllowedEndpointSuffixes — all customer storage lives in our commercial Azure (stamps); Gov customers are Gov for identity only, and a commercial managed identity can't get a token for Gov storage — cost: Gov-hosted storage would need a config change plus a Gov credential (phase 2+).
Task 11: R43/R44 follow-up DONE (4863354), 1099 passed/15 skipped, 0 warnings; PG 16/16; Playwright 37/37. Owner never persisted by sign-in; Pending Gov customer can't first-sign-in. Scoped re-review dispatched.
Task 11: R43/R44 re-review — most resolved; open: allow-list fails open on non-GUID entries (Important), cooldown per-circuit; adjacent hole: StaffAdmin can move/re-point EXISTING internal targets.
- R45: Owner required for any owner or location change to an existing internal target (display-name edits stay StaffAdmin) — closes the hand-internal-storage-to-a-customer path — cost: none.
Task 11: follow-up round 2 dispatched (fail-fast allow-list, singleton cooldown, R45, member tid fallback, timeout test).
Task 11: minor (deferred): upn_normalized backfill uses SQL lower not NFKC (dev data only; fails closed); old ux_users_upn_lower kept (stricter); schema NOT NULL change not tolerant of older instances (unreleased).
Task 11: follow-up round 2 DONE (9647d6f), 1110 passed/15 skipped, 0 warnings; scoped re-review dispatched.
Task 11: follow-up round 2 re-review — all addressed, no Critical/Important (commits 4863354..9647d6f). Task 11 fully closed incl. security audit + test gaps.
Task 12: dispatched (BASE 9647d6f, implementer sonnet) with R7, R42, R15 doc note, R19 amendment note, deferred-minor docs.
Task 12: implementer DONE (ceae75c): podman build ran test gate (1110 passed), Dev-in-Production container exits as designed, /login 200 with headers; web tests 7/7; image/containers removed. Observed 2nd CSP header frame-ancestors 'self' (not set by app code — investigate in review). Review dispatched.
Task 12: review — Approved; 2 Important (web tests not re-runnable: fixed upload name; positional target card). 2nd CSP header confirmed via ASP.NET Core docs as Blazor's default ContentSecurityFrameAncestorsPolicy 'self' → set 'none'. Fix round 1 dispatched incl. changelog Removed section + plain-language bullets.
Task 12: fix round 1 DONE (0e10389): unique roundtrip name + teardown, data-target-name selection, frame-ancestors 'none' on Blazor endpoint, changelog Removed section + no config keys; web tests 7/7 twice. Scoped re-review dispatched.
Task 13: dispatched (Orchestrator repo D:\SC\Personal, new branch feat/cloudselfservice-storage-portal from master 3e1a765, implementer opus) with R4x infra notes.
Task 12: fix round 1/5 (4 addressed, 0 open; commits ceae75c..0e10389). Reviewer's "_changelog in MainLayout" note is obsolete (CLAUDE.md rewritten; single CHANGELOG.md).
Task 12: complete (commits 9647d6f..0e10389, review clean).
Task 13: implementer DONE_WITH_CONCERNS (Orchestrator branch feat/cloudselfservice-storage-portal, ade932f; ZZ_Pulumi build 0/0; ZZ_Pulumi.Tests 85/85). Concerns: App Gateway → external backend 4-min cap may cut long downloads (live check); Owners single-value key; DP keys not persisted; audit_events append-only needs app trigger; dev account opt-in; VPN DNS for privatelink zones. Review dispatched.
- R46: App follow-up now (CloudSelfService, single committer): (a) persist Data Protection keys to {DATA_ROOT}/dp-keys with SetApplicationName("CloudSelfService") when DATA_ROOT set (suite pattern from ReleaseMetrics), else ephemeral with a startup warning; (b) EnsureSchema adds a trigger on audit_events raising on UPDATE/DELETE (append-only at the DB, even for the owning role); PG integration test — sessions surviving restarts and audit integrity are production-critical — cost: none.
R46: DONE (649f935): DP keys persisted to DATA_ROOT/dp-keys; audit_events UPDATE/DELETE/TRUNCATE blocked by triggers (PG-verified 42501); 1132 passed. Scoped review dispatched.
R46: review Approved; cleanup dispatched — 6 LF files flipped to CRLF (verified by CR counts), duplicate Security heading, ops notes. Dev refresh script doesn't touch audit_events (verified).
R46: cleanup DONE (fefa322): EOL restored (CR=0), changelog merged, ops notes; verified by controller. R46 complete.
Task 13: review (opus) — Approved; blast radius contained (gateway in-place, no renames/re-index, shared WAF unchanged). Important (not code-fixable): App Gateway external-backend timeout cap 240 s may cut multi-GB downloads → GO-LIVE CHECK for Dan (fallback: private App Service backend, suite-wide). Fix round 1 dispatched: derive AllowedSubscriptions, reject dedicatedEgress on portal app, validate privateLinkDnsZoneIds, more validator tests.
Task 13: minor (deferred): RandomUuid role-assignment names (suite pattern); AllowedHosts custom host vs azurewebsites.net pings (deploy check); first up crash-loops until Entra values set (documented).
Task 13: fix round 1 DONE (5bdc0d3): derived AllowedSubscriptions, egress guard, zone-id validation; ZZ_Pulumi.Tests 108/108; EOL preserved (verified). Re-review dispatched.
Task 13: fix round 1/5 (4 addressed, 0 open; commits ade932f..5bdc0d3 on Orchestrator branch feat/cloudselfservice-storage-portal)
Task 13: complete (Orchestrator 3e1a765..5bdc0d3, review clean).
ALL 13 TASKS COMPLETE. App branch head fefa322 (from master a7ec0c4). Final whole-branch review next.
FINAL REVIEW (opus, a7ec0c4..fefa322): With fixes. CRITICAL: MainLayout pending-role check uses stored StaffRole not OwnerConfig.EffectiveRole → configured Owner locked out on fresh DB (runtime-verified; dev persona hid it). Important: missing ConnectionStrings outside Development silently in-memory; spec §5.5 malware-scan tag undone/unrecorded; App Service publicly reachable → XFF forgery + WAF bypass (infra); resolver consumers beyond StorageAccessService (gated; add architecture test). Minors: ops doc inaccuracies, CHANGELOG overclaim (unaudited tenant/oid denials), clamp warning per construction, empty Owners warning, StoragePath/StoragePaths duplication, .superpowers not in .gitignore. Triage: fix-soon T5 tenant/oid audit, T7 NotFound/AlreadyExists at Warning, T5 keepalive redirect:'manual'; rest safely deferred.
- R47: malware-scan tag (spec §5.5) is surfaced in phase 1: BlobBackend reads the "Malware Scanning scan result" blob index tag in listing when present and StorageItem gains an optional ScanResult shown as a badge; Files has no equivalent (documented). If index-tag read needs an extra RBAC permission (Storage Blob Data Owner for tags) → record as a deferral in spec + ops doc instead — spec says "where present" — cost: a badge, or a documented deferral.
- R48: App Service access restriction to the App Gateway subnet goes into the Pulumi branch now (single app, this portal only), plus `clientAffinityEnabled: true` note for scale-out deferred — closes XFF forgery/WAF bypass — cost: one Pulumi resource property.
Final fix wave dispatched (app, one fixer) + Pulumi R48 (Orchestrator, separate fixer, separate repo).
R48: STOPPED by implementer (correctly): VNet-subnet access rule needs Microsoft.Web service endpoint on the SHARED App Gateway subnet (affects every app). Only scaling xmldoc committed (Orchestrator e4c5cea). Options in task-13-report.md "R48 access restriction": (1) add Microsoft.Web SE to gateway subnet + subnet Allow rule, default Deny, ScmIpSecurityRestrictionsUseMain (recommended); (2) allow gateway frontend public IP /32; (3) App Service private endpoint.
- R49: R48 shared-infra choice surfaced to Dan, not decided here — it modifies a shared subnet (outside this plan's authority) — cost if deferred: XFF forgery / WAF bypass remain possible until applied; documented as a go-live blocker before customer cut-over.
Final fix wave DONE (fefa322..847b43a: b9d6363, 3eac5cd, 847b43a): Owner lockout fixed via StaffAccess helper (runtime-verified), fail-fast without DB outside Dev (runtime-verified), refused-token audit rows, storage-gate architecture test (DevSignIn allow-listed: Dev-only, fake only — accepted), logging levels, StoragePaths delegation, keepalive redirect:'manual', .superpowers ignored, docs/spec/changelog. 1157 passed; webapp 8/8. Item 3 deferred per R47 (Blob Data Contributor lacks tags/read — verified on Microsoft Learn).
- R50: session-guard.js keepalive change diverges from the suite's shared copy (consistency-checker Check 34) — accept for this app now; propagating to the other 9 apps is a suite change for Dan to schedule — cost: a consistency-check flag until propagated.
Scoped re-review of fix wave dispatched.
Final fix wave re-review (opus): all 12 addressed, no Critical/Important (fefa322..847b43a).
Final: parked — five service-layer EffectiveRole call sites bypass StaffAccess (all correct; CLAUDE.md rule overstated) — Ruling: leave code, reword docs in a follow-up — behaviour correct, consistency only.
Final: parked — gate architecture test blind to service-locator use and inline Program.cs lambdas — Ruling: accept; note in test xmldoc in a follow-up — current code has no such use outside DevSignIn (Dev-only).
Final: parked — refused-token audit write tied to RequestAborted (can drop on client disconnect) — Ruling: accept; matches IdentityResolver; audit is fail-open by suite pattern.
Final: parked — any 3xx on /keepalive now signs out (e.g. gateway maintenance redirect) — Ruling: accept; same outcome as idle sign-out, fails closed.
Final: parked — Staff.razor reads stored role for display/edit (never reachable wrongly) — Ruling: accept.
Final: parked — changelog "signed out at its next activity" really within 5 min — Ruling: cosmetic, follow-up.
