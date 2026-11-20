using CloudSelfService.Models;

namespace CloudSelfService.Services.Identity;

/// <summary>
/// The configured Owners. Owner is config-authoritative (as in ReleaseMetrics): this list decides
/// it, and a persisted Owner whose UPN has left it counts as StaffAdmin from the next check, not
/// from their next sign-in.
/// </summary>
public sealed class OwnerConfig
{
    private readonly HashSet<string> _owners;

    public OwnerConfig(string commaSeparatedOwners) =>
        _owners = new HashSet<string>(
            commaSeparatedOwners.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    public bool IsOwner(string upn) => !string.IsNullOrWhiteSpace(upn) && _owners.Contains(upn.Trim());

    public StaffRole EffectiveRole(AppUser user) =>
        user.StaffRole == StaffRole.Owner && !IsOwner(user.Upn) ? StaffRole.StaffAdmin : user.StaffRole;
}
