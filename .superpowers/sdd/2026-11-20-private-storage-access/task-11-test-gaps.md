# Task 11 — unit-test-reviewer gap list (AdminService / AdminPageBase / AdminEndpoints), HEAD e3ccbbe

Implement ALL items. Tests: AdminServiceTests.cs, AdminPageBaseTests.cs, AdminEndpointTests.cs.

CODE FIX (R40): AdminService.IsValidUpn accepts a lone surrogate (EnumerateRunes maps it to U+FFFD, so the Surrogate category check never matches). Inside the rune loop reject `rune == Rune.ReplacementChar` (also rejects a literal U+FFFD), and reject private-use (UnicodeCategory.PrivateUse) for hygiene. Apply the same lone-surrogate rule to StoragePath strict validation if it has the same pattern (check; say what you found).
CODE ADD (R41): add a `staff-admin` dev persona in Authorization/DevSignIn.cs (StaffAdmin) so the StaffAdmin-allowed CSV path is testable; add `AuditCsv_StaffAdmin_200`.

[SEC] 1. IsValidUpn_LoneSurrogate_False ("a\uD800b@x.example") + AddCustomerUser same → Invalid, nothing stored; same theory pins "-a@x.example" false; 320-char UPN true, 321 false (adapt to actual limit; say).
[SEC] 2. SetStaffRole_UndefinedRole_Invalid_RoleUnchanged ((StaffRole)4, (StaffRole)(-1)).
[SEC] 3. SetGrant_UndefinedLevel_Invalid_NoGrantStored ((AccessLevel)99, (AccessLevel)(-1)).
[SEC] 4. SaveTarget_MoveToInternalWithGrants_Invalid (CustomerId=null with grants).
[SEC] 5. SetUserActive_StaffTargetByOwnerNotInConfig_Denied_SingleDeniedRow.
[SEC] 6. Mutation_ByStaffRoleOrUnknownActor_DeniedWithExactlyOneDeniedRow — theory over all 7 ops, actor _staff and Guid.NewGuid(); stores unchanged; unknown actor UserDisplay "" with Ctx IP/correlation.
[SEC] 7. AuditCsv_BeyondTransfersLimit_429 (Transfers:RequestsPerMinute=1; second GET 429; one ExportAudit row).
[SEC] 8. FilterSummary_* — no filters "(no filters)"; trimming; order user→customer→target→operation→result→from→to; capped at 500.
[SEC] 9. Act_WhileBusy_SecondCallIgnored.
[SEC] 10. Load_SessionGone_SessionEndedNotDenied_DataCleared; Page_RequiringOwner_PersistedOwnerNotInConfig_Denied.
11. Act_ActionThrows_StoreDown_NoReload_BusyReset; Act_UserReadThrows_StoreDown_ActionNotInvoked.
12. SaveTarget_Boundaries_And_Normalisation (account 3/24 ok; container 3/63 ok, 64 invalid; display 100/101; resource group ""/"rg/x"/91 invalid; (StorageKind)9 invalid; deleted CustomerId → "That customer no longer exists."; " CORE.Windows.NET " stored "core.windows.net"; allow-list config normalisation + fallback).
13. SaveCustomer_Boundaries_And_Enums (name 200/201; notes 2000/2001; undefined enums invalid; AzureGov Complete kept; duplicate-name store failure writes no row).
14. TestTarget_MissingTarget_NotFound_NoProbe; TestTarget_CallerCancelled_Propagates_NoFailedRow; timeout (non-caller OCE) → Unavailable + one Failed row (custom IStorageBackendResolver stub).
15. AdminAlert_For_MapsOutcomes; AuditFilters_EdgeInputs.
Low: AddCustomerUser_MissingCustomer_NotFound; AddCustomerUser_DisplayName201_Invalid.

If any test fails against current code beyond item 1 (known) and a wrong assumption here, it's a real defect: minimal fix + call it out.
