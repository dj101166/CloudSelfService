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

    // Every folder MapStaticAssets serves from (wwwroot plus the scoped-CSS and collocated-JS outputs).
    private static readonly string[] StaticPrefixes =
        { "/_framework", "/_content", "/css", "/js", "/images", "/fonts", "/lib", "/Shared" };

    // The root-level assets, named, because a page path can end in a dotted segment too (a folder called "2024.01").
    private static readonly Regex RootAsset = RootAssetPattern();

    // Customer data or API responses; never cacheable, whatever the last segment looks like.
    private static readonly string[] AlwaysNoStorePrefixes = { "/files", "/api" };

    /// <summary>
    /// Rule: no-store for everything under /files and /api, and for every other path except the known static assets
    /// (the static folders and the named root-level files). Pages, endpoints and the circuit's /_blazor are all no-store.
    /// </summary>
    public static bool ShouldDisableCaching(PathString path)
    {
        var value = path.Value ?? "/";
        if (AlwaysNoStorePrefixes.Any(p => UnderPrefix(value, p)))
            return true;
        if (StaticPrefixes.Any(p => UnderPrefix(value, p)))
            return false;
        return !RootAsset.IsMatch(value);
    }

    private static bool UnderPrefix(string value, string prefix) =>
        value.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) || value.Equals(prefix, StringComparison.OrdinalIgnoreCase);

    // CloudSelfService.styles.css and favicon.png, each also with the fingerprinted name MapStaticAssets serves
    // (e.g. CloudSelfService.k8bafzscy8.styles.css), plus the modules manifest and favicon.ico for browsers that ask.
    [GeneratedRegex(@"^/(CloudSelfService(\.[a-z0-9]+)?\.(styles\.css|modules\.json)|favicon(\.[a-z0-9]+)?\.(png|ico))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RootAssetPattern();

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadstsCode();
}
