using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Authorization;

namespace CloudSelfService.Authorization;

public static class Policies
{
    public const string Staff = "Staff";
    public const string StaffAdmin = "StaffAdmin";
    public const string Owner = "Owner";
}

public sealed class AppRoleRequirement : IAuthorizationRequirement
{
    public StaffRole MinimumRole { get; }
    public AppRoleRequirement(StaffRole minimumRole) => MinimumRole = minimumRole;
}

public sealed class AppRoleHandler : AuthorizationHandler<AppRoleRequirement>
{
    private readonly IUserStore _users;
    public AppRoleHandler(IUserStore users) => _users = users;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AppRoleRequirement requirement)
    {
        // MUST stay above the first await. A synchronous return here is what keeps the anonymous
        // path through the router's NotAuthorized fragment on the renderer's first non-streaming
        // pass, where a navigation becomes a real 302. Move an await above this line and that
        // fragment renders on an async completion instead, where the navigation recurses and
        // kills the host process. Pinned by RoleHandlerSynchronyTests.
        if (AppClaims.UserIdOf(context.User) is not { } userId) return;

        var user = await _users.GetAsync(userId);
        if (user is { Active: true, Kind: UserKind.Staff } && user.StaffRole >= requirement.MinimumRole)
            context.Succeed(requirement);
    }
}
