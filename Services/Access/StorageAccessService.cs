using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Storage;

namespace CloudSelfService.Services.Access;

public enum AccessOutcome { Ok, Denied, NotFound, Invalid, AlreadyExists, Unavailable }

public sealed record AccessResult(AccessOutcome Outcome, string? Message = null);

public sealed record AccessResult<T>(AccessOutcome Outcome, T? Value, string? Message = null);

public sealed record ReachableTarget(StorageTarget Target, Customer? Customer, AccessLevel Level);

/// <summary>
/// The only gate to storage. Every call re-reads the user, customer, target and grant from the
/// stores, so a revoked grant, a deactivated user or customer, or a de-configured Owner takes
/// effect on the very next call — nothing is trusted from the circuit or the cookie.
/// Store (database) failures propagate unhandled by design, so the gate fails closed; callers at the
/// HTTP and circuit boundary must catch them.
/// </summary>
public sealed class StorageAccessService
{
    public const int PageSize = 200;
    public const string DeniedMessage = "You don't have access to do that.";
    public const string ReservedNameMessage = "That name is reserved.";
    public const string RootNotAllowedMessage = "Choose an item inside the share, not the share itself.";
    public const string NotAFileMessage = "That is a folder, not a file.";
    public const string SameLocationMessage = "The new name or location is the same as the current one.";
    public const string CancelledMessage = "Cancelled before it finished.";

    private readonly IUserStore _users;
    private readonly ICustomerStore _customers;
    private readonly ITargetStore _targets;
    private readonly IGrantStore _grants;
    private readonly IStorageBackendResolver _backends;
    private readonly AuditService _audit;
    private readonly OwnerConfig _owners;
    private readonly TimeProvider _time;
    private readonly ILogger<StorageAccessService> _logger;

    public StorageAccessService(IUserStore users, ICustomerStore customers, ITargetStore targets, IGrantStore grants,
        IStorageBackendResolver backends, AuditService audit, OwnerConfig owners, TimeProvider time,
        ILogger<StorageAccessService> logger)
    {
        _time = time;
        _logger = logger;
        _users = users;
        _customers = customers;
        _targets = targets;
        _grants = grants;
        _backends = backends;
        _audit = audit;
        _owners = owners;
    }

    private sealed record Resolution(Guid UserId, AppUser? User, StorageTarget? Target, AccessLevel Level, string Reason);

    public async Task<IReadOnlyList<ReachableTarget>> ListReachableTargetsAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, ct);
        if (user is not { Active: true })
            return Array.Empty<ReachableTarget>();

        var reachable = new List<ReachableTarget>();
        if (user.Kind == UserKind.Staff)
        {
            var customers = (await _customers.ListAsync(ct)).ToDictionary(c => c.Id);
            foreach (var t in await _targets.ListAsync(null, ct))
            {
                var level = AccessPolicy.EffectiveLevel(user, null, t, null);
                if (level >= AccessLevel.Read)
                    reachable.Add(new ReachableTarget(t, t.CustomerId is { } cid ? customers.GetValueOrDefault(cid) : null, level));
            }
        }
        else if (user.CustomerId is { } customerId)
        {
            var customer = await _customers.GetAsync(customerId, ct);
            if (customer is not { Active: true })
                return Array.Empty<ReachableTarget>();
            var grants = (await _grants.ListForCustomerAsync(customerId, ct)).ToDictionary(g => g.TargetId);
            foreach (var t in await _targets.ListAsync(customerId, ct))
            {
                var level = AccessPolicy.EffectiveLevel(user, customer, t, grants.GetValueOrDefault(t.Id));
                if (level >= AccessLevel.Read)
                    reachable.Add(new ReachableTarget(t, customer, level));
            }
        }

        return reachable
            .OrderBy(r => r.Customer?.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AccessLevel> GetLevelAsync(Guid userId, Guid targetId, CancellationToken ct = default) =>
        (await ResolveAsync(userId, targetId, ct)).Level;

    public async Task<AccessResult<StorageListing>> ListAsync(Guid userId, Guid targetId, string folder, string? continuation,
        AuditContext ctx, CancellationToken ct = default)
    {
        if (!TryNormalize(folder, out var path, out var error))
            return new(AccessOutcome.Invalid, null, error);

        var r = await ResolveAsync(userId, targetId, ct);
        if (!await AllowedAsync(r, targetId, StorageOperation.List, path, ctx))
            return new(AccessOutcome.Denied, null, DeniedMessage);

        try
        {
            var listing = await _backends.For(r.Target!).ListAsync(r.Target!, path, continuation, PageSize, ct);
            return new(AccessOutcome.Ok, listing);
        }
        catch (StorageFailureException failure)
        {
            LogFailure(failure, StorageOperation.List, targetId, path, ctx);
            return new(OutcomeOf(failure), null, failure.Message);
        }
    }

    /// <summary>Authorises a download and resolves the file. Only a denial is audited; the endpoint audits the transfer.</summary>
    public async Task<AccessResult<(StorageTarget Target, StorageItem Item)>> AuthorizeDownloadAsync(Guid userId, Guid targetId,
        string path, AuditContext ctx, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var error))
            return new(AccessOutcome.Invalid, default, error);
        if (normalized.Length == 0)
            return new(AccessOutcome.Invalid, default, RootNotAllowedMessage);

        var r = await ResolveAsync(userId, targetId, ct);
        if (!await AllowedAsync(r, targetId, StorageOperation.Download, normalized, ctx))
            return new(AccessOutcome.Denied, default, DeniedMessage);

        try
        {
            var item = await _backends.For(r.Target!).GetItemAsync(r.Target!, normalized, ct);
            if (item is null)
                return new(AccessOutcome.NotFound, default, StorageErrorMapper.NotFoundMessage);
            if (item.IsFolder)
                return new(AccessOutcome.Invalid, default, NotAFileMessage);
            return new(AccessOutcome.Ok, (r.Target!, item));
        }
        catch (StorageFailureException failure)
        {
            LogFailure(failure, StorageOperation.Download, targetId, normalized, ctx);
            return new(OutcomeOf(failure), default, failure.Message);
        }
    }

    public async Task<AccessResult> CreateFolderAsync(Guid userId, Guid targetId, string parent, string name,
        AuditContext ctx, CancellationToken ct = default)
    {
        if (!TryNormalize(parent, out var parentPath, out var error))
            return new(AccessOutcome.Invalid, error);
        if (!StoragePath.TryValidateName(name, out error))
            return new(AccessOutcome.Invalid, error);
        // Re-normalising the combined path enforces the overall length limit and the reserved names.
        if (!TryNormalize(StoragePath.Combine(parentPath, name), out var path, out error))
            return new(AccessOutcome.Invalid, error);

        var r = await ResolveAsync(userId, targetId, ct);
        if (!await AllowedAsync(r, targetId, StorageOperation.CreateFolder, path, ctx))
            return new(AccessOutcome.Denied, DeniedMessage);

        return await ExecuteAsync(r, StorageOperation.CreateFolder, path, ctx,
            (backend, t) => backend.CreateFolderAsync(t, path, ct));
    }

    /// <summary>Rename (same parent) and move (different parent) are one operation, both audited as Move.</summary>
    public async Task<AccessResult> MoveAsync(Guid userId, Guid targetId, string from, string to,
        AuditContext ctx, CancellationToken ct = default)
    {
        if (!TryNormalize(from, out var source, out var error) || !TryNormalize(to, out var dest, out error))
            return new(AccessOutcome.Invalid, error);
        if (source.Length == 0 || dest.Length == 0)
            return new(AccessOutcome.Invalid, RootNotAllowedMessage);
        if (source == dest)
            return new(AccessOutcome.Invalid, SameLocationMessage);
        if (StoragePaths.IsMoveIntoItself(source, dest))
            return new(AccessOutcome.Invalid, StorageErrorMapper.MoveIntoItselfMessage);

        var auditPath = $"{source} -> {dest}";
        var r = await ResolveAsync(userId, targetId, ct);
        if (!await AllowedAsync(r, targetId, StorageOperation.Move, auditPath, ctx))
            return new(AccessOutcome.Denied, DeniedMessage);
        // Azure Files names are case-insensitive, so "Docs" -> "docs/Sub" is a move into itself there; blob names are not.
        // Checked after authorisation because it needs the target's kind.
        if (r.Target!.Kind == StorageKind.FileShare && dest.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
            return new(AccessOutcome.Invalid, StorageErrorMapper.MoveIntoItselfMessage);

        return await ExecuteAsync(r, StorageOperation.Move, auditPath, ctx, async (backend, t) =>
        {
            var item = await backend.GetItemAsync(t, source, ct)
                ?? throw new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);
            await backend.MoveAsync(t, source, dest, item.IsFolder, ct);
        });
    }

    public async Task<AccessResult> DeleteAsync(Guid userId, Guid targetId, string path, AuditContext ctx,
        CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var error))
            return new(AccessOutcome.Invalid, error);
        if (normalized.Length == 0)
            return new(AccessOutcome.Invalid, RootNotAllowedMessage);

        var r = await ResolveAsync(userId, targetId, ct);
        if (!await AllowedAsync(r, targetId, StorageOperation.Delete, normalized, ctx))
            return new(AccessOutcome.Denied, DeniedMessage);

        return await ExecuteAsync(r, StorageOperation.Delete, normalized, ctx, async (backend, t) =>
        {
            var item = await backend.GetItemAsync(t, normalized, ct)
                ?? throw new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);
            await backend.DeleteAsync(t, normalized, item.IsFolder, ct);
        });
    }

    /// <summary>
    /// Authorisation without a storage call, for the upload API, which re-checks on every chunk. Only the
    /// root may be named for List; every other operation needs an item path. Only a denial is audited.
    /// </summary>
    public async Task<AccessResult<StorageTarget>> AuthorizeAsync(Guid userId, Guid targetId, StorageOperation op, string path,
        AuditContext ctx, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var error))
            return new(AccessOutcome.Invalid, null, error);
        if (normalized.Length == 0 && op != StorageOperation.List)
            return new(AccessOutcome.Invalid, null, RootNotAllowedMessage);

        var r = await ResolveAsync(userId, targetId, ct);
        if (!await AllowedAsync(r, targetId, op, normalized, ctx))
            return new(AccessOutcome.Denied, null, DeniedMessage);
        return new(AccessOutcome.Ok, r.Target);
    }

    private async Task<AppUser?> LoadUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.GetAsync(userId, ct);
        if (user is null) return null;
        // Customers never carry a staff role, whatever the record's StaffRole field says.
        return user.Kind == UserKind.Staff
            ? user with { StaffRole = _owners.EffectiveRole(user) }
            : user with { StaffRole = StaffRole.None };
    }

    private async Task<Resolution> ResolveAsync(Guid userId, Guid targetId, CancellationToken ct)
    {
        var user = await LoadUserAsync(userId, ct);
        var target = targetId == Guid.Empty ? null : await _targets.GetAsync(targetId, ct);

        Customer? customer = null;
        Grant? grant = null;
        if (user is { Kind: UserKind.Customer, CustomerId: { } customerId })
        {
            customer = await _customers.GetAsync(customerId, ct);
            if (target is not null && target.CustomerId == customerId)
                grant = await _grants.GetAsync(customerId, target.Id, ct);
        }

        var level = AccessPolicy.EffectiveLevel(user, customer, target, grant);
        var reason = user switch
        {
            null => "user not found",
            { Active: false } => "user inactive",
            { Kind: UserKind.Customer } when customer is not { Active: true } => "customer missing or inactive",
            _ when target is null => "target not found",
            _ when !target.Active => "target inactive",
            _ => $"access level {level}",
        };
        return new Resolution(userId, user, target, level, reason);
    }

    private async Task<bool> AllowedAsync(Resolution r, Guid targetId, StorageOperation op, string path, AuditContext ctx)
    {
        if (r.Target is not null && AccessPolicy.Allows(r.Level, op))
            return true;
        await RecordAsync(r, targetId, op, path, AuditResult.Denied,
            $"{r.Reason}; {op} needs {AccessPolicy.Required(op)}", ctx);
        return false;
    }

    private async Task<AccessResult> ExecuteAsync(Resolution r, StorageOperation op, string auditPath, AuditContext ctx,
        Func<IStorageBackend, StorageTarget, Task> action)
    {
        var target = r.Target!;
        try
        {
            await action(_backends.For(target), target);
        }
        catch (StorageFailureException failure)
        {
            LogFailure(failure, op, target.Id, auditPath, ctx);
            await RecordAsync(r, target.Id, op, auditPath, AuditResult.Failed, failure.Message, ctx);
            return new(OutcomeOf(failure), failure.Message);
        }
        catch (OperationCanceledException)
        {
            // A cancelled folder delete or move may have run partway, so it still leaves a row.
            await RecordAsync(r, target.Id, op, auditPath, AuditResult.Failed, CancelledMessage, ctx);
            throw;
        }
        catch (Exception ex) when (ex is not StorageFailureException)
        {
            _logger.LogError(ex, "Unexpected storage error during {Operation} on target {TargetId} at {Path} (correlation {CorrelationId}).",
                op, target.Id, auditPath, ctx.CorrelationId);
            await RecordAsync(r, target.Id, op, auditPath, AuditResult.Failed, StorageErrorMapper.OtherMessage, ctx);
            throw;
        }
        await RecordAsync(r, target.Id, op, auditPath, AuditResult.Success, "", ctx);
        return new(AccessOutcome.Ok);
    }

    // The user only sees the mapped message, so the underlying SDK exception is kept here for support.
    private void LogFailure(StorageFailureException failure, StorageOperation op, Guid targetId, string path, AuditContext ctx) =>
        _logger.LogWarning(failure.InnerException ?? failure,
            "Storage {Operation} failed on target {TargetId} at {Path}: {Kind} (correlation {CorrelationId}).",
            op, targetId, path, failure.Kind, ctx.CorrelationId);

    // Not tied to the request token: a client that disconnects mid-operation must not erase the audit row.
    private Task RecordAsync(Resolution r, Guid targetId, StorageOperation op, string path, AuditResult result, string error,
        AuditContext ctx) =>
        _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), r.UserId, r.User?.Upn ?? "",
            r.User is { Kind: UserKind.Customer } ? r.User.CustomerId : r.Target?.CustomerId, targetId, op.ToString(), path, null, result, error,
            ctx.SourceIp, ctx.CorrelationId), CancellationToken.None);

    private static AccessOutcome OutcomeOf(StorageFailureException failure) => failure.Kind switch
    {
        StorageFailureKind.NotFound => AccessOutcome.NotFound,
        StorageFailureKind.AlreadyExists => AccessOutcome.AlreadyExists,
        _ => AccessOutcome.Unavailable,
    };

    // Backends hide folder markers and upload staging names, so naming one directly is refused too.
    private static bool TryNormalize(string? raw, out string path, out string? error)
    {
        if (!StoragePath.TryNormalizePath(raw, out path, out error))
            return false;
        if (path.Length > 0 && path.Split('/').Any(StoragePaths.IsHiddenName))
        {
            path = "";
            error = ReservedNameMessage;
            return false;
        }
        return true;
    }
}
