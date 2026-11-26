
- Work on branch `feat/large-uploads` in `D:\SC\Personal\CloudSelfService`. Commit steps run only once Dan has authorised commits for this execution. **Never add a `Co-Authored-By` trailer.** Preserve each file's existing line endings.
- Every storage read/write stays behind `StorageAccessService`; `StorageGateArchitectureTests` allow-list unchanged. No storage URL, key or SAS to the browser.
- Piece sizes: `MinPieceSize` = 4 MiB (4,194,304), `MaxBlobPieceSize` = 32 MiB (33,554,432), Files always 4 MiB, `MaxBlocksPerBlob` = 50,000. Blob size steps 4 → 8 → 16 → 32 MiB, smallest that keeps count ≤ 50,000.
- `firstPieceMd5` is always the MD5 of the file's first 4 MiB (or the whole file if smaller), independent of piece size.
- Limit defaults: `Transfers:MaxUploadBytes` **107,374,182,400** (100 GiB); `Transfers:MaxUploadBytesCeiling` **1,099,511,627,776** (1 TiB), clamped to **1,677,721,600,000** bytes (32 MiB × 50,000 = 1,562.5 GiB) with a startup warning.
- Memory budget: `Transfers:MaxInFlightPieceBytes` **268,435,456** (256 MiB) app-wide; `Transfers:MaxInFlightPieceBytesPerUser` **67,108,864** (64 MiB). Over budget → 429 with code `wait` and `Retry-After`.
- Per-user throughput unchanged: the chunk token bucket is charged one token per started 4 MiB of the piece (a 32 MiB piece costs 8).
- Parallel pieces returned by Begin: `clamp(16 MiB / pieceSize, 2, ParallelChunks)` (4 MiB → 4, larger → 2).
- Session lifetime: `expires_at = min(created_at + Transfers:MaxSessionDays, lastPieceAt + Transfers:SessionHours)`; defaults `SessionHours` 24, `MaxSessionDays` 7. Begin sets `expires_at = created_at + SessionHours` as today.
- Admin settings page `/admin/settings`, `[Authorize(Policy = Policies.StaffAdmin)]`, title `Settings — Advanced Cloud Self Service`. Audit operation `Admin.SetUploadLimit`.
- sumi CSS tokens only; `data-testid` selectors; comments only for a non-obvious WHY.
- Tests: `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` (name the project). Names `Method_Condition_Expected`. PostgreSQL integration tests run when `CSSP_TEST_PG` is set.
- Changelog: `CHANGELOG.md`, new top entry `## [1.1.0] - TBD`, glyph sections in order 🐛 🔄 ✨ 🗑️ 🛡️; no em-dashes, no customer names.
- Never run `pulumi`, `az` auth or podman auth commands.


Ledger rulings that bind tasks:
- R1: UploadSessionService ctor parameter order after Task 4 = (access, sessions, backends, audit, time, config, budget, limits, logger).
- R2: BeginAsync order after Task 4: input validation → authorise Upload → FindResumable (+first-piece match) → only if not resumable: size limit + per-user caps.
- R3: Begin never calls ChunkMath.PieceSizeFor for a size not already accepted against the effective limit (always ≤ MaxBlobBytes).
- Project security rules: every storage read/write via StorageAccessService; StorageGateArchitectureTests allow-list unchanged; no storage URL/key/SAS to the browser; every @page has [Authorize] or a policy.
