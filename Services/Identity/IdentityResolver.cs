using System.Security.Claims;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;

namespace CloudSelfService.Services.Identity;

public enum AdmissionOutcome { Admitted, AdmittedPendingRole, UnknownCustomer, Deactivated }

public sealed record Admission(AdmissionOutcome Outcome, AppUser? User);

public static class AppClaims
{
    public const string UserId = "urn:cssp:user_id";
    public const string DisplayName = "name";

    /// <summary>
    /// The cookie principal. It carries identity only - never a role, a customer or a grant - so
    /// every authorization decision has to re-read the store and a revocation takes effect on the
    /// next request.
    /// </summary>
    public static ClaimsPrincipal PrincipalFor(AppUser user, string authenticationType) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user.Upn),
            new Claim(DisplayName, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Upn : user.DisplayName),
            new Claim(UserId, user.Id.ToString()),
        }, authenticationType, ClaimTypes.Name, ClaimTypes.Role));

    public static Guid? UserIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst(UserId)?.Value, out var id) ? id : null;
}

/// <summary>
/// The one place an authenticated Entra principal becomes (or fails to become) an app user. A
/// second identity provider later feeds this same method rather than touching access control.
/// </summary>
public sealed class IdentityResolver
{
    private const string SignInOperation = "SignIn";

    private readonly IUserStore _users;
    private readonly ICustomerStore _customers;
    private readonly AuditService _audit;
    private readonly IConfiguration _config;

    public IdentityResolver(IUserStore users, ICustomerStore customers, AuditService audit, IConfiguration config)
    {
        _users = users;
        _customers = customers;
        _audit = audit;
        _config = config;
    }

    public Task<Admission> AdmitAsync(EntraIdentity id, CancellationToken ct = default) =>
        AdmitAsync(id, null, ct);

    public async Task<Admission> AdmitAsync(EntraIdentity id, AuditContext? context, CancellationToken ct = default)
    {
        var staffDomain = _config["Entra:StaffEmailDomain"] ?? "advancedutility.com";
        var (admission, reason) = EntraIdentityReader.IsStaff(id, staffDomain)
            ? await AdmitStaffAsync(id, ct)
            : await AdmitCustomerAsync(id, ct);

        var denied = admission.Outcome is AdmissionOutcome.UnknownCustomer or AdmissionOutcome.Deactivated;
        await _audit.RecordAsync(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, admission.User?.Id, id.Upn, admission.User?.CustomerId, null,
            SignInOperation, "", null, denied ? AuditResult.Denied : AuditResult.Success,
            denied ? $"{admission.Outcome}: {reason} (oid {id.ObjectId}, tenant {id.TenantId}, guest {id.IsGuest})" : "",
            context?.SourceIp ?? "", context?.CorrelationId ?? ""), ct);

        return admission;
    }

    private async Task<(Admission, string)> AdmitStaffAsync(EntraIdentity id, CancellationToken ct)
    {
        var user = await _users.FindByObjectIdAsync(id.ObjectId, ct);
        if (user is null && id.Upn.Length > 0)
        {
            user = await _users.FindByUpnAsync(id.Upn, ct);
            // Fails closed: a different object id on the same UPN is a deleted-and-recreated
            // account, and it must not inherit the old record's role without an admin deciding so.
            if (user?.EntraObjectId is not null)
                return (Denied(AdmissionOutcome.UnknownCustomer, null), "UPN is bound to a different object id");
        }

        if (user is not null && user.Kind != UserKind.Staff)
            return (Denied(AdmissionOutcome.UnknownCustomer, null), "identity matches a customer record");

        if (user is { Active: false })
            return (Denied(AdmissionOutcome.Deactivated, user), "staff record is inactive");

        var now = DateTimeOffset.UtcNow;
        var role = IsConfiguredOwner(id.Upn) ? StaffRole.Owner : user?.StaffRole ?? StaffRole.None;
        user = user is null
            ? new AppUser(Guid.NewGuid(), UserKind.Staff, null, id.Upn, id.DisplayName, id.ObjectId, role, true, now, now)
            : user with { EntraObjectId = id.ObjectId, StaffRole = role, LastSignInAt = now };
        await _users.UpsertAsync(user, ct);

        var outcome = role == StaffRole.None ? AdmissionOutcome.AdmittedPendingRole : AdmissionOutcome.Admitted;
        return (new Admission(outcome, user), "");
    }

    private async Task<(Admission, string)> AdmitCustomerAsync(EntraIdentity id, CancellationToken ct)
    {
        var user = await _users.FindByObjectIdAsync(id.ObjectId, ct);
        if (user is null && id.Upn.Length > 0)
        {
            user = await _users.FindByUpnAsync(id.Upn, ct);
            // A UPN match only stands in for an oid on the record's first sign-in. Once bound,
            // the oid is authoritative, so a reused or re-issued email address cannot take over.
            if (user?.EntraObjectId is not null)
                return (Denied(AdmissionOutcome.UnknownCustomer, null), "UPN is bound to a different object id");
        }

        if (user is null)
            return (Denied(AdmissionOutcome.UnknownCustomer, null), "no customer user record");

        if (user.Kind != UserKind.Customer || user.CustomerId is not { } customerId)
            return (Denied(AdmissionOutcome.UnknownCustomer, null), "identity matches a non-customer record");

        var customer = await _customers.GetAsync(customerId, ct);
        if (customer is null)
            return (Denied(AdmissionOutcome.UnknownCustomer, null), "customer record missing");

        if (!user.Active || !customer.Active)
            return (Denied(AdmissionOutcome.Deactivated, user), user.Active ? "customer is inactive" : "user is inactive");

        user = user with { EntraObjectId = id.ObjectId, LastSignInAt = DateTimeOffset.UtcNow };
        await _users.UpsertAsync(user, ct);
        return (new Admission(AdmissionOutcome.Admitted, user), "");
    }

    private bool IsConfiguredOwner(string upn) =>
        upn.Length > 0
        && (_config["Authorization:Owners"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(upn, StringComparer.OrdinalIgnoreCase);

    private static Admission Denied(AdmissionOutcome outcome, AppUser? user) => new(outcome, user);
}
