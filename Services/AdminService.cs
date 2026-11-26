using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CloudSelfService.Models;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Storage;
using CloudSelfService.Services.Transfers;

namespace CloudSelfService.Services;

/// <summary>
/// Staff administration: customers, their users, storage targets, grants and staff roles. Every method re-loads the
/// acting user and checks the role itself, so a page holding a stale role cannot act with it, and every change writes
/// one audit row. Store (database) failures propagate; the page catches them. A refusal for lack of role is audited as
/// Denied; input that fails validation changes nothing and writes nothing.
/// </summary>
public sealed partial class AdminService
{
    public const string SaveCustomerOp = "Admin.SaveCustomer";
    public const string AddCustomerUserOp = "Admin.AddCustomerUser";
    public const string SetUserActiveOp = "Admin.SetUserActive";
    public const string SaveTargetOp = "Admin.SaveTarget";
    public const string TestTargetOp = "Admin.TestTarget";
    public const string SetGrantOp = "Admin.SetGrant";
    public const string SetStaffRoleOp = "Admin.SetStaffRole";
    public const string SetUploadLimitOp = "Admin.SetUploadLimit";

    public const string DefaultEndpointSuffix = "core.windows.net";
    public const string StaffUpnMessage = "Advanced staff sign in with their own account and don't need to be added.";
    public const string OwnersFromConfigMessage = "Owners are managed in configuration, not here.";

    private static readonly string[] DefaultSuffixes = { DefaultEndpointSuffix };

    private readonly IUserStore _users;
    private readonly ICustomerStore _customers;
    private readonly ITargetStore _targets;
    private readonly IGrantStore _grants;
    private readonly IStorageBackendResolver _backends;
    private readonly AuditService _audit;
    private readonly OwnerConfig _owners;
    private readonly TimeProvider _time;
    private readonly string _staffDomain;
    private readonly HashSet<Guid> _allowedSubscriptions;
    private readonly ProbeCooldown _cooldown;
    private readonly UploadLimitService _limits;
    private readonly IUploadLimitStore _limitStore;
    private readonly ILogger<AdminService>? _logger;
    private readonly TimeSpan _testTimeout;

    public const string InternalOwnerOnlyMessage = "Only an Owner can register internal storage.";
    public const string SubscriptionNotAllowedMessage = "That subscription isn't on the allowed list for this portal.";
    public const string TestTimeoutMessage = "The storage account didn't respond in time.";
    public const string TestCooldownMessage = "Wait a few seconds before testing again.";
    public const double DefaultTestTimeoutSeconds = 20;
    public static readonly TimeSpan TestCooldown = TimeSpan.FromSeconds(5);

    public AdminService(IUserStore users, ICustomerStore customers, ITargetStore targets, IGrantStore grants,
        IStorageBackendResolver backends, AuditService audit, OwnerConfig owners, TimeProvider time, IConfiguration config,
        UploadLimitService limits, IUploadLimitStore limitStore, ProbeCooldown? cooldown = null, ILogger<AdminService>? logger = null)
    {
        _logger = logger;
        _limits = limits;
        _limitStore = limitStore;
        _users = users;
        _customers = customers;
        _targets = targets;
        _grants = grants;
        _backends = backends;
        _audit = audit;
        _owners = owners;
        _time = time;
        _cooldown = cooldown ?? new ProbeCooldown(time);
        _testTimeout = TimeSpan.FromSeconds(Math.Max(0.001, config.GetValue<double?>("Admin:TestAccessTimeoutSeconds") ?? DefaultTestTimeoutSeconds));
        _staffDomain = config["Entra:StaffEmailDomain"] is { Length: > 0 } d ? d : "advancedutility.com";
        var configured = config.GetSection("Storage:AllowedEndpointSuffixes").Get<string[]>()
            ?.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToArray();
        AllowedEndpointSuffixes = configured is { Length: > 0 } ? configured : DefaultSuffixes;
        _allowedSubscriptions = ParseAllowedSubscriptions(config);
    }

    public IReadOnlyList<string> AllowedEndpointSuffixes { get; }

    /// <summary>
    /// The configured subscription allow-list. Any non-blank entry that is not a GUID throws: dropping it would leave an
    /// empty set, which means "any subscription", so a typo would silently open registration up.
    /// </summary>
    public static HashSet<Guid> ParseAllowedSubscriptions(IConfiguration config)
    {
        var set = new HashSet<Guid>();
        foreach (var raw in config.GetSection("Storage:AllowedSubscriptions").Get<string[]>() ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Guid.TryParse(raw.Trim(), out var id) || id == Guid.Empty)
                throw new InvalidOperationException($"Storage:AllowedSubscriptions contains an invalid subscription id: {raw}");
            set.Add(id);
        }
        return set;
    }

    /// <summary>Empty means any subscription the managed identity can reach may be registered.</summary>
    public bool HasSubscriptionAllowList => _allowedSubscriptions.Count > 0;

    /// <summary>The operation names the audit page offers as a filter.</summary>
    public static readonly IReadOnlyList<string> AuditOperationNames = new[]
    {
        nameof(StorageOperation.List), nameof(StorageOperation.Download), nameof(StorageOperation.Upload),
        nameof(StorageOperation.Overwrite), nameof(StorageOperation.CreateFolder), nameof(StorageOperation.Move),
        nameof(StorageOperation.Delete), "UploadBegin", "SignIn",
        SaveCustomerOp, AddCustomerUserOp, SetUserActiveOp, SaveTargetOp, TestTargetOp, SetGrantOp, SetStaffRoleOp, SetUploadLimitOp,
        ExportAuditOp,
    };

    [GeneratedRegex(@"^[a-z0-9]{3,24}$")]
    private static partial Regex AccountNameRegex();

    [GeneratedRegex(@"^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$")]
    private static partial Regex ContainerNameRegex();

    [GeneratedRegex(@"^[\w\-\.\(\)]{1,90}$")]
    private static partial Regex ResourceGroupRegex();

    public async Task<AccessResult<Customer>> SaveCustomerAsync(Guid actorId, Customer c, AuditContext? ctx = null,
        CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.StaffAdmin, SaveCustomerOp, c.Name, c.Id, null, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, null, StorageAccessService.DeniedMessage);

        var name = c.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200) return new(AccessOutcome.Invalid, null, "Customer name is required (up to 200 characters).");
        var notes = c.Notes?.Trim() ?? "";
        if (notes.Length > 2000) return new(AccessOutcome.Invalid, null, "Notes are limited to 2,000 characters.");
        string? tenant = null;
        if (!string.IsNullOrWhiteSpace(c.HomeTenantId))
        {
            if (!Guid.TryParse(c.HomeTenantId.Trim(), out var tenantGuid))
                return new(AccessOutcome.Invalid, null, "The home tenant ID must be a GUID.");
            tenant = tenantGuid.ToString("D");
        }
        if (!Enum.IsDefined(c.Cloud) || !Enum.IsDefined(c.CrossTenantStatus))
            return new(AccessOutcome.Invalid, null, "That cloud or status isn't valid.");

        var status = c.Cloud == CustomerCloud.Commercial ? CrossTenantStatus.NotRequired
            : c.CrossTenantStatus == CrossTenantStatus.NotRequired ? CrossTenantStatus.Pending : c.CrossTenantStatus;
        var saved = c with
        {
            Id = c.Id == Guid.Empty ? Guid.NewGuid() : c.Id, Name = name, Notes = notes, HomeTenantId = tenant,
            CrossTenantStatus = status,
        };

        try { await _customers.UpsertAsync(saved, ct); }
        catch (InvalidOperationException ex) { return new(AccessOutcome.Invalid, null, ex.Message); }

        await RecordAsync(actor, SaveCustomerOp, saved.Id, null,
            $"{saved.Name} ({saved.Cloud}, {(saved.Active ? "active" : "inactive")})", ctx);
        return new(AccessOutcome.Ok, saved);
    }

    /// <summary>
    /// Exactly one '@' with text each side, no whitespace or control/format characters anywhere (they would make two
    /// accounts look identical, or a name look different from what is stored), and neither side starting with a
    /// character a spreadsheet reads as a formula.
    /// </summary>
    public static bool IsValidUpn(string name, out int at)
    {
        at = name.IndexOf('@');
        if (at < 1 || at == name.Length - 1 || name.Length > 320 || name.IndexOf('@', at + 1) >= 0) return false;
        foreach (var rune in name.EnumerateRunes())
        {
            // EnumerateRunes maps a lone surrogate to U+FFFD, so the replacement character is how one is caught.
            if (rune == Rune.ReplacementChar || Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse)
                return false;
        }
        return !(name[0] is '=' or '+' or '-' or '@') && !(name[at + 1] is '=' or '+' or '-' or '@')
            && name.AsSpan(at + 1).ToString().All(char.IsAscii);
    }

    public async Task<AccessResult<AppUser>> AddCustomerUserAsync(Guid actorId, Guid customerId, string upn, string displayName,
        AuditContext? ctx = null, CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.StaffAdmin, AddCustomerUserOp, upn ?? "", customerId, null, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, null, StorageAccessService.DeniedMessage);

        var customer = await _customers.GetAsync(customerId, ct);
        if (customer is null) return new(AccessOutcome.NotFound, null, "That customer no longer exists.");

        var name = (upn ?? "").Trim();
        if (!IsValidUpn(name, out var at))
            return new(AccessOutcome.Invalid, null, "Enter the user's UPN, like name@customer.example.");
        var normalized = UpnNormalizer.Normalize(name);
        if (_owners.IsOwner(name))
            return new(AccessOutcome.Invalid, null, OwnersFromConfigMessage);
        if (normalized.EndsWith("@" + UpnNormalizer.Normalize(_staffDomain), StringComparison.Ordinal))
            return new(AccessOutcome.Invalid, null, StaffUpnMessage);
        if (await _users.FindByUpnAsync(name, ct) is not null)
            return new(AccessOutcome.Invalid, null, "A user with that UPN already exists.");

        var shown = string.IsNullOrWhiteSpace(displayName) ? name[..at] : displayName.Trim();
        if (shown.Length > 200) return new(AccessOutcome.Invalid, null, "The display name is limited to 200 characters.");
        var user = new AppUser(Guid.NewGuid(), UserKind.Customer, customerId, name, shown, null, StaffRole.None, true,
            _time.GetUtcNow(), null);

        try { await _users.UpsertAsync(user, ct); }
        catch (InvalidOperationException ex) { return new(AccessOutcome.Invalid, null, ex.Message); }

        await RecordAsync(actor, AddCustomerUserOp, customerId, null, $"{name} → {customer.Name}", ctx);
        return new(AccessOutcome.Ok, user);
    }

    public async Task<AccessResult> SetUserActiveAsync(Guid actorId, Guid userId, bool active, AuditContext? ctx = null,
        CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.StaffAdmin, SetUserActiveOp, userId.ToString(), null, null, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, StorageAccessService.DeniedMessage);

        var user = await _users.GetAsync(userId, ct);
        if (user is null) return new(AccessOutcome.NotFound, "That user no longer exists.");

        if (user.Kind == UserKind.Staff)
        {
            if (_owners.EffectiveRole(actor) < StaffRole.Owner)
            {
                await RecordAsync(actor, SetUserActiveOp, null, null, user.Upn, ctx, AuditResult.Denied,
                    "Only an Owner can change a staff user's access.");
                return new(AccessOutcome.Denied, StorageAccessService.DeniedMessage);
            }
            if (user.Id == actor.Id) return new(AccessOutcome.Invalid, "You can't change your own access.");
            if (_owners.IsOwner(user.Upn)) return new(AccessOutcome.Invalid, OwnersFromConfigMessage);
        }

        try
        {
            if (!await _users.SetActiveAsync(user.Id, active, ct))
                return new(AccessOutcome.NotFound, "That user no longer exists.");
        }
        catch (InvalidOperationException ex) { return new(AccessOutcome.Invalid, ex.Message); }
        await RecordAsync(actor, SetUserActiveOp, user.CustomerId, null, $"{user.Upn} = {(active ? "active" : "inactive")}", ctx);
        return new(AccessOutcome.Ok);
    }

    public async Task<AccessResult<StorageTarget>> SaveTargetAsync(Guid actorId, StorageTarget t, AuditContext? ctx = null,
        CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.StaffAdmin, SaveTargetOp, t.DisplayName, t.CustomerId, t.Id, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, null, StorageAccessService.DeniedMessage);

        var display = t.DisplayName?.Trim() ?? "";
        var sub = Guid.TryParse(t.SubscriptionId?.Trim(), out var subGuid) ? subGuid.ToString("D") : "";
        var rg = t.ResourceGroup?.Trim() ?? "";
        var account = t.AccountName?.Trim() ?? "";
        var container = t.ContainerName?.Trim() ?? "";
        var suffix = string.IsNullOrWhiteSpace(t.EndpointSuffix) ? DefaultEndpointSuffix : t.EndpointSuffix.Trim().ToLowerInvariant();

        string? error =
            display.Length is 0 or > 100 ? "Display name is required (up to 100 characters)."
            : !Guid.TryParse(sub, out _) ? "Subscription ID must be a GUID."
            : !ResourceGroupRegex().IsMatch(rg) ? "Resource group is required (letters, digits, - _ . and parentheses)."
            : !AccountNameRegex().IsMatch(account) ? "Storage account name must be 3 to 24 lowercase letters and digits."
            : !ContainerNameRegex().IsMatch(container) ? "Share or container name must be 3 to 63 lowercase letters, digits and single hyphens, starting and ending with a letter or digit."
            : !Enum.IsDefined(t.Kind) ? "Choose file share or blob container."
            : !AllowedEndpointSuffixes.Contains(suffix) ? $"Endpoint suffix must be one of: {string.Join(", ", AllowedEndpointSuffixes)}."
            : null;
        if (error is not null) return new(AccessOutcome.Invalid, null, error);

        if (_allowedSubscriptions.Count > 0 && !_allowedSubscriptions.Contains(Guid.Parse(sub)))
            return new(AccessOutcome.Invalid, null, SubscriptionNotAllowedMessage);

        if (t.CustomerId is { } cid && await _customers.GetAsync(cid, ct) is null)
            return new(AccessOutcome.Invalid, null, "That customer no longer exists.");

        var existing = t.Id == Guid.Empty ? null : await _targets.GetAsync(t.Id, ct);
        // Internal storage is Owner-managed: creating it, moving a customer's target to it, and (for an existing internal
        // target) handing it to a customer or re-pointing it at other storage all need an Owner. A display name or
        // active flag edit does not.
        var locationChanged = existing is not null
            && (existing.AccountName != account || existing.ContainerName != container || existing.Kind != t.Kind
                || !string.Equals(existing.EndpointSuffix, suffix, StringComparison.OrdinalIgnoreCase));
        var internalChange = (t.CustomerId is null && (existing is null || existing.CustomerId is not null))
            || (existing is { CustomerId: null } && (t.CustomerId is not null || locationChanged));
        if (internalChange && _owners.EffectiveRole(actor) < StaffRole.Owner)
        {
            await RecordAsync(actor, SaveTargetOp, existing?.CustomerId, existing?.Id, display, ctx, AuditResult.Denied, InternalOwnerOnlyMessage);
            return new(AccessOutcome.Denied, null, InternalOwnerOnlyMessage);
        }
        if (existing?.CustomerId is { } oldOwner && oldOwner != t.CustomerId
            && (await _grants.ListForCustomerAsync(oldOwner, ct)).Any(g => g.TargetId == existing.Id))
            return new(AccessOutcome.Invalid, null, "Remove this target's grants before moving it to another customer.");

        if (existing?.CustomerId is { } owner
            && (existing.AccountName != account || existing.ContainerName != container || existing.Kind != t.Kind
                || !string.Equals(existing.EndpointSuffix, suffix, StringComparison.OrdinalIgnoreCase))
            && (await _grants.ListForCustomerAsync(owner, ct)).Any(g => g.TargetId == existing.Id))
            return new(AccessOutcome.Invalid, null, "Remove this target's grants before pointing it at different storage.");

        var saved = t with
        {
            Id = t.Id == Guid.Empty ? Guid.NewGuid() : t.Id,
            DisplayName = display, SubscriptionId = sub, ResourceGroup = rg, AccountName = account,
            ContainerName = container, EndpointSuffix = suffix,
        };

        try { await _targets.UpsertAsync(saved, ct); }
        catch (InvalidOperationException ex) { return new(AccessOutcome.Invalid, null, ex.Message); }

        await RecordAsync(actor, SaveTargetOp, saved.CustomerId, saved.Id,
            $"{saved.DisplayName} ({saved.AccountName}/{saved.ContainerName}, {(saved.Active ? "active" : "inactive")})", ctx);
        return new(AccessOutcome.Ok, saved);
    }

    public async Task<AccessResult<string>> TestTargetAsync(Guid actorId, Guid targetId, AuditContext? ctx = null,
        CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.StaffAdmin, TestTargetOp, targetId.ToString(), null, targetId, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, null, StorageAccessService.DeniedMessage);

        var target = await _targets.GetAsync(targetId, ct);
        if (target is null) return new(AccessOutcome.NotFound, null, "That target no longer exists.");

        if (!_cooldown.TryEnter(actor.Id, TestCooldown))
            return new(AccessOutcome.Busy, null, TestCooldownMessage);

        var noun = target.Kind == StorageKind.FileShare ? "file share" : "container";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_testTimeout);
        try
        {
            await _backends.For(target).ProbeAsync(target, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = StorageErrorMapper.Map(ex);
            var message = ex is OperationCanceledException && timeout.IsCancellationRequested
                ? TestTimeoutMessage
                : failure.Diagnostic ?? failure.Message;
            _logger?.LogWarning(failure.InnerException ?? failure,
                "Test access failed on target {TargetId}: {Diagnostic}", target.Id, message);
            await RecordAsync(actor, TestTargetOp, target.CustomerId, target.Id, target.DisplayName, ctx, AuditResult.Failed, message);
            return new(AccessOutcome.Unavailable, null, message);
        }

        await RecordAsync(actor, TestTargetOp, target.CustomerId, target.Id, target.DisplayName, ctx);
        return new(AccessOutcome.Ok, $"Reachable — the portal can list this {noun}.");
    }

    public async Task<AccessResult> SetGrantAsync(Guid actorId, Guid customerId, Guid targetId, AccessLevel level,
        AuditContext? ctx = null, CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.StaffAdmin, SetGrantOp, $"{customerId} → {targetId} = {level}",
            customerId, targetId, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, StorageAccessService.DeniedMessage);

        if (!Enum.IsDefined(level)) return new(AccessOutcome.Invalid, "That access level isn't valid.");
        var customer = await _customers.GetAsync(customerId, ct);
        if (customer is null) return new(AccessOutcome.NotFound, "That customer no longer exists.");
        var target = await _targets.GetAsync(targetId, ct);
        if (target is null) return new(AccessOutcome.NotFound, "That target no longer exists.");
        if (target.CustomerId != customerId)
            return new(AccessOutcome.Invalid, "That storage belongs to another customer, so it can't be granted here.");

        try
        {
            if (level == AccessLevel.None) await _grants.RemoveAsync(customerId, targetId, ct);
            else await _grants.SetAsync(new Grant(customerId, targetId, level, actor.Upn, _time.GetUtcNow()), ct);
        }
        catch (InvalidOperationException ex) { return new(AccessOutcome.Invalid, ex.Message); }

        await RecordAsync(actor, SetGrantOp, customerId, targetId, $"{customer.Name} → {target.DisplayName} = {level}", ctx);
        return new(AccessOutcome.Ok);
    }

    public async Task<AccessResult> SetStaffRoleAsync(Guid actorId, Guid staffUserId, StaffRole role, AuditContext? ctx = null,
        CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.Owner, SetStaffRoleOp, $"{staffUserId} = {role}", null, null, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, StorageAccessService.DeniedMessage);

        if (staffUserId == actor.Id) return new(AccessOutcome.Invalid, "You can't change your own role.");
        if (role is StaffRole.Owner) return new(AccessOutcome.Invalid, "Owner can't be assigned here. " + OwnersFromConfigMessage);
        if (!Enum.IsDefined(role)) return new(AccessOutcome.Invalid, "That role isn't valid.");

        var user = await _users.GetAsync(staffUserId, ct);
        if (user is not { Kind: UserKind.Staff }) return new(AccessOutcome.NotFound, "That staff user no longer exists.");
        if (_owners.IsOwner(user.Upn)) return new(AccessOutcome.Invalid, OwnersFromConfigMessage);

        try
        {
            if (!await _users.SetStaffRoleAsync(user.Id, role, ct))
                return new(AccessOutcome.NotFound, "That staff user no longer exists.");
        }
        catch (InvalidOperationException ex) { return new(AccessOutcome.Invalid, ex.Message); }
        await RecordAsync(actor, SetStaffRoleOp, null, null, $"{user.Upn} = {role}", ctx);
        return new(AccessOutcome.Ok);
    }

    public const string UploadLimitEndMessage = "The end time must be in the future.";
    public const string UploadLimitNoteMessage = "The note is limited to 500 characters.";
    public const long MinUploadLimitBytes = 1L << 30;
    private static readonly TimeSpan MinUploadLimitLead = TimeSpan.FromMinutes(1);

    public static string UploadLimitRangeMessage(long ceiling) =>
        $"The limit must be between 1 GB and {UploadLimitService.FormatGb(ceiling)} GB.";

    /// <summary>Replaces the upload limit override. <paramref name="expiresAt"/> null is permanent; otherwise more than a minute ahead.</summary>
    public async Task<AccessResult> SetUploadLimitAsync(Guid actorId, long bytes, DateTimeOffset? expiresAt, string? note,
        AuditContext? ctx = null, CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.StaffAdmin, SetUploadLimitOp, $"limit -> {bytes} bytes", null, null, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, StorageAccessService.DeniedMessage);

        // Read past the cache: another instance may have changed the limit, and the audit row names the value replaced.
        _limits.Invalidate();
        var before = await _limits.GetStateAsync(ct);
        if (bytes < MinUploadLimitBytes || bytes > before.Ceiling)
            return new(AccessOutcome.Invalid, UploadLimitRangeMessage(before.Ceiling));
        var now = _time.GetUtcNow();
        if (expiresAt is { } end && end <= now + MinUploadLimitLead)
            return new(AccessOutcome.Invalid, UploadLimitEndMessage);
        var text = note?.Trim() ?? "";
        if (text.Length > 500) return new(AccessOutcome.Invalid, UploadLimitNoteMessage);

        var until = expiresAt?.ToUniversalTime();
        await _limitStore.SetAsync(new UploadLimitOverride(bytes, until, actor.Id, actor.Upn, now, text), ct);
        _limits.Invalidate();

        var untilText = until is { } u ? u.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "permanent";
        await RecordAsync(actor, SetUploadLimitOp, null, null,
            $"limit {UploadLimitService.FormatGb(before.Effective)} GB -> {UploadLimitService.FormatGb(bytes)} GB; until {untilText}; note: {text}",
            ctx, bytes: bytes);
        return new(AccessOutcome.Ok);
    }

    /// <summary>Removes the override, so the configured default applies again.</summary>
    public async Task<AccessResult> ClearUploadLimitAsync(Guid actorId, AuditContext? ctx = null, CancellationToken ct = default)
    {
        var actor = await RequireAsync(actorId, StaffRole.StaffAdmin, SetUploadLimitOp, "limit -> default", null, null, ctx, ct);
        if (actor is null) return new(AccessOutcome.Denied, StorageAccessService.DeniedMessage);

        _limits.Invalidate();
        var before = await _limits.GetStateAsync(ct);
        await _limitStore.ClearAsync(ct);
        _limits.Invalidate();

        await RecordAsync(actor, SetUploadLimitOp, null, null,
            $"limit {UploadLimitService.FormatGb(before.Effective)} GB -> default {UploadLimitService.FormatGb(before.Default)} GB", ctx,
            bytes: before.Default);
        return new(AccessOutcome.Ok);
    }

    /// <summary>The acting user if still an active staff user holding at least <paramref name="minimum"/>; otherwise a Denied row is written and null returned.</summary>
    private async Task<AppUser?> RequireAsync(Guid actorId, StaffRole minimum, string operation, string path, Guid? customerId,
        Guid? targetId, AuditContext? ctx, CancellationToken ct)
    {
        var actor = await _users.GetAsync(actorId, ct);
        if (actor is { Kind: UserKind.Staff, Active: true } && _owners.EffectiveRole(actor) >= minimum)
            return actor;

        await _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), actorId, actor?.Upn ?? "", customerId, targetId,
            operation, path, null, AuditResult.Denied, StorageAccessService.DeniedMessage, SourceIp(ctx), Correlation(ctx)),
            CancellationToken.None);
        return null;
    }

    private Task RecordAsync(AppUser actor, string operation, Guid? customerId, Guid? targetId, string path, AuditContext? ctx,
        AuditResult result = AuditResult.Success, string error = "", long? bytes = null) =>
        _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), actor.Id, actor.Upn, customerId, targetId, operation,
            path, bytes, result, error, SourceIp(ctx), Correlation(ctx)), CancellationToken.None);

    public const string ExportAuditOp = "Admin.ExportAudit";

    /// <summary>Audits a CSV export by a user the endpoint has already authorised: path is the filter summary, bytes the row count.</summary>
    public Task RecordExportAsync(Guid actorId, string actorUpn, string filterSummary, int rows, AuditContext? ctx) =>
        _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), actorId, actorUpn, null, null, ExportAuditOp,
            filterSummary, rows, AuditResult.Success, "", SourceIp(ctx), Correlation(ctx)), CancellationToken.None);

    private static string SourceIp(AuditContext? ctx) => ctx?.SourceIp ?? "";

    private static string Correlation(AuditContext? ctx) => ctx?.CorrelationId ?? Guid.NewGuid().ToString("N");
}

/// <summary>An alert's text and CSS class for an <see cref="AccessResult"/>, shared by the admin pages.</summary>
public sealed record AdminAlert(string Text, string Css)
{
    public const string StoreDownMessage = "The service is temporarily unavailable.";

    public bool IsError => Css != "alert--success";

    public static AdminAlert Success(string text) => new(text, "alert--success");

    public static AdminAlert StoreDown() => new(StoreDownMessage, "alert--warning");

    public static AdminAlert For(AccessOutcome outcome, string? message, string success = "Saved.") => outcome switch
    {
        AccessOutcome.Ok => Success(string.IsNullOrEmpty(message) ? success : message),
        AccessOutcome.Unavailable or AccessOutcome.Busy or AccessOutcome.Conflict =>
            new(message ?? StoreDownMessage, "alert--warning"),
        _ => new(message ?? "That didn't work.", "alert--danger"),
    };
}

/// <summary>The audit filters shared by the audit page and the CSV export, so both ask the store the same question.</summary>
public static class AuditFilters
{
    public const int PageLimit = 500;
    public const int ExportLimit = 5000;

    public static bool TryParseDate(string? text, out DateTimeOffset? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return false;
        date = new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc));
        return true;
    }

    private static DateTimeOffset? EndOfDay(DateTimeOffset? day) =>
        day is not { } d ? null : d.UtcDateTime.Date == DateTime.MaxValue.Date ? DateTimeOffset.MaxValue : d.AddDays(1).AddTicks(-1);

    /// <summary>Null when a filter value is malformed. Dates are UTC days; <c>to</c> includes the whole day.</summary>
    public static AuditQuery? ToQuery(Guid? userId, Guid? customerId, Guid? targetId, string? operation, string? result,
        string? from, string? to, int limit)
    {
        AuditResult? parsedResult = null;
        if (!string.IsNullOrWhiteSpace(result))
        {
            if (!Enum.TryParse<AuditResult>(result, true, out var r) || !Enum.IsDefined(r)) return null;
            parsedResult = r;
        }
        if (!TryParseDate(from, out var f) || !TryParseDate(to, out var t)) return null;
        return new AuditQuery(userId, customerId, targetId, string.IsNullOrWhiteSpace(operation) ? null : operation.Trim(),
            parsedResult, f, EndOfDay(t), limit);
    }
}

/// <summary>CSV cell escaping for the audit export.</summary>
public static class CsvCell
{
    /// <summary>
    /// A formula starter (= + - @, tab, CR) at the start of the cell or after a ; , | or tab separator is prefixed with
    /// an apostrophe so it shows as text; then EVERY cell is RFC 4180 quoted, so no locale's separator can split it.
    /// </summary>
    public static string Escape(string? value)
    {
        var s = Neutralise(value ?? "");
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // A formula starter is dangerous at the start of a cell AND after a separator a spreadsheet may split on
    // (; for locales where it is the list separator, plus , | and tab), optionally after spaces.
    private static string Neutralise(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        var atStart = true;
        foreach (var ch in s)
        {
            if (atStart && ch is '=' or '+' or '-' or '@' or '\t' or '\r')
                sb.Append('\'');
            sb.Append(ch);
            if (ch is ';' or ',' or '|' or '\t')
                atStart = true;
            else if (ch != ' ')
                atStart = false;
        }
        return sb.ToString();
    }

    public static string Row(IEnumerable<string?> cells) => string.Join(',', cells.Select(Escape));

    public static string AuditCsv(IEnumerable<AuditEvent> events, IReadOnlyDictionary<Guid, string> customers,
        IReadOnlyDictionary<Guid, string> targets, int? truncatedAt = null)
    {
        var sb = new StringBuilder();
        sb.Append(Row(new[] { "Time (UTC)", "User", "Customer", "Target", "Operation", "Path", "Bytes", "Result", "Error", "Source IP", "Correlation ID" })).Append("\r\n");
        foreach (var e in events)
        {
            sb.Append(Row(new[]
            {
                e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                e.UserDisplay,
                e.CustomerId is { } c && customers.TryGetValue(c, out var cn) ? cn : "",
                e.TargetId is { } t && targets.TryGetValue(t, out var tn) ? tn : "",
                e.Operation, e.Path, e.Bytes?.ToString(CultureInfo.InvariantCulture) ?? "", e.Result.ToString(), e.Error,
                e.SourceIp, e.CorrelationId,
            })).Append("\r\n");
        }
        if (truncatedAt is { } n)
            sb.Append($"# truncated at {n} rows — narrow the filters to see more").Append("\r\n");
        return sb.ToString();
    }
}
