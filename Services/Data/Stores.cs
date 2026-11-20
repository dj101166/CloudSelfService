using CloudSelfService.Models;

namespace CloudSelfService.Services.Data;

public interface IUserStore
{
    Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default);
    Task<AppUser?> FindByObjectIdAsync(string oid, CancellationToken ct = default);
    Task<AppUser?> FindByUpnAsync(string upn, CancellationToken ct = default);
    Task<IReadOnlyList<AppUser>> ListAsync(UserKind? kind = null, Guid? customerId = null, CancellationToken ct = default);
    Task UpsertAsync(AppUser user, CancellationToken ct = default);
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
