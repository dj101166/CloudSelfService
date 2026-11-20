using System.Security.Claims;

namespace CloudSelfService.Services.Identity;

public sealed record EntraIdentity(string ObjectId, string TenantId, string Upn, string DisplayName, bool IsGuest)
{
    /// <summary>
    /// True when <see cref="Upn"/> came only from the <c>email</c> claim. That claim is the mail
    /// attribute, which a guest's home tenant can set freely, so it may label a sign-in but must
    /// never be what binds this oid to a pre-registered record or marks the caller as staff.
    /// </summary>
    public bool UpnFromEmail { get; init; }
}

public static class EntraIdentityReader
{
    /// <summary>
    /// Reads the token's claims as issued (the OIDC handler runs with MapInboundClaims = false,
    /// so these are the short JWT names). Null when there is no <c>oid</c>: without it there is
    /// nothing stable to bind a user record to.
    /// </summary>
    public static EntraIdentity? Read(ClaimsPrincipal p)
    {
        var oidClaim = p.FindFirst("oid");
        if (oidClaim is null || string.IsNullOrWhiteSpace(oidClaim.Value)) return null;

        var verifiedUpn = FirstNonBlank(p, "preferred_username", "upn");
        var upn = FirstNonBlank(p, "preferred_username", "email", "upn") ?? "";
        var name = FirstNonBlank(p, "name") ?? upn;
        var iss = FirstNonBlank(p, "iss") ?? oidClaim.Issuer;
        var idp = FirstNonBlank(p, "idp");

        // `acct` is an optional claim the app registration must request; `idp` is the fallback
        // that needs no configuration - Entra stamps it with the home tenant (or live.com) for
        // a B2B guest, and either omits it or sets it to the issuer for a member.
        var isGuest = p.FindFirst("acct")?.Value == "1"
                      || (idp is not null && !string.Equals(idp, iss, StringComparison.Ordinal));

        return new EntraIdentity(oidClaim.Value, FirstNonBlank(p, "tid") ?? "", upn, name, isGuest)
        {
            UpnFromEmail = verifiedUpn is null && upn.Length > 0,
        };
    }

    public static bool IsStaff(EntraIdentity id, string staffDomain) =>
        !id.IsGuest
        && !id.UpnFromEmail
        && !string.IsNullOrWhiteSpace(staffDomain)
        && id.Upn.EndsWith("@" + staffDomain, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonBlank(ClaimsPrincipal p, params string[] types)
    {
        foreach (var type in types)
        {
            var value = p.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }
}
