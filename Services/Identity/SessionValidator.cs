using System.Globalization;
using System.Security.Claims;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;

namespace CloudSelfService.Services.Identity;

public sealed record SessionLifetime(TimeSpan AbsoluteLifetime)
{
    public static SessionLifetime FromConfiguration(IConfiguration config) =>
        new(TimeSpan.FromHours(config.GetValue<double?>("Auth:AbsoluteSessionHours") ?? 12));
}

public sealed record SessionValidation(bool Valid, string Reason, AppUser? User)
{
    public static SessionValidation Reject(string reason) => new(false, reason, null);
}

/// <summary>
/// Decides whether a signed-in principal still stands. Run on every cookie-authenticated request
/// and by <see cref="CurrentUser"/> inside a circuit, so a deactivation, a deleted record or a
/// re-bound oid ends the session on the next request rather than at expiry.
/// </summary>
public static class SessionValidator
{
    public static async Task<SessionValidation> ValidateAsync(ClaimsPrincipal? principal, IUserStore users,
        ICustomerStore customers, DateTimeOffset now, SessionLifetime options, CancellationToken ct = default)
    {
        if (AppClaims.UserIdOf(principal) is not { } userId)
            return SessionValidation.Reject("no user id claim");

        if (!long.TryParse(principal!.FindFirst(AppClaims.IssuedAt)?.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var issuedSeconds))
            return SessionValidation.Reject("no issue time claim");

        if (now - DateTimeOffset.FromUnixTimeSeconds(issuedSeconds) > options.AbsoluteLifetime)
            return SessionValidation.Reject("absolute session lifetime exceeded");

        var user = await users.GetAsync(userId, ct);
        if (user is null)
            return SessionValidation.Reject("user record not found");

        if (!user.Active)
            return SessionValidation.Reject("user is inactive");

        var oid = principal.FindFirst(AppClaims.ObjectId)?.Value;
        if (string.IsNullOrEmpty(oid) || !string.Equals(oid, user.EntraObjectId, StringComparison.Ordinal))
            return SessionValidation.Reject("object id does not match the user record");

        if (user.Kind == UserKind.Customer)
        {
            var customer = user.CustomerId is { } customerId ? await customers.GetAsync(customerId, ct) : null;
            if (customer is not { Active: true })
                return SessionValidation.Reject("customer is missing or inactive");
        }

        return new SessionValidation(true, "", user);
    }
}
