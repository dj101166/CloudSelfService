using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace CloudSelfService.Endpoints;

/// <summary>Pure decisions behind the auth and header wiring in Program.cs, kept here to be testable.</summary>
public static partial class AuthPolicy
{
    public sealed record CookieSettings(string Name, CookieSecurePolicy SecurePolicy);

    /// <summary>
    /// <c>__Host-</c> makes the browser refuse the cookie unless it is Secure, host-only and
    /// path "/", so it cannot be planted from a sibling subdomain. Development runs on plain http,
    /// where a <c>__Host-</c> cookie would be dropped, so it keeps SameAsRequest and a plain name.
    /// </summary>
    public static CookieSettings CookieFor(bool isDevelopment) => isDevelopment
        ? new("cssp-dev", CookieSecurePolicy.SameAsRequest)
        : new("__Host-cssp", CookieSecurePolicy.Always);

    public static Guid ParseTenantId(string? configured) =>
        Guid.TryParse(configured, out var id) && id != Guid.Empty
            ? id
            : throw new InvalidOperationException(
                "Entra:TenantId must be the directory (tenant) GUID when Auth:Provider is Entra.");

    public static bool IsOurTenant(string? tokenTid, Guid configured) =>
        Guid.TryParse(tokenTid, out var tid) && tid == configured;

    /// <summary>
    /// What a remote sign-in failure may put in the log: its AADSTS codes, or failing those the
    /// exception type. The message itself can echo protocol parameters, so it is never logged.
    /// </summary>
    public static string FailureSummary(Exception? failure)
    {
        if (failure is null) return "unknown";
        var codes = AadstsCode().Matches(failure.ToString()).Select(m => m.Value).Distinct().ToList();
        return codes.Count > 0 ? string.Join(",", codes) : failure.GetType().Name;
    }

    private static readonly string[] StaticPrefixes = { "/_framework", "/_content", "/css", "/js", "/images", "/lib" };

    /// <summary>
    /// Rule: no-store unless the path is under a static-asset folder or its last segment has a file
    /// extension. Every page, endpoint and the circuit's /_blazor traffic is extensionless.
    /// </summary>
    public static bool ShouldDisableCaching(PathString path)
    {
        var value = path.Value ?? "/";
        if (StaticPrefixes.Any(p => value.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)
                                   || value.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return false;
        var last = value[(value.LastIndexOf('/') + 1)..];
        return !last.Contains('.');
    }

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadstsCode();
}
