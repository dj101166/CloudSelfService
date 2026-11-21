using CloudSelfService.Models;

namespace CloudSelfService.Services.Identity;

/// <summary>
/// The one rule for what a signed-in user's staff role is right now. The stored role is never enough on its own:
/// Owner is derived from configuration and never persisted, so a configured Owner's record holds None.
/// </summary>
public static class StaffAccess
{
    /// <summary>The effective staff role; None for a customer whatever its record's role field says.</summary>
    public static StaffRole Effective(AppUser user, OwnerConfig owners) =>
        user.Kind == UserKind.Staff ? owners.EffectiveRole(user) : StaffRole.None;

    /// <summary>A staff user with no effective role yet, who sees the pending panel instead of any page.</summary>
    public static bool IsPending(AppUser user, OwnerConfig owners) =>
        user.Kind == UserKind.Staff && Effective(user, owners) == StaffRole.None;

    /// <summary>Staff holding at least <paramref name="minimum"/>.</summary>
    public static bool Reaches(AppUser user, OwnerConfig owners, StaffRole minimum) =>
        user.Kind == UserKind.Staff && Effective(user, owners) >= minimum;
}
