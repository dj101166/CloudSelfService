using System.Security.Claims;
using CloudSelfService.Authorization;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudSelfService.Tests;

public class AppRoleHandlerTests
{
    private readonly InMemoryUserStore _users = new();

    internal static IConfiguration Owners(string owners = "") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authorization:Owners"] = owners,
        }).Build();

    internal static AuthorizationHandlerContext ContextFor(ClaimsPrincipal principal, StaffRole minimum) =>
        new(new[] { new AppRoleRequirement(minimum) }, principal, resource: null);

    internal static ClaimsPrincipal SignedInAs(Guid userId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "someone@example.com"),
            new Claim(AppClaims.UserId, userId.ToString()),
        }, "test"));

    private async Task<AppUser> Add(UserKind kind, StaffRole role, bool active = true, string? upn = null)
    {
        var u = new AppUser(Guid.NewGuid(), kind, kind == UserKind.Customer ? Guid.NewGuid() : null,
            upn ?? $"{Guid.NewGuid():N}@example.com", "Name", null, role, active, DateTimeOffset.UtcNow, null);
        await _users.UpsertAsync(u);
        return u;
    }

    private async Task<bool> Evaluate(ClaimsPrincipal principal, StaffRole minimum, string owners = "")
    {
        var context = ContextFor(principal, minimum);
        await new AppRoleHandler(_users, Owners(owners)).HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task Handle_StaffRoleNone_FailsStaffPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.None);

        Assert.False(await Evaluate(SignedInAs(user.Id), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_CustomerUser_FailsStaffPolicy()
    {
        // A customer record cannot carry a staff role through any admin path, but the handler
        // must not trust that: it checks the kind as well as the role.
        var user = await Add(UserKind.Customer, StaffRole.Owner);

        Assert.False(await Evaluate(SignedInAs(user.Id), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_StaffAdmin_PassesStaffPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.StaffAdmin);

        Assert.True(await Evaluate(SignedInAs(user.Id), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_StaffAdmin_FailsOwnerPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.StaffAdmin);

        Assert.False(await Evaluate(SignedInAs(user.Id), StaffRole.Owner));
    }

    [Fact]
    public async Task Handle_DeactivatedStaff_Fails()
    {
        var user = await Add(UserKind.Staff, StaffRole.Owner, active: false);

        Assert.False(await Evaluate(SignedInAs(user.Id), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_PersistedOwnerNotInConfig_FailsOwnerPolicy_PassesStaffAdmin()
    {
        var user = await Add(UserKind.Staff, StaffRole.Owner, upn: "boss@advancedutility.com");

        Assert.False(await Evaluate(SignedInAs(user.Id), StaffRole.Owner, owners: "other@advancedutility.com"));
        Assert.True(await Evaluate(SignedInAs(user.Id), StaffRole.StaffAdmin, owners: "other@advancedutility.com"));
    }

    [Fact]
    public async Task Handle_PersistedOwnerInConfig_PassesOwnerPolicy()
    {
        var user = await Add(UserKind.Staff, StaffRole.Owner, upn: "boss@advancedutility.com");

        Assert.True(await Evaluate(SignedInAs(user.Id), StaffRole.Owner, owners: "BOSS@advancedutility.com"));
    }

    [Fact]
    public async Task Handle_UnknownUserId_Fails()
    {
        Assert.False(await Evaluate(SignedInAs(Guid.NewGuid()), StaffRole.Staff));
    }

    [Fact]
    public async Task Handle_MalformedUserIdClaim_Fails()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AppClaims.UserId, "not-a-guid") }, "test"));

        Assert.False(await Evaluate(principal, StaffRole.Staff));
    }
}
