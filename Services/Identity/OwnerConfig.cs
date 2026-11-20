using CloudSelfService.Models;

namespace CloudSelfService.Services.Identity;

/// <summary>
/// Owner is config-authoritative (as in ReleaseMetrics): <c>Authorization:Owners</c> decides it,
/// and a persisted Owner whose UPN has left that list counts as StaffAdmin from the next check,
/// not from their next sign-in.
/// </summary>
public static class OwnerConfig
{
    public static bool IsOwner(string upn, IConfiguration config) =>
        !string.IsNullOrWhiteSpace(upn)
        && (config["Authorization:Owners"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(upn.Trim(), StringComparer.OrdinalIgnoreCase);

    public static StaffRole EffectiveRole(AppUser user, IConfiguration config) =>
        user.StaffRole == StaffRole.Owner && !IsOwner(user.Upn, config) ? StaffRole.StaffAdmin : user.StaffRole;
}
