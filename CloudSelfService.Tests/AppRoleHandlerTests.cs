using System.Security.Claims;
using CloudSelfService.Authorization;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace CloudSelfService.Tests;

public class AppRoleHandlerTests
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryCustomerStore _customers = new();

    internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal static AppRoleHandler HandlerFor(IUserStore users, ICustomerStore customers, string owners = "") =>
        new(users, customers, new OwnerConfig(owners), new FixedTime(Now), new SessionLifetime(TimeSpan.FromHours(12)));

    internal static AuthorizationHandlerContext ContextFor(ClaimsPrincipal principal, StaffRole minimum) =>
        new(new[] { new AppRoleRequirement(minimum) }, principal, resource: null);

    internal static ClaimsPrincipal SignedInAs(AppUser user, DateTimeOffset? issuedAt = null) =>
        AppClaims.PrincipalFor(user, "test", issuedAt ?? Now);

    private async Task<AppUser> Add(UserKind kind, StaffRole role, bool active = true, string? upn = null)
    {
        var u = new AppUser(Guid.NewGuid(), kind, kind == UserKind.Customer ? Guid.NewGuid() : null,
            upn ?? $"{Guid.NewGuid():N}@example.com", "Name", $"oid-{Guid.NewGuid():N}", role, active, Now, null);
        await _users.UpsertAsync(u);
        return u;
    }

    private async Task<bool> Evaluate(ClaimsPrincipal principal, StaffRole minimum, string owners = "")
    {
        var context = ContextFor(principal, minimum);
        await HandlerFor(_users, _customers, owners).HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task Handle_StaffRoleNone_FailsStaffPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.None);

        Assert.False(await Evaluate(SignedInAs(user), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_CustomerUser_FailsStaffPolicy()
    {
        // A customer record cannot carry a staff role through any admin path, but the handler
        // must not trust that: it checks the kind as well as the role.
        var user = await Add(UserKind.Customer, StaffRole.Owner);

        Assert.False(await Evaluate(SignedInAs(user), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_StaffAdmin_PassesStaffPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.StaffAdmin);

        Assert.True(await Evaluate(SignedInAs(user), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_StaffAdmin_FailsOwnerPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.StaffAdmin);

        Assert.False(await Evaluate(SignedInAs(user), StaffRole.Owner));
    }

    [Fact]
    public async Task Handle_DeactivatedStaff_Fails()
    {
        var user = await Add(UserKind.Staff, StaffRole.Owner, active: false);

        Assert.False(await Evaluate(SignedInAs(user), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_PersistedOwnerNotInConfig_FailsOwnerPolicy_PassesStaffAdmin()
    {
        var user = await Add(UserKind.Staff, StaffRole.Owner, upn: "boss@advancedutility.com");

        Assert.False(await Evaluate(SignedInAs(user), StaffRole.Owner, owners: "other@advancedutility.com"));
        Assert.True(await Evaluate(SignedInAs(user), StaffRole.StaffAdmin, owners: "other@advancedutility.com"));
    }

    [Fact]
    public async Task Handle_PersistedOwnerInConfig_PassesOwnerPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.Owner, upn: "boss@advancedutility.com");

        Assert.True(await Evaluate(SignedInAs(user), StaffRole.Owner, owners: "BOSS@advancedutility.com"));
    }

    [Fact]
    public async Task Handle_UnknownUserId_Fails()
    {
        var ghost = new AppUser(Guid.NewGuid(), UserKind.Staff, null, "ghost@example.com", "Ghost", "oid-ghost",
            StaffRole.Owner, true, Now, null);
        Assert.False(await Evaluate(SignedInAs(ghost), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_OidMismatchedPrincipal_FailsStaffPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.Owner);
        var principal = SignedInAs(user);
        await _users.UpsertAsync(user with { EntraObjectId = "oid-rebound" });

        Assert.False(await Evaluate(principal, StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_ExpiredPrincipal_FailsStaffPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.Owner);

        Assert.False(await Evaluate(SignedInAs(user, Now - TimeSpan.FromHours(13)), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_MalformedUserIdClaim_Fails()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AppClaims.UserId, "not-a-guid") }, "test"));

        Assert.False(await Evaluate(principal, StaffRole.Staff));
    }
}
