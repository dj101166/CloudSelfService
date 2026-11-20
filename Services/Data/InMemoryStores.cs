using CloudSelfService.Models;

namespace CloudSelfService.Services.Data;

public sealed class InMemoryUserStore : IUserStore
{
    private readonly Dictionary<Guid, AppUser> _items = new();
    private readonly object _lock = new();

    public Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_items.GetValueOrDefault(id));
    }

    public Task<AppUser?> FindByObjectIdAsync(string oid, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_items.Values.FirstOrDefault(u => u.EntraObjectId == oid));
    }

    public Task<AppUser?> FindByUpnAsync(string upn, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_items.Values.FirstOrDefault(
                u => string.Equals(u.Upn, upn, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<IReadOnlyList<AppUser>> ListAsync(UserKind? kind = null, Guid? customerId = null, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<AppUser>>(_items.Values
                .Where(u => (kind is null || u.Kind == kind) && (customerId is null || u.CustomerId == customerId))
                .OrderBy(u => u.Upn, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task UpsertAsync(AppUser user, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_items.Values.Any(u => u.Id != user.Id && string.Equals(u.Upn, user.Upn, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A user with UPN '{user.Upn}' already exists.");
            if (user.EntraObjectId is not null && _items.Values.Any(u => u.Id != user.Id && u.EntraObjectId == user.EntraObjectId))
                throw new InvalidOperationException("Entra object id is already bound to another user.");
            _items[user.Id] = user;
        }
        return Task.CompletedTask;
    }
}

public sealed class InMemoryCustomerStore : ICustomerStore
{
    private readonly Dictionary<Guid, Customer> _items = new();
    private readonly object _lock = new();

    public Task<Customer?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_items.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<Customer>> ListAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Customer>>(_items.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task UpsertAsync(Customer c, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_items.Values.Any(x => x.Id != c.Id && x.Name == c.Name))
                throw new InvalidOperationException($"A customer named '{c.Name}' already exists.");
            _items[c.Id] = c;
        }
        return Task.CompletedTask;
    }
}

public sealed class InMemoryTargetStore : ITargetStore
{
    private readonly Dictionary<Guid, StorageTarget> _items = new();
    // Shared with InMemoryGrantStore so the owner rule is enforced atomically across both.
    internal readonly object _lock = new();
    internal Func<Guid, bool> HasGrants { get; set; } = _ => false;

    internal StorageTarget? GetUnlocked(Guid id) => _items.GetValueOrDefault(id);

    public Task<StorageTarget?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_items.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<StorageTarget>> ListAsync(Guid? customerId = null, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<StorageTarget>>(_items.Values
                .Where(t => customerId is null || t.CustomerId == customerId)
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task UpsertAsync(StorageTarget t, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_items.Values.Any(x => x.Id != t.Id && x.AccountName == t.AccountName
                    && x.Kind == t.Kind && x.ContainerName == t.ContainerName))
                throw new InvalidOperationException("A target for that account, kind and container already exists.");
            if (_items.TryGetValue(t.Id, out var existing) && existing.CustomerId != t.CustomerId && HasGrants(t.Id))
                throw new InvalidOperationException("A target's owner cannot change while grants for it exist.");
            _items[t.Id] = t;
        }
        return Task.CompletedTask;
    }
}

public sealed class InMemoryGrantStore : IGrantStore
{
    private readonly Dictionary<(Guid, Guid), Grant> _items = new();
    private readonly InMemoryTargetStore _targets;
    private object _lock => _targets._lock;

    public InMemoryGrantStore(InMemoryTargetStore targets)
    {
        _targets = targets;
        targets.HasGrants = id => _items.Values.Any(g => g.TargetId == id);
    }

    public Task<Grant?> GetAsync(Guid customerId, Guid targetId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_items.GetValueOrDefault((customerId, targetId)));
    }

    public Task<IReadOnlyList<Grant>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Grant>>(_items.Values.Where(g => g.CustomerId == customerId).ToList());
    }

    public Task SetAsync(Grant g, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var target = _targets.GetUnlocked(g.TargetId);
            if (target is null || target.CustomerId != g.CustomerId)
                throw new InvalidOperationException("A grant can only go to the customer that owns the target.");
            _items[(g.CustomerId, g.TargetId)] = g;
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid customerId, Guid targetId, CancellationToken ct = default)
    {
        lock (_lock) _items.Remove((customerId, targetId));
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly List<AuditEvent> _items = new();
    private readonly object _lock = new();

    public Task AppendAsync(AuditEvent e, CancellationToken ct = default)
    {
        lock (_lock) _items.Add(e);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery q, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<AuditEvent>>(_items
                .Where(e => (q.UserId is null || e.UserId == q.UserId)
                         && (q.CustomerId is null || e.CustomerId == q.CustomerId)
                         && (q.TargetId is null || e.TargetId == q.TargetId)
                         && (q.Operation is null || e.Operation == q.Operation)
                         && (q.Result is null || e.Result == q.Result)
                         && (q.From is null || e.At >= q.From)
                         && (q.To is null || e.At <= q.To))
                .OrderByDescending(e => e.At)
                .Take(Math.Clamp(q.Limit, 1, 5000)).ToList());
    }
}
