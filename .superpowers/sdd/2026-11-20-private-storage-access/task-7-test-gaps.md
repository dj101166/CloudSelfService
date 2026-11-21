# Task 7 — unit-test-reviewer gap list (StorageAccessService / AccessPolicy / StoragePath)

Source: unit-test-reviewer run against HEAD 89d1ee4 (read-only). Implement ALL items 1–20 below.
Controller ruling on item 17 (R25): the audit log is staff-only, so the Denied reasons are DELIBERATELY different — pin both reason strings
("access level None; List needs Read" for a foreign target and "target not found; List needs Read" for a missing one) rather than asserting equality.

Fixture names (StorageAccessServiceTests): _fullUserA has Full on _ta; _tb is Bravo's blob container; _userB has Read on _tb.
Existing private helpers: ThrowingBackend, StaleGrantStore, ListLogger. In-memory grant store refuses cross-customer grants, so item 9 needs a custom grant fake.

P1 (security-relevant)
1. Authorize_ForeignOrMissingTarget_DeniedNullValueIdentical_AuditedOnce — Theory over Upload / Overwrite / List(path ""): AuthorizeAsync(_fullUserA, X) for X = _tb.Id, Guid.NewGuid(), Guid.Empty → all three results equal, Denied, Value null; exactly 3 Denied rows; _fake.Calls empty.
2. AuthorizeDownload_ForeignTarget_IndistinguishableFromMissingAndEmptyId — PutFile(_tb,"secret.txt"); same three X → equal, Denied, Target/Item null; backend call count unchanged after seeding; 3 Denied rows.
3. Mutations_ForeignTarget_DeniedAuditedOnce_BackendUntouched — Theory CreateFolder("","New") / Move("Docs"→"Docs2") / Delete("Docs") on _tb as _fullUserA after Seed(_tb,"Docs") → Denied + DeniedMessage; no backend calls after seed; Docs still exists; one Denied row with CustomerId=_custA, TargetId=_tb; Move row Path "Docs -> Docs2".
4. Move_FileShareCaseSubtreeOnForeignTarget_DeniedNotInvalid — add Bravo FileShare _tbShare; MoveAsync(_fullUserA,_tbShare,"Docs","docs/Docs") and same against Guid.NewGuid() → equal, Denied, one Denied row each (no kind oracle).
5. Authorize_GrantDowngradedOrRevokedBetweenChunks_SecondCallDenied — Theory downgrade to Read / remove grant between two AuthorizeAsync(Upload,"big.bin") calls → second Denied, null Value, one Denied row Operation "Upload".
6. List_UserReassignedToOtherCustomerBetweenCalls_SecondCallDenied — List ok; upsert _fullUserA with CustomerId=_custB; List Denied; ListReachable returns only _tb at Read; audit row CustomerId=_custB.
7. Delete_HiddenName_Invalid_NoBackend_NoAudit — Theory ".folder", "Docs/.folder", ".a.txt.cssupload-<32 lower hex>", ".A.TXT.CSSUPLOAD-<32 UPPER hex>", plus Move with hidden name as SOURCE → Invalid, ReservedNameMessage, no calls, no audit.
8. List_HiddenNameInMiddleSegment_Invalid — "a/.folder/b", "a/.x.cssupload-<hex>/b" → Invalid, no audit, no backend.
9. ListReachable_TargetStoreIgnoresCustomerFilter_StillOnlyOwnGrantedTargets — leaky ITargetStore wrapper returning all targets + IGrantStore fake returning grants for _ta,_tb,_internal under custA → only _ta.
10. ListReachableAndList_GrantLevelNone_ExcludedAndDenied — GrantA(None) → ListReachable empty; List Denied, no backend call.
11. Delete_Cancelled_AuditsFailedCancelled_UsesNonCancellableToken_Rethrows — ThrowingBackend(OperationCanceledException), audit-store spy capturing ct; cancelled token passed → throws OCE; one Failed row Error=CancelledMessage Path "a.txt"; captured ct.CanBeCanceled == false.
12. AccessPolicyTests.Required_UnknownOperation_IsFull — Required((StorageOperation)99)==Full; Allows(Contribute,(StorageOperation)99) false.
13. AccessPolicyTests.EffectiveLevel_UnknownStaffRole_None — staff (StaffRole)99, active target, no grant → None.
14. ListReachable_InactiveUser_Empty — Theory _fullUserA / _staffAdmin set Active=false → empty.
15. CustomerUserWithNullCustomerId_ListReachableEmpty_ListDenied — Customer-kind user with CustomerId null and StaffRole StaffAdmin → ListReachable empty; List on _internal and _ta Denied; one Denied row each.
16. List_StoreThrows_PropagatesAndBackendNotCalled — IGrantStore.GetAsync throws InvalidOperationException → ThrowsAsync<InvalidOperationException>; no backend calls.
17. Denied_ForeignVsMissingTarget_AuditReasonsPinned — per R25: foreign row Error == "access level None; List needs Read"; missing row Error == "target not found; List needs Read" (use the actual constants/format in the code; if the strings differ, pin what the code produces and say so).

P2 (correctness)
18. CreateFolder_InvalidParentOrOverlongCombined_Invalid_NoAudit_NoBackend — (".." , "x"), (".folder","x"), (5×200-char segments joined = 1004 chars, 30×'z') → Invalid, no audit, no backend.
19. StoragePathTests: TryNormalizePath_TotalExactly1024_Accepted (build exactly 1024 chars of valid segments ≤255) and TryNormalizePath_Failure_LeavesNormalizedEmpty ("a/.." → false, normalized == "").
20. Extend Move_InvalidPaths_Invalid_NoBackend with Assert.Empty(audits); Move_ReadUser_Denied asserts audit Path "Docs -> Docs2".

If any test FAILS against current code (other than by a wrong assumption in this list), that is a real defect: fix the code minimally, and report it clearly.
