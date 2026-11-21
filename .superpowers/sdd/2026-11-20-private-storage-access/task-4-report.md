# Task 4 report
Status: DONE_WITH_CONCERNS (live DB check deferred).
- RED: tests failed to compile (CloudSelfService.Services.Data missing). GREEN: 164 passed, 0 failed; build 0 errors.
- Files: Services/Data/{Stores,InMemoryStores,PgStores,PgsqlContext}.cs, Services/AuditService.cs, Program.cs, csproj (Npgsql 10.0.3), appsettings.json, appsettings.Development.json.sample, tests InMemoryStoresTests/PgSchemaTests.
- DI: all stores singletons (PG stateless over data source; in-memory shared state). Warning logged when in-memory. EnsureSchema runs only when configured.
- Schema: staging_path per R1; trigger via CREATE OR REPLACE FUNCTION + DROP TRIGGER IF EXISTS/CREATE CONSTRAINT TRIGGER (AFTER INSERT OR UPDATE), idempotent. PG SetAsync maps check_violation to InvalidOperationException.
- Concerns: Step 4 live check deferred to Task 13 (no ConnectionStrings:Default locally; schema SQL/PG stores never executed against a real DB, so untested). Dropping/recreating the trigger each startup takes a brief lock on grants.

## Fix round 1 (commit 6c627f1)
- .gitignore: Data/ -> /Data/; Services/Data (4 files) now tracked (git ls-files confirms). Remaining ignored items are unrelated (settings.local.json, .msg, appsettings.Development.json, review diffs).
- storage_targets BEFORE UPDATE trigger (targets_owner_change) rejects owner change while grants exist (check_violation -> InvalidOperationException). In-memory: InMemoryGrantStore now takes InMemoryTargetStore and shares its lock; target store consults a HasGrants callback (simplest, atomic). Tests TargetStore_ChangeOwnerWith(out)Grants added.
- Schema: triggers created only if absent (pg_trigger check), whole ensure in one transaction under pg_advisory_xact_lock. Added indexes grants(target_id), audit_events(customer_id/target_id).
- 23505 / 23514 mapped to InvalidOperationException in user/customer/target upserts; grant target check moved inside the lock; Program.cs bare block removed (comment: fail fast on purpose).
- PgStoresIntegrationTests (PgFact skips without CSSP_TEST_PG): 5 tests (idempotent+concurrent ensure, round trips, owner rules, duplicate UPN case-insensitive, audit filters).
- Real DB run (postgres:17-alpine via podman; localhost:55432 was not forwarded on this machine, used the WSL VM IP 172.21.108.241): Passed 5/5, Failed 0. No SQL fixes were needed. Container stopped.
- Without CSSP_TEST_PG: Passed 166, Skipped 5, Failed 0; build 0 errors 0 warnings.
