# SDD ledger — plan: docs/superpowers/plans/2026-11-25-large-uploads.md

Spec: docs/superpowers/specs/2026-11-25-large-uploads-design.md (approved by Dan 2026-11-25)
Branch: feat/large-uploads (from master 501e42c); plan+spec commit ca26707. Dan authorised local per-task commits; no push/sync.

## Pre-flight scan

| Pair / task | Produces vs consumes | Finding |
|---|---|---|
| T1 / T2 | T1 ChunkMath 2-arg API + mechanical caller swaps; T2 makes callers use session.ChunkSize | Consistent. T1 must leave behaviour identical (MinPieceSize everywhere). |
| T1 / T7 | T1 `FingerprintBytes` (C#); T2 renames JS `CHUNK_SIZE`→`FINGERPRINT_BYTES`; T7 test asserts it | Consistent. |
| T2 / T3 | Both edit UploadSessionService ctor/PutChunk and UploadEndpoints guard | T3 adds `PieceMemoryBudget` param before logger; T4 adds `UploadLimitService`. Order R1. |
| T2 / T4 | T2 Begin piece size; T4 moves size check after resumable lookup | R2. |
| T3 / T7 | T3 JS `wait` handling; T7 rewrites expiry handling in same file | Disjoint functions. OK. |
| T4 / T7 | `UploadLimitService.FormatGb`, `GetStateAsync` consumed by panel | Consistent names. |
| T5 / T6 | Both modify IUploadSessionStore (MarkChunkAsync signature; ListOpenForUserAsync) | Disjoint members. OK. |
| T6 / T7 | `ListUnfinishedAsync(userId, targetId, folder)`, `UnfinishedUpload` | Consistent. |
| T8 | docs only; values match Global Constraints | OK. |
| T1 self | tests vs constants | `PieceSizeFor(Blob, 0)` → 4 MiB; consistent. |
| T2 self | `Begin_LargeBlob...` needs limit > 195 GiB; pre-T4 limit comes from config clamped to MaxBlobBytes | OK via `maxBytes` helper. PieceSizeFor throws over MaxBlobBytes: R3. |
| T3 self | `ChargesTwoTokens` needs 8 MiB session row created directly | OK. |
| T4 self | "size check before authorisation, as today" vs "resolve resumable first (needs target)" | R2. |
| T5 self | Review Focus adds `Begin_SameFileOtherUser_NewSession` to T5 | Carry into T5 dispatch. |
| T6 self | page-level Discard uses existing AbortAsync | OK. |
| T7 self | no bUnit; static contract tests | OK. |

Ruling R1: UploadSessionService ctor parameter order after T4 = (access, sessions, backends, audit, time, config, budget, limits, logger) — keeps every later task's test helper edit mechanical — cost if wrong: trivial reorder.
Ruling R2: In T4, BeginAsync order is: input validation → authorise Upload → FindResumable(+first-piece match) → if not resumable: size limit + per-user caps. An oversized request without access now gets Denied rather than too_large — spec says limit applies to new uploads only and authorisation first is safer — cost if wrong: one message order test.
Ruling R3: Begin must never call `PieceSizeFor` for a size it hasn't already accepted against the effective limit (always ≤ MaxBlobBytes); no try/catch around it — cost if wrong: a 500 on a misconfiguration, caught by tests.

Task 1: dispatched (base ca26707, implementer ae614c4f015a9a4eb, sonnet)
Ruling R4: Task 1 kept the service clamp at MinPieceSize*MaxBlocksPerBlob (195.3 GiB) instead of MaxBlobBytes to keep behaviour identical — correct for a no-behaviour task; Task 4 removes MaxUploadBytesFrom entirely and replaces the clamp with UploadLimitService ceiling — cost if wrong: none, superseded.
Task 1: complete (commits ca26707..dbd87b0, review clean). Minors carried: stale "at most ChunkSize" comment in UploadSessionService (fix in T2); ChunkCount pieceSize<=0 guard (T2); MinPieceSize*MaxBlocksPerBlob stand-in removed in T4.
Task 2: dispatched (base dbd87b0, implementer aa39319deb6aca185, opus). Ruling R5: T2 raises MaxUploadBytesFrom clamp to MaxBlobBytes (needed for large-blob tests before T4) — T4 replaces it — cost if wrong: an operator config above 195 GiB takes effect one task early, nothing deployed.
Ruling R6: T2 ParallelChunks = min(_parallel, max(2, 16MiB/piece)) instead of Math.Clamp (throws when ParallelChunks=1) — matches spec values for ParallelChunks>=2 and stays valid at 1 — cost if wrong: none.
Note: memory unbounded-by-bytes between T2 and T3 (up to 2 GiB) — branch not deployable until T3; WAF 32 MiB body check stays in plan's "after all tasks" release step.
Task 2: complete (commits dbd87b0..76fce71, review clean). Minors carried to T3 (same files): guard body-limit test with stub IHttpMaxRequestBodySizeFeature; name the 16 MiB in-flight target constant; single parse of MinChunkBytesPerSecond; RateLimits.cs doc comment. ArrayPool large-buffer retention noted for T3.
Task 3: dispatched (base 76fce71, implementer ac7655f6ff2ea9100, opus)
Note T3: tokens charged before budget check (a 'wait' piece spends tokens on each retry) — reviewer to judge; security-auditor runs once after all tasks per plan.
Task 3: minor (deferred): wait 429 doesn't read body — client may re-send up to 32 MiB per retry; no jitter on Retry-After (final review to triage)
Task 3: minor (deferred): zero-byte reserve refused when usage > max (add bytes==0 ||)
Task 3: minor (deferred): ChunkThroughputLimiter public type inside RateLimits.cs — own file
Task 3: minor (deferred): RateLimits doc sentence "concurrency ... guard the request rate" wording
Task 3: minor (deferred): Uploader_HandlesWaitCode is string-contains only
Task 3: minor (deferred): ArrayPool.Shared retains 32 MiB LOH arrays outside the budget — decide before release
Ruling R7: budget-refused pieces spend no throughput tokens (charge after TryReserve, release lease if bucket refuses) + client resets busyWaits on 'wait' — spec: wait is not a failure — cost if wrong: one more limiter refactor. Task 3: fix round 1 dispatched (resume ac7655f6ff2ea9100).
Task 3: fix round 1/5 (1 addressed, 0 open; commits 5ac0719..f35c2c1)
Task 3: minor (deferred): PutChunkAsync chargeThroughput optional (null = no charge); no test pins endpoint passes it
Task 3: complete (commits 76fce71..f35c2c1, review clean after 1 fix round)
Task 4: dispatched (base f35c2c1, implementer a50219984b7f536ef, opus)
Ruling R8: on revert, the audit row's Bytes holds the default limit (brief silent) — symmetric with save (Bytes = new effective) — cost if wrong: one audit field.
Note T4: port 5300 held by PID 4 (http.sys) on this machine; implementer smoke-tested on 5377. Not an app defect.
Task 4: minor (deferred): UploadLimitService.SetOverrideAsync/ClearOverrideAsync public (make internal, self-invalidate) — security-relevant, final review should fix
Task 4: minor (deferred): no test for expiry inside the 30 s cache window
Task 4: minor (deferred): datetime-local guard test comment states unverified cause
Task 4: minor (deferred): Pg SetTwice_KeepsOneRow counts after clear
Task 4: minor (deferred): Staff_DeniedTheSettingsPage asserts only absence
Task 4: minor (deferred): Settings prefill truncates non-whole-GiB defaults (could be 0)
Task 4: minor (deferred): note text not checked for control characters
Task 4: minor (deferred): SettingsPageTests inside UploadLimitServiceTests.cs
Task 4: complete (commits f35c2c1..57f95e5, review clean)
Task 5: dispatched (base 57f95e5, implementer a56816ed9f5ca1590, sonnet)
Task 5: minor (deferred): no test that a session still ends at CreatedAt+7d while pieces keep arriving
Task 5: minor (deferred): MaxSessionDays < SessionHours/24 leaves Begin's longer expiry (harmless via GREATEST)
Task 5: complete (commits 57f95e5..158eb55, review clean)
Task 6: dispatched (base 158eb55, implementer a188371ddd5f7e92b, opus)
Task 6: minor (deferred): ListUnfinished is N+1 (GetLevel + ReceivedChunks per session), bounded by 8 open sessions
Task 6: complete (commits 158eb55..5c3bc0a, review clean)
Task 7: dispatched (base 5c3bc0a, implementer a013ca6f85cb77a7f, opus)
Session crash during Task 7 implementation: uncommitted T7 edits in tree; leftover dev app (pid 35572, :5310) stopped; resuming implementer a013ca6f85cb77a7f.
Task 7: original implementer stopped (user's machine out of memory); not resumable. Re-dispatched fresh implementer afe9f050be1bed231 (opus) to finish from the uncommitted tree, with memory limits (one app, one browser, close both after manual check).
Ruling R9: T7 first-run spurious signed-out = in-memory dev stores lost the user after the crash restart ('user record not found'), so the stale cookie was correctly rejected and the upload paused as designed — not a defect — cost if wrong: reviewer's expiry-classification check catches it.
Task 7: minor (deferred): waiting-row claim never released if Begin fails/cancelled before session — row hidden until reload (Files home still shows it)
Task 7: minor (deferred): "different file" notice shown for same file when row's session expired between load and pick
Task 7: minor (deferred): pick on prerendered waiting row before circuit is interactive is dropped silently
Task 7: minor (deferred): no test for GetStateAsync failure path; no render test for session-ending banner / signed-out sign-in link
Task 7: minor (deferred): checkSessionEnd warns new uploads after end time passed
Task 7: complete (commits 5c3bc0a..3972ac2, review clean)
Task 8: dispatched (base 3972ac2, implementer a1ebded2f88174518, sonnet)
Note T8: 8 chrome.exe processes are the user's own Google Chrome (Program Files, started 16:07, before T8) — left alone. T8 changed SmokeTests hard-coded 1.0.0 → 1.1.0 (needed by new top changelog entry).
Ruling R10: Settings page lost-click before circuit is a real defect — fix in product (form controls disabled until interactive via RendererInfo.IsInteractive) and make the web test wait for enabled Save instead of a retry loop — spec anti-goal: controls must do what they say — cost if wrong: small UI change.
Ruling R11: capacity note was my spec's error — 390-780 GiB uses 16 MiB×2 = 32 MiB/upload → ~8 at once; >780 GiB uses 32 MiB×2 → ~4. Fix operations.md and the spec line — cost if wrong: doc text.
Task 8: minor (folded into fix round): SmokeTests hard-coded "1.1.0" — replace with non-"dev" check.
Task 8: fix round 1 dispatched (resume a1ebded2f88174518)
Pre-existing (deferred, out of scope): other admin pages (Customers, CustomerDetail, Targets, Staff, AuditLog) share the prerender lost-click pattern — raise with Dan as follow-up.
Task 8: fix round 1/5 (4 addressed, 0 open; commits 60ced03..2fb1338)
Task 8: complete (commits 3972ac2..2fb1338, review clean after 1 fix round)
All 8 tasks complete. Next: final whole-branch review (base 501e42c..2fb1338).
Final review: dispatched (501e42c..2fb1338, a39a73720a0776366, opus)
Final review: With fixes. Important: (1) chunk PUT lost per-user request-rate bucket (regression); (2) UploadLimitService public write path; (3) security-auditor + unit-test-reviewer not yet run; (4) large-piece load behaviour (re-send on wait, no jitter, ArrayPool retention).
Ruling R12: fix wave includes I1, I2, the large-piece hardening from I4 (dedicated bounded ArrayPool; client backoff with jitter on repeated wait/busy, Retry-After for busy from bucket refill) but NOT a server-side blocking wait (holds request slots; backoff gets most of the bandwidth win) — the feature's purpose is >195 GiB uploads, so shipping it needing another round defeats it — cost if wrong: a somewhat larger fix wave.
Ruling R13: fix wave also takes cheap minors: docs 4 MiB→32 MiB (CLAUDE.md, security.md), spec §3 line vs R2, changelog wording, RateLimits comment, no-piece discard time on Files home, Files block IDs skipped in Complete, count-based received bytes for unfinished list, "Select file" disabled until interactive, operations.md prerequisite note for raising the limit above 195 GiB (WAF test). Remaining minors stay deferred per the reviewer's triage — cost if wrong: follow-up tickets.
Final review fix wave: dispatched.
Final fix wave: 2fb1338..9175b7f (4 commits), 1356 pass / 25 skip; web 11/11. Re-review dispatched.
Final fix wave re-review: all addressed (A, B, C1-C4, D1-D8), no new Critical/Important.
Final: minor (deferred): ChunkRequestsPerMinute comment/doc reasoning stale vs C3's 1-60 s Retry-After (no starvation; ParallelChunks 16 can overflow to busy+5s)
Final: minor (deferred): UploadLimitWriteArchitectureTests misses non-generic GetService+cast and calls via locals
Final: minor (deferred): CompleteAsync still reads all chunk rows (could use ReceivedSummaryAsync)
Final: minor (deferred): run Pg contract tests (ReceivedSummaryAsync, upload_limit_override, MarkChunk expiry) under CSSP_TEST_PG before release
Security-auditor: dispatched.
Security-auditor: 1 MEDIUM fixed (form bodies parsed by antiforgery under 32 MiB cap → guard returns 415 on form content types; commits 5bf5e10, 33a5643). LOW left: slow-body budget hold (4 colluding Contribute accounts could make others wait); ListUnfinished per-session queries bounded by 8. Full suite after: 1360 pass / 25 skip / 0 fail.
Unit-test-reviewer: dispatched.
Unit-test-reviewer: 4 gaps closed, no product bug; commit 6bac880; suite 1362 pass / 25 skip / 0 fail. Branch complete: 501e42c..6bac880.
