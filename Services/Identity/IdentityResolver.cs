using System.Security.Claims;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;

namespace CloudSelfService.Services.Identity;

public enum AdmissionOutcome { Admitted, AdmittedPendingRole, UnknownCustomer, Deactivated }

public sealed record Admission(AdmissionOutcome Outcome, AppUser? User);

public static class AppClaims
{
    public const string UserId = "urn:cssp:user_id";
    public const string ObjectId = "urn:cssp:oid";
    public const string IssuedAt = "urn:cssp:iat";
    public const string DisplayName = "name";

    /// <summary>
    /// The cookie principal. It carries identity only - never a role, a customer or a grant - so
    /// every authorization decision has to re-read the store and a revocation takes effect on the
    /// next request. The oid and issue time exist for <see cref="SessionValidator"/>: a record
    /// re-bound to another oid, or a session past its absolute lifetime, is refused even though
    /// sliding expiry would otherwise keep it alive.
    /// </summary>
    public static ClaimsPrincipal PrincipalFor(AppUser user, string authenticationType, DateTimeOffset issuedAt) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user.Upn),
            new Claim(DisplayName, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Upn : user.DisplayName),
            new Claim(UserId, user.Id.ToString()),
            new Claim(ObjectId, user.EntraObjectId ?? ""),
            new Claim(IssuedAt, issuedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
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
    private readonly OwnerConfig _owners;

    public IdentityResolver(IUserStore users, ICustomerStore customers, AuditService audit, IConfiguration config,
        OwnerConfig owners)
    {
        _users = users;
        _customers = customers;
        _audit = audit;
        _config = config;
        _owners = owners;
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
        if (user is null)
        {
            // Owner is never persisted: EffectiveRole derives it from configuration, so a sign-in writes no role at all.
            var created = new AppUser(Guid.NewGuid(), UserKind.Staff, null, id.Upn, id.DisplayName, id.ObjectId, StaffRole.None, true, now, now);
            if (await BindNewAsync(created, id, ct) is not { } fresh)
                return (Denied(AdmissionOutcome.UnknownCustomer, null), "object id bind lost a race");
            return (StaffAdmission(fresh), "");
        }

        var (upn, name) = await NamesAsync(user, id, ct);
        if (await SignInAsync(user, id, upn, name, now, ct) is not { } bound)
            return await LostRaceAsync(user.Id, ct);
        return (StaffAdmission(bound), "");
    }

    private Admission StaffAdmission(AppUser stored)
    {
        var role = _owners.EffectiveRole(stored);
        var outcome = role == StaffRole.None ? AdmissionOutcome.AdmittedPendingRole : AdmissionOutcome.Admitted;
        return new Admission(outcome, stored with { StaffRole = role });
    }

    private async Task<(Admission, string)> AdmitCustomerAsync(EntraIdentity id, CancellationToken ct)
    {
        var user = await _users.FindByObjectIdAsync(id.ObjectId, ct);
        if (user is null && id.Upn.Length > 0)
        {
            // Only a tenant-verified name may stand in for the oid; `email` is settable by the
            // guest's home tenant, so matching on it would let anyone claim a pre-registered record.
            if (id.UpnFromEmail)
                return (Denied(AdmissionOutcome.UnknownCustomer, null), "no verified UPN to bind on");

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

        if (user.EntraObjectId is null)
        {
            // The first bind is the moment a name becomes an identity, so it is where the home tenant is checked.
            if (customer.CrossTenantStatus == CrossTenantStatus.Pending)
                return (Denied(AdmissionOutcome.UnknownCustomer, null), "customer's cross-tenant setup is pending");
            if (!string.IsNullOrWhiteSpace(customer.HomeTenantId)
                && !(Guid.TryParse(customer.HomeTenantId, out var expected) && HomeTenantOf(id) == expected))
                return (Denied(AdmissionOutcome.UnknownCustomer, null), "Home tenant does not match");
        }

        var (upn, name) = await NamesAsync(user, id, ct);
        if (await SignInAsync(user, id, upn, name, DateTimeOffset.UtcNow, ct) is not { } bound)
            return await LostRaceAsync(user.Id, ct);

        return (new Admission(AdmissionOutcome.Admitted, bound), "");
    }

    /// <summary>
    /// The caller's home tenant: for a member, the token's <c>tid</c>; for a guest, the GUID in the <c>idp</c> claim (https://sts.windows.net/{tid}/ or
    /// https://login.microsoftonline.us/{tid}/v2.0). Null for a consumer idp or when the claim is absent.
    /// </summary>
    public static Guid? HomeTenantOf(EntraIdentity id)
    {
        if (!id.IsGuest) return Guid.TryParse(id.TenantId, out var memberTid) ? memberTid : null;
        if (string.IsNullOrWhiteSpace(id.Idp)) return null;
        if (!Uri.TryCreate(id.Idp.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Guid.TryParse(first, out var tid) ? tid : null;
    }

    /// <summary>
    /// The UPN and display name to store once the caller is known to own the record, so a rename in Entra reaches the
    /// record (and with it the Owners check, which is by UPN). An email-only identity never renames a record, and
    /// neither does a UPN another record holds.
    /// </summary>
    private async Task<(string Upn, string Name)> NamesAsync(AppUser user, EntraIdentity id, CancellationToken ct)
    {
        if (id.UpnFromEmail || string.IsNullOrWhiteSpace(id.Upn)) return (user.Upn, user.DisplayName);

        var upn = user.Upn;
        if (!string.Equals(upn, id.Upn, StringComparison.Ordinal))
        {
            var holder = await _users.FindByUpnAsync(id.Upn, ct);
            if (holder is null || holder.Id == user.Id) upn = id.Upn;
        }

        return (upn, string.IsNullOrWhiteSpace(id.DisplayName) ? user.DisplayName : id.DisplayName);
    }

    /// <summary>
    /// One atomic UPDATE that binds the oid only while the user is still active; it never writes Active or the role, so
    /// a sign-in racing an admin's deactivate or demote cannot undo it. Null when it matched no row.
    /// </summary>
    private async Task<AppUser?> SignInAsync(AppUser user, EntraIdentity id, string upn, string name, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            return await _users.RecordSignInAsync(user.Id, id.ObjectId, upn, name, now, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<(Admission, string)> LostRaceAsync(Guid userId, CancellationToken ct)
    {
        var current = await _users.GetAsync(userId, ct);
        return current is { Active: false }
            ? (Denied(AdmissionOutcome.Deactivated, current), "deactivated during sign-in")
            : (Denied(AdmissionOutcome.UnknownCustomer, null), "object id bind lost a race");
    }

    /// <summary>Inserts a first-time staff record and re-reads it, so only the caller whose oid is actually stored is admitted.</summary>
    private async Task<AppUser?> BindNewAsync(AppUser user, EntraIdentity id, CancellationToken ct)
    {
        try
        {
            await _users.UpsertAsync(user, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var stored = await _users.GetAsync(user.Id, ct);
        return stored is not null && string.Equals(stored.EntraObjectId, id.ObjectId, StringComparison.Ordinal)
            ? stored
            : null;
    }

    private static Admission Denied(AdmissionOutcome outcome, AppUser? user) => new(outcome, user);
}
