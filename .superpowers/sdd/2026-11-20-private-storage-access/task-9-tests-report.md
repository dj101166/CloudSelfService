# Task 9 tests report

Status: DONE. No defects found in existing code; every new test passed against current behaviour.

## R35 code fix
- ChunkMath: `MaxBlocksPerBlob = 50_000`, `MaxUploadBytes = ChunkSize * 50_000` (195.3 GiB).
- UploadSessionService ctor: effective max = min(configured, ceiling); logs one Warning when clamped.
- Tests: ChunkMathTests.ChunkCount_EffectiveMaxUpload_WithinBlobBlockLimit; Begin_ConfiguredMaxAboveBlobBlockLimit_ClampedAndWarned (1 TiB -> clamped, ceiling Ok, ceiling+1 Invalid, one warning); Service_ConfiguredMaxWithinBlobBlockLimit_NotWarned.
- CHANGELOG.md entry added (no _changelog list exists in MainLayout.razor to update; grep found no TBD entry there).

## Items (UploadSessionServiceTests unless noted)
1 Sweep_StuckCompletingReclaimedDuringLandedCheck_LeftAloneNoAudit (+ renamed old test to TrySetStatus_RequireClaimWithStaleClaim_RefusedAtStoreLevel)
2 Complete_AbortCompleteOrChunkDuringCommit_NotFoundAndNoSideEffects
3 Abort_TamperedStagingPath_RefusedReopenedNoStorageCall
4 Abort_TargetChangedKindSinceBegin_RefusedSessionStaysOpen
5 Begin_ReplacingOlderSessionWithTamperedStaging_CleanupRefusedNotDeleted
6 Status_GrantRevokedAfterBegin_Denied, Abort_GrantRevokedAfterBegin_DeniedNoStorageCall
7 UploadSessionStore_RequireClaimWithNullClaim_Matches - in new UploadSessionStoreContractTests.cs (in-memory + PG), not PgStoresIntegrationTests
8 Begin_ExpiredUnsweptOlderSessionOfSameFile_NotSubtractedFromCaps_Busy (+ ...ByteCap_Busy variant)
9 Begin_CompletingSessionStillCountsAgainstOpenCap_Busy / ...ByteCap_Busy (service, in-memory); store part: UploadSessionStore_CompletingSessionsCountAgainstCaps (contract, in-memory + PG)
10 Complete_ReceivedIncludesOutOfRangeIndex_StillMissingPiece_Invalid
11 Begin_BlobOtherUsersStaleButCompletingSession_ConflictInProgress
12 PutChunk_StagingLostButSessionAlreadyEnded_NoSecondAuditRowNoAbort
13 Sweep_ExpiredSessionReopenedAfterFailedComplete_Aborted
14 Sweep_StuckCompletingTargetChangedKindOrDeleted_AbortedWithoutLandedCheck_RefusalRecorded (Theory: changed kind, deleted)
15 PurgeAsync_NeverDeletesOpenOrCompletingRows_EvenPastHardCutoff - abstract UploadSessionStoreContract base, InMemory subclass ([Fact]) and Pg subclass ([PgFact]); existing PgStoresIntegrationTests left as is
16 PutChunk_StorageMd5Mismatch extended: status Open, no non-Begin audit rows
17 Begin_ResumeAfterFileAppeared_... / Begin_ResumeAfterFileVanished_... (both directions, Complete audits Overwrite / Upload)
18 UploadEndpointTests.PutChunks_PerUserConcurrencyCap_429_AndRejectionDoesNotSpendToken
19 UploadEndpointTests.Complete_DeclaredJsonBodyOver16KiB_413FromGuard
20 UploadErrorCodesTests.UploadErrorCodes_WireValues_Pinned (new file; all 10 codes)
21 Begin_FilesStagingNameLengthBoundary: StagingFits permits leaf <= 255-(1+11+32)=211 chars; 211 Ok, 212 Invalid on Files, 212 Ok on Blob (pins the code's arithmetic; list's numbers matched)
22 RangeHeaderTests: bytes=99999999999999999999- and bytes=+5- added to Parse_Malformed_None theory

## Adaptations
- Service() helper gained an optional logger parameter; UploadAudits() gained an optional operation parameter.
- Abort tamper/kind-change result is Unavailable (StorageFailureException Other mapped), asserted for #3 and NotEqual Ok for #4 as the list said.

## Results
- `dotnet build` / `dotnet test`: 0 errors, 0 warnings. 781 passed, 10 skipped (PG/live without env), 0 failed.
- PG run (throwaway postgres:17-alpine via podman WSL VM IP 172.21.108.241:55432): all 11 Pg-filtered tests passed (incl. the 3 new contract tests). Container stopped afterwards.
