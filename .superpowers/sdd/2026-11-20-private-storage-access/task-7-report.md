# Task 7 report — StorageAccessService

**Status:** DONE_WITH_CONCERNS (minor; see Concerns)
**Commit:** d4100fe "Add StorageAccessService as the single authorised gate to storage" (base df3bb55)
**Suite:** 434 passed / 6 skipped / 0 failed (was 355/6). `dotnet build CloudSelfService.csproj`: 0 warnings, 0 errors.

## What was built

`Services/Access/StorageAccessService.cs`: `AccessOutcome`, `AccessResult`, `AccessResult<T>`, `ReachableTarget`, and the service with the API from the brief. Per R14 it takes `Guid userId`.

How every call runs:
1. **Normalise paths first.** `StoragePath.TryNormalizePath` runs, plus a reserved-name check: any segment matching `StoragePaths.IsHiddenName` (`.folder` or a staging name) is refused. An invalid path returns `Invalid` with no audit row and no store or backend call.
2. **Re-read the caller from the stores.** `ResolveAsync` reads the user, customer, target and grant each time. A staff user's role comes from `OwnerConfig.EffectiveRole`, so a de-configured Owner counts as StaffAdmin. A customer user's StaffRole is forced to None. `Guid.Empty` is treated as no target.
3. **Compute the level.** `AccessPolicy.EffectiveLevel` handles user active, customer active and owned, target active, and grant matched.
4. **Deny when the level is short.** The service audits `Denied`, with the reason in `Error` (e.g. "target not found; List needs Read"), and returns `Denied` with "You don't have access to do that." The backend is never called. A foreign target, a missing one and `Guid.Empty` all return identical results.
5. **Call the backend** and map `StorageFailureException`: NotFound→`NotFound`, AlreadyExists→`AlreadyExists`, anything else→`Unavailable`. The result carries the mapper's message.
6. **Audit by kind of call:**
   - Mutating calls (CreateFolder, Move, Delete) audit Success, or Failed with the user-facing message.
   - List is not audited unless denied.
   - AuthorizeDownload and Authorize audit only `Denied` (R3).

Operation specifics:
- **Move:**
  - `Invalid` when either end is root, when from == to, or when `to` is inside `from`'s subtree. The subtree check is case-insensitive because Azure Files is, and it runs before the backend.
  - It does a GetItem to decide whether the source is a folder. A missing source is `NotFound`, audited Failed.
  - The audit path is `"from -> to"`, and the operation is `Move` for both rename and move.
- **Delete:** root is `Invalid`. GetItem decides file vs folder; a missing item is `NotFound`, audited Failed.
- **AuthorizeDownload:** root is `Invalid`; a folder is `Invalid` ("That is a folder, not a file."); a missing file is `NotFound`. On success it returns `(Target, Item)`.
- **AuthorizeAsync:** checks authorisation only, with no backend call. Root is allowed only for `List`.
- **ListReachableTargetsAsync:**
  - Staff with a role of Staff or above get every active target, with its customer (null for internal targets).
  - A customer user gets nothing if their customer is missing or inactive. Otherwise they get only their own customer's targets that carry a grant of Read or above.
  - Results are ordered by customer name (OrdinalIgnoreCase), then by display name.
- **Audit rows:**
  - The `CustomerId` is the customer user's customer, or the target's owning customer for staff.
  - Audit writes use `CancellationToken.None`, so a disconnecting client can't drop the row.
  - A cancelled mutating operation writes a Failed row ("Cancelled before it finished."), then rethrows, because a folder delete or move may have run partway.

`Program.cs`: `AddScoped<StorageAccessService>()`. `IStorageBackendResolver` was already registered by Task 6 (Fake in Dev with `Storage:UseFake`, otherwise `StorageBackendResolver`), so nothing more was needed there.

`AccessPolicyTests`: added the carried case `EffectiveLevel_CustomerUserWithStaffRoleOwner_NoGrant_None`.

## Tests (CloudSelfService.Tests/StorageAccessServiceTests.cs)

The fixture seeds:
- customers Alpha and Bravo;
- TA (Alpha's FileShare, granted to Alpha), TA2 (Alpha's, ungranted), TB (Bravo's BlobContainer, Read grant), an internal target (CustomerId null) and an inactive Alpha target that has a grant;
- users: a Read user and a Full user at A, a user at B, and staff with role None, Staff and StaffAdmin.

The Read and Full users at A are told apart by changing A's grant level, because grants are per customer.

All 15 named tests are present: `ListReachable_CustomerUser_OnlyOwnGrantedTargets`, `ListReachable_Staff_AllActiveTargets`, `ListReachable_StaffRoleNone_Empty`, `List_CustomerAOnTargetB_Denied_AndAudited_AndBackendNotCalled`, `Delete_ReadUser_Denied`, `Delete_FullUser_Succeeds_AndAuditsSuccess`, `Delete_Root_Invalid` (theory with 4 forms of root), `CreateFolder_InvalidName_Invalid_NoAudit`, `Move_IntoOwnSubtree_Invalid`, `Move_SameParent_AuditedAsMove`, `List_GrantRevokedBetweenCalls_SecondCallDenied`, `List_UserDeactivatedBetweenCalls_SecondCallDenied`, `List_BackendUnavailable_ReturnsUnavailableWithReadableMessage` (theory covering Unreachable and NotAuthorised), `List_PassesContinuationToken` (250 items → 200 + token, then 50 with no token, 250 distinct), `AuthorizeDownload_PathTraversal_Invalid`.

Extra tests, grouped by what they cover:
- **Reachable targets:** a customer user with an Owner StaffRole and the Owner UPN still gets only their own granted targets; an inactive customer gets an empty list; an unknown user gets an empty list.
- **GetLevel:** a de-configured Owner gets Full; `Guid.Empty` gets None; another customer's target gets None.
- **List:**
  - A foreign target, a missing target and `Guid.Empty` give identical Denied results, all audited, with no backend call.
  - Denied cases: an inactive target; staff with role None; a customer deactivated between calls; staff demoted to None between calls; an unknown user (audited with the user id).
  - Invalid paths (`..`, `a/../..`, a backslash, `.folder`, a staging name): Invalid, no audit row, no backend call.
  - A blank, whitespace or `/` path lists the root.
  - Listing your own target returns Ok with no audit row; a missing folder returns NotFound.
- **AuthorizeDownload:** your own file returns Ok and resolves `/Docs//a.txt`, with no audit row; another customer's target is Denied and audited as Download; a folder is Invalid; root is Invalid with no backend call; a missing file is NotFound with no audit row; a `.folder` marker is Invalid.
- **CreateFolder:** bad names (`a/b`, `..`, empty, `.folder`) are Invalid; Contribute succeeds and audits Success; Read is Denied; an existing folder returns AlreadyExists and audits Failed with the mapper's message.
- **Move:**
  - Invalid: a case-variant subtree move; every root or same-path form; traversal; a `.folder` destination.
  - Allowed: a case-only rename (`a.txt` → `A.txt`).
  - A folder moved to a different parent succeeds and is audited as Move.
  - Read is Denied and audited; a missing source is NotFound and audited Failed.
- **Delete:**
  - Contribute staff are Denied.
  - A recursive folder delete succeeds.
  - A missing item is NotFound and audited Failed; an unreachable backend gives Unavailable and is audited Failed with the message.
  - When staff act on a customer target, the audit row carries the target's customer.
  - A de-configured Owner can still delete, as StaffAdmin.
- **AuthorizeAsync:** Contribute upload returns Ok with the target, no audit row and no backend call; Read upload is Denied and audited as Upload with the path; invalid paths (root for Upload, traversal, a staging name for Overwrite) are Invalid with no audit row; List at root is Ok.

**TDD:**
- RED: the first run failed to compile with CS0246 because `StorageAccessService` did not exist.
- GREEN: 430/6 after implementation.
- I then added 4 more tests, all of which pass: case-insensitive subtree, case-only rename, staff audit customer id, de-configured Owner delete.

## Self-attack results

| Attack | Result |
|---|---|
| Path traversal (`..`, `a/../..`, `../TB/secret.txt`, backslash) | Invalid before any store or backend call; no audit row |
| Absolute or duplicated slashes (`/Docs//a.txt`) | Normalised to `Docs/a.txt`, which stays inside the target root |
| Hidden names (`.folder`, a `.x.cssupload-<32hex>` staging file) | Invalid in every operation, so a customer can't read, delete or overwrite markers or other users' in-flight staging files |
| Another customer's target id | Denied, with the same message as a missing id; audited; backend not called |
| A random or `Guid.Empty` target id | Same as above; `Guid.Empty` skips the store read |
| Inactive customer | Denied in every operation; empty reachable list |
| User deactivated between calls | The next call is Denied and audited |
| Grant revoked between calls | The next call is Denied |
| Staff role None, or demoted mid-session | Denied; empty reachable list |
| De-configured Owner | Treated as StaffAdmin (Full), not Owner; there is no level above Full anyway |
| Customer user with StaffRole=Owner and an Owner UPN | Role forced to None in `LoadUserAsync`; only own granted targets |
| Inactive target with a valid grant | Denied |
| null, whitespace or `/` paths | Root for List and AuthorizeAsync(List); Invalid for Delete, Move, AuthorizeDownload, and AuthorizeAsync on non-List operations |
| Move from or to root, from == to | Invalid |
| Move into own subtree, including a case variant | Invalid before the backend |
| Delete root | Invalid, no audit row |
| A non-StorageFailure exception from a backend | Propagates. Every real backend method is wrapped in `StorageErrorMapper.RunAsync`, so a raw SDK exception would be a backend bug. I did not add a catch-all: it would swallow programming errors. |
| Client disconnect mid-delete or move | Rows are written with `CancellationToken.None`; a cancelled mutating call audits Failed, then rethrows |

## Files
- D:\SC\Personal\CloudSelfService\Services\Access\StorageAccessService.cs (new)
- D:\SC\Personal\CloudSelfService\CloudSelfService.Tests\StorageAccessServiceTests.cs (new)
- D:\SC\Personal\CloudSelfService\CloudSelfService.Tests\AccessPolicyTests.cs (carried case)
- D:\SC\Personal\CloudSelfService\Program.cs (scoped registration and using)

## Concerns / notes for the controller
1. **Extra public constants.** `DeniedMessage`, `ReservedNameMessage`, `RootNotAllowedMessage`, `NotAFileMessage`, `SameLocationMessage` and `CancelledMessage` are public constants on the service. Tests and Tasks 8–10 can reference them.
2. **Move and Delete do a `GetItemAsync` before the backend call** to learn `isFolder`, because the backend API needs it. That makes them check-then-act: if the item changes type in between, the backend fails with NotFound or AlreadyExists, which is safe.
3. **A case-only rename (`a.txt` → `A.txt`) is allowed by the service.** On Azure Files it may fail with AlreadyExists (case-insensitive), or the backend may treat it as a no-op. That is the backend's behaviour and is not verified against live Azure.
4. **What the Denied audit row records:**
   - `Error` carries the internal reason (e.g. "target not found", "customer missing or inactive"). This is only for staff reviewing the audit log; the caller only ever sees the generic message.
   - For an unknown user, `UserDisplay` is "" and `UserId` is the claimed id.
5. **R3 is applied as written:** `AuthorizeDownloadAsync` writes no row for NotFound or Unavailable either. Task 8 must audit Download Failed when authorisation returns NotFound or Unavailable, if it wants those recorded.
6. **`AuthorizeAsync` does not check that the item exists.** Task 9 owns the overwrite and existence rules.
7. Per R5, I did not run unit-test-reviewer or security-auditor.

## Fix round 1 (commit 89d1ee4)

Suite: 444 passed / 6 skipped / 0 failed. App build: 0 warnings / 0 errors.

- **Important: storage failures are now logged.** The service now takes `ILogger<StorageAccessService>`. Every `StorageFailureException` catch in List, AuthorizeDownload and ExecuteAsync logs at Warning. The log carries `failure.InnerException ?? failure` as the exception object, plus targetId, operation, path, `failure.Kind` and `ctx.CorrelationId`. No file contents or tokens are logged.
  - Test `List_BackendUnavailable_LogsInnerException`: one Warning, the same inner exception instance, and the correlation id, target id and kind in the message.
  - Also tested: `AuthorizeDownload_BackendFailure_LogsWarning` and `Delete_BackendFailure_LogsWarningAndAuditsFailed`.
- **Unexpected exceptions in ExecuteAsync.** A new catch for any exception other than StorageFailure logs Error, audits Failed with `StorageErrorMapper.OtherMessage`, and rethrows. OperationCanceled is still caught earlier and audited as Cancelled. Test: `Delete_UnexpectedBackendException_AuditsFailed_LogsError_Propagates`.
- **Subtree check by storage kind.** Before authorisation, the service runs the Ordinal `StoragePaths.IsMoveIntoItself` check. After authorisation, once the target kind is known, an extra OrdinalIgnoreCase check runs for FileShare targets only. Tests: `Move_Files_CaseDifferentSubtree_Invalid`, `Move_Blob_CaseDifferentSubtree_Allowed`, and the earlier `Move_IntoOwnSubtreeDifferentCase_Invalid` (on TA, a FileShare).
  - Consequence: on a file share, a Contribute-less user who attempts a case-variant subtree move gets Denied rather than Invalid. That is harmless.
- **Staging regex.** `StoragePaths` now uses `RegexOptions.IgnoreCase`. In `StoragePathsTests`, uppercase hex is now `true`, and I added a case for an uppercase `.CSSUPLOAD-` literal.
- **Audit timestamps.** They now come from the injected `TimeProvider`, which `Program.cs` already registers as `TimeProvider.System`. The Denied audit test asserts `row.At == Now`.
- **Class xmldoc.** It now says store (DB) failures propagate unhandled by design (fail closed), so the HTTP and circuit boundary must catch them.
- **New tests:**
  - `List_TargetReassignedToOtherCustomerUnderExistingGrant_Denied` uses a test-only `StaleGrantStore` stub because the in-memory store enforces the owner rule. The list is Denied with no backend call, and the reachable list is empty.
  - `List_PercentEncodedDotDot_TreatedAsLiteralName`: the backend receives `%2e%2e/%2E%2E` literally, gets NotFound, and nothing is decoded.
  - `List_ContinuationTokenFromOtherTarget_UsesRequestedTarget`: TA's token used on TB lists only TB's items.
