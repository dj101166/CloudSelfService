# R46 report
- Data Protection keys persist under DATA_ROOT/dp-keys (helper DataProtectionPaths.KeyDirectory); warn paths for unset/failed.
- audit_events_append_only() function + audit_events_no_change (UPDATE/DELETE row) + audit_events_no_truncate (TRUNCATE statement) triggers, idempotent in EnsureSchemaAsync.
- Tests: helper, SchemaSql names, PG integration (UPDATE/DELETE/TRUNCATE rejected with 42501, EnsureSchema re-run).
- Docs: operations.md, CLAUDE.md, CHANGELOG.
- Result: build 0 warn/0 err; 1132 pass, 0 fail, 1 skip (live storage); PG run via 172.21.108.241:55432; container stopped.

## Cleanup
Restored LF on six files (net diff +110 lines), merged Security changelog sections, moved sign-in bullet to Added, added three ops notes. Build 0/0; suite 0 failures (PG tests skipped this run, no logic change).
