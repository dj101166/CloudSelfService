using System.Security.Claims;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudSelfService.Tests;

public class SessionValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly SessionLifetime Options = new(TimeSpan.FromHours(12));

    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryCustomerStore _customers = new();

    private async Task<AppUser> AddStaff(bool active = true, string oid = "oid-1")
    {
        var u = new AppUser(Guid.NewGuid(), UserKind.Staff, null, "sam@advancedutility.com", "Sam", oid,
            StaffRole.Staff, active, Now, Now);
        await _users.UpsertAsync(u);
        return u;
    }

    private async Task<AppUser> AddCustomerUser(bool customerActive)
    {
        var c = new Customer(Guid.NewGuid(), "Contoso", customerActive, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
        await _customers.UpsertAsync(c);
        var u = new AppUser(Guid.NewGuid(), UserKind.Customer, c.Id, "pat@customer.example", "Pat", "oid-pat",
            StaffRole.None, true, Now, Now);
        await _users.UpsertAsync(u);
        return u;
    }

    private Task<SessionValidation> Validate(ClaimsPrincipal principal, DateTimeOffset? at = null) =>
        SessionValidator.ValidateAsync(principal, _users, _customers, at ?? Now, Options);

    [Fact]
    public async Task Validate_ActiveUser_Accepted()
    {
        var user = await AddStaff();

        var result = await Validate(AppClaims.PrincipalFor(user, "Cookies", Now.AddHours(-1)));

        Assert.True(result.Valid);
        Assert.Equal(user.Id, result.User!.Id);
    }

    [Fact]
    public async Task Validate_DeactivatedUser_Rejected()
    {
        var user = await AddStaff();
        var principal = AppClaims.PrincipalFor(user, "Cookies", Now);
        await _users.UpsertAsync(user with { Active = false });

        Assert.False((await Validate(principal)).Valid);
    }

    [Fact]
    public async Task Validate_DeletedUser_Rejected()
    {
        var ghost = new AppUser(Guid.NewGuid(), UserKind.Staff, null, "gone@advancedutility.com", "Gone", "oid-gone",
            StaffRole.Owner, true, Now, Now);

        Assert.False((await Validate(AppClaims.PrincipalFor(ghost, "Cookies", Now))).Valid);
    }

    [Fact]
    public async Task Validate_InactiveCustomer_Rejected()
    {
        var user = await AddCustomerUser(customerActive: false);

        Assert.False((await Validate(AppClaims.PrincipalFor(user, "Cookies", Now))).Valid);
    }

    [Fact]
    public async Task Validate_ActiveCustomer_Accepted()
    {
        var user = await AddCustomerUser(customerActive: true);

        Assert.True((await Validate(AppClaims.PrincipalFor(user, "Cookies", Now))).Valid);
    }

    [Fact]
    public async Task Validate_OidMismatch_Rejected()
    {
        var user = await AddStaff(oid: "oid-original");
        var principal = AppClaims.PrincipalFor(user, "Cookies", Now);
        await _users.UpsertAsync(user with { EntraObjectId = "oid-rebound" });

        Assert.False((await Validate(principal)).Valid);
    }

    [Fact]
    public async Task Validate_OlderThanAbsoluteLifetime_Rejected()
    {
        var user = await AddStaff();

        Assert.False((await Validate(AppClaims.PrincipalFor(user, "Cookies", Now - TimeSpan.FromHours(12) - TimeSpan.FromSeconds(1)))).Valid);
        Assert.True((await Validate(AppClaims.PrincipalFor(user, "Cookies", Now - TimeSpan.FromHours(11)))).Valid);
    }

    [Fact]
    public async Task Validate_CookieWithoutIssueTimeOrOid_Rejected()
    {
        var user = await AddStaff();
        var bare = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AppClaims.UserId, user.Id.ToString()) }, "Cookies"));

        Assert.False((await Validate(bare)).Valid);
    }

    [Fact]
    public void SessionLifetime_DefaultsToTwelveHours()
    {
        Assert.Equal(TimeSpan.FromHours(12),
            SessionLifetime.FromConfiguration(new ConfigurationBuilder().Build()).AbsoluteLifetime);
    }

    private sealed class FixedAuth(ClaimsPrincipal p) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(p));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private CurrentUser CurrentUserFor(ClaimsPrincipal principal) =>
        new(new FixedAuth(principal), _users, _customers, Options, new FixedTime(Now));

    [Fact]
    public async Task CurrentUser_InactiveUser_Null()
    {
        var user = await AddStaff();
        var principal = AppClaims.PrincipalFor(user, "Cookies", Now);
        await _users.UpsertAsync(user with { Active = false });

        Assert.Null(await CurrentUserFor(principal).GetAsync());
    }

    [Fact]
    public async Task CurrentUser_InactiveCustomer_Null()
    {
        var user = await AddCustomerUser(customerActive: false);

        Assert.Null(await CurrentUserFor(AppClaims.PrincipalFor(user, "Cookies", Now)).GetAsync());
    }

    [Fact]
    public async Task CurrentUser_ActiveUser_ReturnsFreshRecord()
    {
        var user = await AddStaff();
        var principal = AppClaims.PrincipalFor(user, "Cookies", Now);
        await _users.UpsertAsync(user with { StaffRole = StaffRole.StaffAdmin });

        Assert.Equal(StaffRole.StaffAdmin, (await CurrentUserFor(principal).GetAsync())!.StaffRole);
    }
}
