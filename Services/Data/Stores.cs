using CloudSelfService.Models;

namespace CloudSelfService.Services.Data;

public interface IUserStore
{
    Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default);
    Task<AppUser?> FindByObjectIdAsync(string oid, CancellationToken ct = default);
    Task<AppUser?> FindByUpnAsync(string upn, CancellationToken ct = default);
    Task<IReadOnlyList<AppUser>> ListAsync(UserKind? kind = null, Guid? customerId = null, CancellationToken ct = default);
    Task UpsertAsync(AppUser user, CancellationToken ct = default);

    /// <summary>Changes only the Active flag, so it cannot overwrite anything a concurrent sign-in wrote. False when no such user.</summary>
    Task<bool> SetActiveAsync(Guid userId, bool active, CancellationToken ct = default);

    /// <summary>Changes only the staff role, of a Staff-kind user. False when there is no such staff user.</summary>
    Task<bool> SetStaffRoleAsync(Guid userId, StaffRole role, CancellationToken ct = default);

    /// <summary>
    /// Records a sign-in atomically: binds the oid and refreshes UPN, name and time ONLY while the user is still active
    /// and the record is unbound or already bound to this oid. Never writes Active or the role. Null when it matched no
    /// row (deactivated meanwhile, bound to another oid, or gone); the caller then refuses admission.
    /// </summary>
    Task<AppUser?> RecordSignInAsync(Guid userId, string oid, string upn, string displayName, DateTimeOffset now, CancellationToken ct = default);
}

public interface ICustomerStore
{
    Task<Customer?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(Customer c, CancellationToken ct = default);
}

public interface ITargetStore
{
    Task<StorageTarget?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<StorageTarget>> ListAsync(Guid? customerId = null, CancellationToken ct = default);
    Task UpsertAsync(StorageTarget t, CancellationToken ct = default);
}

public interface IGrantStore
{
    Task<Grant?> GetAsync(Guid customerId, Guid targetId, CancellationToken ct = default);
    Task<IReadOnlyList<Grant>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task SetAsync(Grant g, CancellationToken ct = default);
    Task RemoveAsync(Guid customerId, Guid targetId, CancellationToken ct = default);
}

public interface IAuditStore
{
    Task AppendAsync(AuditEvent e, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery q, CancellationToken ct = default);
}

public sealed record AuditQuery(Guid? UserId = null, Guid? CustomerId = null, Guid? TargetId = null,
    string? Operation = null, AuditResult? Result = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 500);
