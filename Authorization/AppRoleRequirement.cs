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
    private readonly ICustomerStore _customers;
    private readonly OwnerConfig _owners;
    private readonly TimeProvider _time;
    private readonly SessionLifetime _lifetime;

    public AppRoleHandler(IUserStore users, ICustomerStore customers, OwnerConfig owners, TimeProvider time,
        SessionLifetime lifetime)
    {
        _users = users;
        _customers = customers;
        _owners = owners;
        _time = time;
        _lifetime = lifetime;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AppRoleRequirement requirement)
    {
        // MUST stay above the first await. A synchronous return here is what keeps the anonymous
        // path through the router's NotAuthorized fragment on the renderer's first non-streaming
        // pass, where a navigation becomes a real 302. Move an await above this line and that
        // fragment renders on an async completion instead, where the navigation recurses and
        // kills the host process. Pinned by RoleHandlerSynchronyTests.
        if (AppClaims.UserIdOf(context.User) is null) return;

        var session = await SessionValidator.ValidateAsync(context.User, _users, _customers, _time.GetUtcNow(), _lifetime);
        if (session is { Valid: true, User: { Kind: UserKind.Staff } user }
            && _owners.EffectiveRole(user) >= requirement.MinimumRole)
            context.Succeed(requirement);
    }
}
