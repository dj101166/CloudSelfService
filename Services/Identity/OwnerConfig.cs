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
    private readonly string[] _display;

    public OwnerConfig(string commaSeparatedOwners)
    {
        _display = commaSeparatedOwners.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _owners = new HashSet<string>(_display.Select(UpnNormalizer.Normalize), StringComparer.Ordinal);
    }

    /// <summary>A copy, so a caller cannot change who is an Owner.</summary>
    public IReadOnlyCollection<string> Upns => _display.ToArray();

    public bool IsOwner(string upn) => !string.IsNullOrWhiteSpace(upn) && _owners.Contains(UpnNormalizer.Normalize(upn.Trim()));

    /// <summary>
    /// Owner is derived here, never stored: a configured Owner's staff record is Owner whatever role it holds, and a
    /// persisted Owner who has left the configuration counts as StaffAdmin. That keeps a sign-in from ever writing a role.
    /// </summary>
    public StaffRole EffectiveRole(AppUser user) =>
        user.Kind == UserKind.Staff && IsOwner(user.Upn) ? StaffRole.Owner
        : user.StaffRole == StaffRole.Owner ? StaffRole.StaffAdmin
        : user.StaffRole;
}
