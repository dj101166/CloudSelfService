### Task 4: PostgreSQL schema, stores and audit

**Files:**
- Create: `Services/Data/PgsqlContext.cs`, `Services/Data/Stores.cs` (interfaces), `Services/Data/PgStores.cs` (Npgsql implementations), `Services/Data/InMemoryStores.cs` (used by the app when no DB is configured, and by the tests), `Services/AuditService.cs`
- Modify: `CloudSelfService.csproj` (add `Npgsql`), `Program.cs` (register services, call `EnsureSchemaAsync` at startup), `appsettings.json`, `appsettings.Development.json.sample`
- Test: `InMemoryStoresTests.cs`, `PgSchemaTests.cs`

**Interfaces:**
- Consumes: the Task 3 records and enums.
- Produces:

```csharp
public interface IUserStore {
    Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default);
    Task<AppUser?> FindByObjectIdAsync(string oid, CancellationToken ct = default);
    Task<AppUser?> FindByUpnAsync(string upn, CancellationToken ct = default);          // case-insensitive
    Task<IReadOnlyList<AppUser>> ListAsync(UserKind? kind = null, Guid? customerId = null, CancellationToken ct = default);
    Task UpsertAsync(AppUser user, CancellationToken ct = default);
}
public interface ICustomerStore {
    Task<Customer?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(Customer c, CancellationToken ct = default);
}
public interface ITargetStore {
    Task<StorageTarget?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<StorageTarget>> ListAsync(Guid? customerId = null, CancellationToken ct = default);
    Task UpsertAsync(StorageTarget t, CancellationToken ct = default);
}
public interface IGrantStore {
    Task<Grant?> GetAsync(Guid customerId, Guid targetId, CancellationToken ct = default);
    Task<IReadOnlyList<Grant>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task SetAsync(Grant g, CancellationToken ct = default);                // upsert
    Task RemoveAsync(Guid customerId, Guid targetId, CancellationToken ct = default);
}
public interface IAuditStore {
    Task AppendAsync(AuditEvent e, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery q, CancellationToken ct = default);
}
public sealed record AuditQuery(Guid? UserId = null, Guid? CustomerId = null, Guid? TargetId = null,
    string? Operation = null, AuditResult? Result = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 500);
public sealed class AuditService { // wraps IAuditStore; logs and swallows store failures so audit never breaks a user action
    public Task RecordAsync(AuditEvent e, CancellationToken ct = default);
}
```

**Schema** (`EnsureSchemaAsync`, idempotent, `CREATE … IF NOT EXISTS`). Column names are snake_case, and enums are stored as `TEXT` holding their names.
- `customers(id uuid pk, name text not null unique, active bool, notes text, home_tenant_id text, cloud text, cross_tenant_status text)`
- `users(id uuid pk, kind text not null, customer_id uuid null references customers, upn text not null, display_name text, entra_object_id text null unique, staff_role text not null default 'None', active bool, created_at timestamptz, last_sign_in_at timestamptz)`, plus a unique index on `lower(upn)`
- `storage_targets(id uuid pk, customer_id uuid null references customers, display_name text, subscription_id text, resource_group text, account_name text, kind text, container_name text, endpoint_suffix text not null default 'core.windows.net', active bool)`, plus `unique(account_name, kind, container_name)`
- `grants(customer_id uuid references customers, target_id uuid references storage_targets, level text, granted_by text, granted_at timestamptz, primary key(customer_id, target_id))`. A constraint trigger `grants_owner_check` raises unless `storage_targets.customer_id = NEW.customer_id`, so a grant can go only to the customer that owns the target.
- `audit_events(id uuid pk, at timestamptz not null, user_id uuid, user_display text, customer_id uuid, target_id uuid, operation text, path text, bytes bigint, result text, error text, source_ip text, correlation_id text)`, indexed on `(at desc)`. The store's SQL contains no `UPDATE` or `DELETE` on this table.
- `upload_sessions(id uuid pk, user_id uuid, target_id uuid, final_path text, staging_name text, size bigint, chunk_size int, overwrite bool, created_at timestamptz, expires_at timestamptz, status text)`
- `upload_chunks(session_id uuid references upload_sessions on delete cascade, idx int, primary key(session_id, idx))`

**Config.**
- `appsettings.json` gets these sections:
  - `"Auth": { "Provider": "Entra", "IdleTimeoutMinutes": 60, "IdleWarningSeconds": 120 }`
  - `"Entra": { "Instance": "https://login.microsoftonline.com", "TenantId": "", "ClientId": "", "StaffEmailDomain": "advancedutility.com" }`
  - `"Authorization": { "Owners": "" }`
  - `"Transfers": { "MaxUploadBytes": 53687091200, "ParallelChunks": 4, "SessionHours": 24 }`
- The sample file sets these, with every secret left blank:
  - `Auth:Provider` = `Dev`
  - `ConnectionStrings:Default` pointing at `cloudselfservice_dev` on `auseusdevpgsql.infinitycis.com` with `SSL Mode=Require`
  - `Kestrel:Endpoints:Http:Url` = `http://localhost:5300`
- With no `ConnectionStrings:Default`, the in-memory stores are registered instead.

**In-memory grant rule.** The in-memory `IGrantStore.SetAsync` enforces the same owner rule as the trigger, throwing `InvalidOperationException` on a mismatch. It needs `ITargetStore` injected for that.

- [ ] **Step 1: Write the failing tests.**
  - `InMemoryStoresTests`:
    - `GrantStore_Set_TargetOwnedByOtherCustomer_Throws`
    - `UserStore_FindByUpn_CaseInsensitive`
    - `UserStore_FindByObjectId_ReturnsBound`
    - `AuditStore_Query_FiltersByCustomerAndResult`
  - `PgSchemaTests.SchemaSql_HasOwnerTrigger` asserts the schema SQL constant contains `grants_owner_check`.
  - `PgSchemaTests.AuditStoreSql_NoUpdateOrDelete` asserts that none of `PgAuditStore`'s SQL constants contain `UPDATE` or `DELETE`.
- [ ] **Step 2: Run them; expect FAIL.**
- [ ] **Step 3: Implement the stores, schema, `AuditService` and DI registration.** PG stores use parameterised commands only.
- [ ] **Step 4: Run them; expect PASS.**
  - Then `dotnet run`. If VPN and the `_dev` database are available, the log should show the schema created.
  - If not, write in the task report that this check moves to Task 13.
- [ ] **Step 5: Commit:** "Add PostgreSQL schema, stores and append-only audit".

---

