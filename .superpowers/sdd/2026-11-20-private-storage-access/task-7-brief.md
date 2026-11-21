### Task 7: StorageAccessService — the single gate

**Files:**
- Create: `Services/Access/StorageAccessService.cs`
- Modify: `Program.cs` (register it as scoped, and register `IStorageBackendResolver`)
- Test: `StorageAccessServiceTests.cs`

**Interfaces:**
- Consumes: `AccessPolicy`, `StoragePath` (Task 3); the stores and `AuditService` (Task 4); `IStorageBackendResolver` and `StorageFailureException` (Task 6).
- Produces:

```csharp
public enum AccessOutcome { Ok, Denied, NotFound, Invalid, AlreadyExists, Unavailable }
public sealed record AccessResult(AccessOutcome Outcome, string? Message = null);
public sealed record AccessResult<T>(AccessOutcome Outcome, T? Value, string? Message = null);
public sealed record ReachableTarget(StorageTarget Target, Customer? Customer, AccessLevel Level);

public sealed class StorageAccessService {
    public Task<IReadOnlyList<ReachableTarget>> ListReachableTargetsAsync(Guid userId, CancellationToken ct = default);
    public Task<AccessLevel> GetLevelAsync(Guid userId, Guid targetId, CancellationToken ct = default);
    public Task<AccessResult<StorageListing>> ListAsync(Guid userId, Guid targetId, string folder, string? continuation, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult<(StorageTarget Target, StorageItem Item)>> AuthorizeDownloadAsync(Guid userId, Guid targetId, string path, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> CreateFolderAsync(Guid userId, Guid targetId, string parent, string name, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> MoveAsync(Guid userId, Guid targetId, string from, string to, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> DeleteAsync(Guid userId, Guid targetId, string path, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult<StorageTarget>> AuthorizeAsync(Guid userId, Guid targetId, StorageOperation op, string path, AuditContext ctx, CancellationToken ct = default); // used by Task 9
    public const int PageSize = 200;
}
```

**Rules the tests pin.**

Every public method that touches a target follows the same sequence:
1. Reload the user, customer, target and grant from the stores. Nothing is cached between calls.
2. Normalise each path with `StoragePath`. An invalid path returns `Invalid` with the path error and writes no audit row.
3. Compute the effective level. If it isn't enough for the operation, write an audit row with `Denied` and return `Denied`. The backend is never called.
4. Otherwise call the backend.
5. Map a `StorageFailureException` to an outcome: NotFound→`NotFound`, AlreadyExists→`AlreadyExists`, NotAuthorised/Unreachable/Other→`Unavailable` (with the mapper's message), Conflict→`Unavailable`.
6. Audit `Success` or `Failed` with the exception's user message.

`Denied` returns the message "You don't have access to do that." To a customer, a target that exists but isn't theirs looks exactly like one that doesn't exist: the result is `Denied`, never `NotFound`, so they can't probe for other customers' targets.

Operation-specific behaviour:
- **Listing:** passes `PageSize` and the continuation token through. It is **not** audited per page; only a denied list is audited.
- **`MoveAsync`:** is a rename when the parent of `from` equals the parent of `to`, and a move otherwise. Both need Contribute and both are audited as `Move`. `from` and `to` must both be non-root. Moving a folder into its own subtree returns `Invalid`.
- **`DeleteAsync`:** the root path returns `Invalid`.
- **`ListReachableTargetsAsync`:**
  - Staff with role ≥ Staff see every active target, each with its customer.
  - A customer user sees only targets of their own active customer that carry a grant with level ≥ Read.
  - Results are ordered by customer name, then target display name.

- [ ] **Step 1: Write the failing tests.**

Use the in-memory stores and `FakeStorageBackend`, with a fixture seeding:
- customers A and B;
- targets TA (owned by A, a FileShare) and TB (owned by B, a BlobContainer);
- users: a Read user and a Full user at A, one user at B, and staff with role None, Staff and StaffAdmin.

Tests:
- `ListReachable_CustomerUser_OnlyOwnGrantedTargets`
- `ListReachable_Staff_AllActiveTargets`
- `ListReachable_StaffRoleNone_Empty`
- `List_CustomerAOnTargetB_Denied_AndAudited_AndBackendNotCalled`
- `Delete_ReadUser_Denied`
- `Delete_FullUser_Succeeds_AndAuditsSuccess`
- `Delete_Root_Invalid`
- `CreateFolder_InvalidName_Invalid_NoAudit`
- `Move_IntoOwnSubtree_Invalid`
- `Move_SameParent_AuditedAsMove`
- `List_GrantRevokedBetweenCalls_SecondCallDenied`: remove the grant in the store between two calls.
- `List_UserDeactivatedBetweenCalls_SecondCallDenied`
- `List_BackendUnavailable_ReturnsUnavailableWithReadableMessage` (Review Focus 4)
- `List_PassesContinuationToken` (Review Focus 5): the fake backend has 250 items; the first call returns 200 plus a token, and the second call with that token returns the remaining 50.
- `AuthorizeDownload_PathTraversal_Invalid`: `"../TB/secret.txt"`.
- [ ] **Step 2: Run them. Expect FAIL.**
- [ ] **Step 3: Implement `StorageAccessService`.**
- [ ] **Step 4: Run them. Expect PASS.**
- [ ] **Step 5: Invoke the `unit-test-reviewer` agent** on `Services/Access/` and add any gaps it finds.
- [ ] **Step 6: Commit:** "Add StorageAccessService as the single authorised gate to storage".

---

