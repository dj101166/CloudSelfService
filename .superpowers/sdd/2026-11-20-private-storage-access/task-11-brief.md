### Task 11: Staff admin — customers, users, targets, grants, staff, audit

**Files:**
- Create:
  - `Services/AdminService.cs`
  - `Pages/Admin/Customers.razor`, `Pages/Admin/CustomerDetail.razor`, `Pages/Admin/Targets.razor`, `Pages/Admin/Staff.razor`, `Pages/Admin/AuditLog.razor`, each with a `.razor.css`
  - `docs/gov-cloud-guest-setup.md`
- Modify: `Shared/NavMenu.razor` (an Admin group shown only to StaffAdmin or higher; Staff shown only to Owner), `Shared/Icon.razor`
- Test: `AdminServiceTests.cs`

**Interfaces:**
- Consumes: stores, `AuditService`, `IStorageBackendResolver.ProbeAsync`, `CurrentUser`.
- Produces:

```csharp
public sealed class AdminService { // every method takes the acting user id, re-checks role, and audits
    public Task<AccessResult<Customer>> SaveCustomerAsync(Guid actorId, Customer c, CancellationToken ct = default);          // StaffAdmin
    public Task<AccessResult<AppUser>> AddCustomerUserAsync(Guid actorId, Guid customerId, string upn, string displayName, CancellationToken ct = default); // StaffAdmin
    public Task<AccessResult> SetUserActiveAsync(Guid actorId, Guid userId, bool active, CancellationToken ct = default);      // StaffAdmin; staff users: Owner only
    public Task<AccessResult<StorageTarget>> SaveTargetAsync(Guid actorId, StorageTarget t, CancellationToken ct = default);  // StaffAdmin
    public Task<AccessResult<string>> TestTargetAsync(Guid actorId, Guid targetId, CancellationToken ct = default);           // StaffAdmin; ProbeAsync
    public Task<AccessResult> SetGrantAsync(Guid actorId, Guid customerId, Guid targetId, AccessLevel level, CancellationToken ct = default); // StaffAdmin; None = remove
    public Task<AccessResult> SetStaffRoleAsync(Guid actorId, Guid staffUserId, StaffRole role, CancellationToken ct = default); // Owner
}
```

**Rules the tests pin.**
- Each method re-loads the actor and requires the role noted above, so a stale page can't act with a revoked role. Every change writes an audit row whose operation names the change, for example `Admin.SetGrant` with path `"{customer} → {target} = {level}"`.
- **`AddCustomerUserAsync`**
  - The UPN must contain `@` and must not belong to the staff domain. Otherwise it returns `Invalid` with "Advanced staff sign in with their own account and don't need to be added."
  - The UPN must be unique.
  - It creates `Kind=Customer` with a null `EntraObjectId`.
- **`SaveTargetAsync`**
  - Account name must match `^[a-z0-9]{3,24}$`.
  - Container name: `^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$` for both kinds.
  - Subscription ID must be a GUID.
  - Changing the owning customer of a target that already has grants returns `Invalid` ("Remove this target's grants before moving it to another customer.").
- **`SetGrantAsync`** refuses when the target's owner ≠ `customerId` (`Invalid`), on top of the DB trigger.
- **`SetStaffRoleAsync`**
  - Can't change the actor's own role.
  - Can't set `Owner`, because owners come from config only.
- **`TestTargetAsync`** returns the probe result's user message on failure, and "Reachable — the portal can list this {file share/container}." on success.

**Pages.**
- **`/admin/customers`**: a table (name, cloud, cross-tenant status, user count, target count, active) and an Add customer form.
- **`/admin/customers/{id}`** has three sections:
  - Users: add by UPN, activate/deactivate, last sign-in.
  - Storage: the targets this customer owns, each with a level dropdown (`None`/`Read`/`Contribute`/`Full`).
  - Cross-tenant: shown only when `Cloud = AzureGov`. It has the checklist from the doc, a status selector and the home tenant ID.
- **`/admin/targets`**: all targets, an add/edit form and a **Test access** button.
- **`/admin/staff`** (Owner only): staff users with a role dropdown. Config owners are shown read-only.
- **`/admin/audit-log`**: filters for user, customer, target, operation, result and date range. It shows the newest 500 rows and has a CSV export link (a `GET /api/admin/audit.csv` minimal endpoint under the StaffAdmin policy, with CSV-injection-safe cells that prefix `'` to values starting with `= + - @`).
- All pages carry `[Authorize(Policy = Policies.StaffAdmin)]`, except Staff, which carries `Policies.Owner`.

**`docs/gov-cloud-guest-setup.md`** is a customer-IT checklist. Customer side:
1. Entra admin center → External Identities → Cross-tenant access settings → Microsoft cloud settings → enable **Microsoft Azure Commercial**.
2. Organizational settings → Add organization → paste the Advanced tenant ID (lookup by domain does not work across clouds).
3. Allow outbound B2B collaboration for the users or groups who need the portal.
4. Send Advanced the users' **UPNs**, not their email addresses.

Then Advanced does the mirror image, invites the users by UPN, and the users redeem the invitation. The doc keeps a placeholder for our tenant ID rather than hard-coding it, and cites Microsoft's "Cross-cloud settings" page.

- [ ] **Step 1: Write the failing tests** (`AdminServiceTests`, in-memory):
  - `SetGrant_TargetOwnedByOtherCustomer_Invalid`
  - `SetGrant_ByStaffRole_Denied`
  - `SetGrant_None_RemovesGrant`
  - `AddCustomerUser_StaffDomainUpn_Invalid`
  - `AddCustomerUser_DuplicateUpn_Invalid`
  - `SaveTarget_BadAccountName_Invalid`
  - `SaveTarget_ChangeOwnerWithGrants_Invalid`
  - `SetStaffRole_Self_Invalid`
  - `SetStaffRole_ToOwner_Invalid`
  - `SetStaffRole_ByStaffAdmin_Denied`
  - `SetUserActive_StaffUserByStaffAdmin_Denied`
  - `EveryMutation_WritesAuditRow`
  - `AuditCsv_FormulaPrefix_Escaped`, which tests a pure `CsvCell.Escape(string)` helper placed in `AdminService.cs`
- [ ] **Step 2: Run them; expect FAIL.**
- [ ] **Step 3: Implement the service, pages, CSV endpoint and doc.**
- [ ] **Step 4: Run them; expect PASS.** Then verify in the running app (Dev sign-in):
  - as `staff-owner`: add a customer and user, register a target, grant Contribute, and use Test access (it returns the fake backend's result);
  - as `staff`: the Admin nav is absent and `/admin/customers` renders Access Denied in place;
  - as `customer-full`: `/admin/audit-log` renders Access Denied.
- [ ] **Step 5: Security review.** Invoke `security-auditor` on the admin surface and CSV export, then `unit-test-reviewer` on `AdminService`.
- [ ] **Step 6: Commit:** "Add staff admin for customers, users, targets, grants, staff roles and audit log".

---

