# Task 9 — unit-test-reviewer gap list (upload / transfer layer), HEAD 81719c3

Implement ALL items below. Use UploadSessionServiceTests helpers (Service(), Cleanup(), _fake.FailOnCall, _sessions, _audit).
IMPORTANT: FakeStorageBackend refuses non-staging names on FileShare but logs the call first — for guard tests assert
`Assert.DoesNotContain(_fake.Calls, c => c == "AbortUpload:<path>")`, not just the outcome.
Also: the existing test `Sweep_SessionReclaimedSinceListed_LeftAlone` never runs SweepAsync — rename it to describe what it tests (store-level), and add item 1 as the real sweep test.

CODE FIX (ruling R35): cap `Transfers:MaxUploadBytes` effective value at `ChunkMath.ChunkSize * 50_000` (Azure's 50,000 committed blocks per blob = 195.3 GiB at 4 MiB) — clamp with a logged Warning at startup if configured higher — and add `ChunkCount_EffectiveMaxUpload_WithinBlobBlockLimit` + a test that a configured 1 TiB is clamped. (Block-id D6 width is then always sufficient.)

P1
1. Sweep_StuckCompletingReclaimedDuringLandedCheck_LeftAloneNoAudit — Files 10 B session, Open→Completing claim Start; FailOnCall on HasLanded re-claims (Completing→Completing claim Start+2h) and returns null; SweepAsync(Start+1h) → still Completing with new claim, no audit rows, no AbortUpload call.
2. [SEC] Complete_AbortCompleteOrChunkDuringCommit_NotFoundAndNoSideEffects — inside CompleteUpload callback synchronously run AbortAsync, a second CompleteAsync and a Put → all NotFound; no AbortUpload call; exactly one CompleteUpload call; ends Completed with exactly one Success row.
3. [SEC] Abort_TamperedStagingPath_RefusedReopenedNoStorageCall — seed keep.txt; Begin; recreate row with StagingPath="keep.txt"; AbortAsync → Unavailable, status Open, no "AbortUpload:keep.txt" call, keep.txt bytes intact, no audit.
4. [SEC] Abort_TargetChangedKindSinceBegin_RefusedSessionStaysOpen — Begin on _share, put piece 0, upsert target Kind=BlobContainer; AbortAsync → not Ok, Open, StagingRemoved false, staging still present.
5. [SEC] Begin_ReplacingOlderSessionWithTamperedStaging_CleanupRefusedNotDeleted — seed keep.txt; Begin a.bin 10; recreate row StagingPath="keep.txt"; Begin a.bin 12 → Ok; older Aborted, CleanupRefused true, StagingRemoved false; no "AbortUpload:keep.txt"; exactly one Replaced row.
6. [SEC] Status_GrantRevokedAfterBegin_Denied + Abort_GrantRevokedAfterBegin_DeniedNoStorageCall — remove grant after Begin+piece → Status Denied with null Value; Abort Denied, no AbortUpload, status Open.
7. PgStoresIntegrationTests [PgFact] UploadSessionStore_RequireClaimWithNullClaim_Matches — never-claimed Open: TrySetStatusAsync(Open→Aborted, requireClaim:true, expectedClaim:null) true; Completing with null claimed_at appears in ListStuckCompletingAsync.
8. [SEC] Begin_ExpiredUnsweptOlderSessionOfSameFile_NotSubtractedFromCaps_Busy — maxOpen 1; Begin a.bin 10; time Start+25h unswept; Begin b.bin 1 (Ok); Begin a.bin 12 → Busy/busy; plus maxBytes variant.
9. [SEC] Begin_CompletingSessionStillCountsAgainstCaps_Busy — maxOpen 1 with a Completing session → Busy; maxBytes 100 with 60-byte Completing + Begin 41 → Busy. Run against in-memory store (and PG if a contract base is introduced — see 15).
10. Complete_ReceivedIncludesOutOfRangeIndex_StillMissingPiece_Invalid — 3-piece Files; put 0,1; MarkChunkAsync(id,7) and (id,-1) → Invalid "Upload is missing 1 piece.", no CompleteUpload, Open.
11. Begin_BlobOtherUsersStaleButCompletingSession_ConflictInProgress — contributor blob Begin at Start, Completing at Start+1m; at Start+40m colleague Begins same name → Conflict in_progress.
12. PutChunk_StagingLostButSessionAlreadyEnded_NoSecondAuditRowNoAbort — in WriteChunk callback set status Aborted then throw NotFound → Conflict restart; no InterruptedMessage row; no AbortUpload.
13. Sweep_ExpiredSessionReopenedAfterFailedComplete_Aborted — size 0; Complete fails Unreachable (back to Open, ClaimedAt=Start); sweep at Start+25h → Aborted, StagingRemoved, exactly one ExpiredMessage row (plus earlier Failed).
14. [SEC] Sweep_StuckCompletingTargetChangedKindOrDeleted_AbortedWithoutLandedCheck_RefusalRecorded — Theory: target upserted as Blob / target store empty → no HasLanded call, Aborted, CleanupRefused true, one Failed row InterruptedCommitMessage (CustomerId null when deleted).

P2
15. PurgeAsync_NeverDeletesOpenOrCompletingRows_EvenPastHardCutoff — in-memory AND PG (introduce a shared abstract `UploadSessionStoreContract` test base run by both, with the PG subclass gated by CSSP_TEST_PG; move 7 and 9's store parts into it if convenient).
16. Extend PutChunk_StorageMd5Mismatch_UnavailableAndChunkNotMarked: status Open, no non-Begin audit rows.
17. Begin_ResumeAfterFileAppearedOrVanished_UpdatesExistedAtBegin_AuditOperation — both directions; Complete audits Overwrite / Upload accordingly.
18. UploadEndpointTests PutChunks_PerUserConcurrencyCap_429_AndRejectionDoesNotSpendToken — MaxConcurrentChunksPerUser 1, ChunksPerMinute 2; gate first piece; same user 2nd → 429 busy; other user → 204; after release one more 204 then 429.
19. [SEC] UploadEndpointTests Complete_DeclaredJsonBodyOver16KiB_413FromGuard — 17 KiB StringContent application/json to /complete → 413 too_large, Open, no CompleteUpload.
20. UploadErrorCodesTests.UploadErrorCodes_WireValues_Pinned — literal values.

Low
21. Begin_FilesStagingNameLengthBoundary — 211-char leaf Ok on FileShare, 212 Invalid, 212 Ok on Blob (adapt to the actual StagingFits arithmetic; pin what the code does and say so).
22. RangeHeaderTests: "bytes=99999999999999999999-" and "bytes=+5-" → None.

If any test fails against current code beyond a wrong assumption here, it is a real defect: minimal fix + call it out.
