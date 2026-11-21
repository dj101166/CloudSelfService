using System.Net;
using System.Text.RegularExpressions;
using CloudSelfService.Authorization;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// Owner is derived from configuration and never stored, so a configured Owner's record holds None. Every place that
/// shows or gates by role must read it through <see cref="StaffAccess"/>, or a real Owner is locked out on a fresh database.
/// </summary>
public class StaffAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private const string OwnerUpn = "olivia@advancedutility.com";

    private static AppUser User(UserKind kind, StaffRole stored, string upn = OwnerUpn) =>
        new(Guid.NewGuid(), kind, kind == UserKind.Customer ? Guid.NewGuid() : null, upn, "N", "oid", stored, true, Now, null);

    [Fact]
    public void ConfiguredOwner_StoredNone_IsOwnerAndNotPending()
    {
        var owners = new OwnerConfig(OwnerUpn);
        var user = User(UserKind.Staff, StaffRole.None);

        Assert.Equal(StaffRole.Owner, StaffAccess.Effective(user, owners));
        Assert.False(StaffAccess.IsPending(user, owners));
        Assert.True(StaffAccess.Reaches(user, owners, StaffRole.Owner));
    }

    [Fact]
    public void StaffNone_NotConfigured_IsPending()
    {
        var owners = new OwnerConfig("someone.else@advancedutility.com");
        var user = User(UserKind.Staff, StaffRole.None);

        Assert.Equal(StaffRole.None, StaffAccess.Effective(user, owners));
        Assert.True(StaffAccess.IsPending(user, owners));
        Assert.False(StaffAccess.Reaches(user, owners, StaffRole.Staff));
    }

    [Fact]
    public void Customer_IsNeverPendingAndHoldsNoStaffRole()
    {
        // The customer's UPN is even in the Owners list and its record carries Owner: neither may count.
        var owners = new OwnerConfig(OwnerUpn);
        var user = User(UserKind.Customer, StaffRole.Owner);

        Assert.False(StaffAccess.IsPending(user, owners));
        Assert.Equal(StaffRole.None, StaffAccess.Effective(user, owners));
        Assert.False(StaffAccess.Reaches(user, owners, StaffRole.Staff));
    }

    [Fact]
    public void StoredOwner_NoLongerConfigured_IsStaffAdmin()
    {
        var user = User(UserKind.Staff, StaffRole.Owner);

        Assert.Equal(StaffRole.StaffAdmin, StaffAccess.Effective(user, new OwnerConfig("")));
        Assert.False(StaffAccess.IsPending(user, new OwnerConfig("")));
    }

    [Theory]
    [InlineData("Shared/MainLayout.razor", "StaffAccess.IsPending(")]
    [InlineData("Shared/NavMenu.razor", "StaffAccess.Effective(")]
    [InlineData("Shared/AdminPageBase.cs", "StaffAccess.Reaches(")]
    [InlineData("Authorization/AppRoleRequirement.cs", "StaffAccess.Reaches(")]
    public void RoleDecisions_GoThroughTheHelper(string file, string call)
    {
        var text = RepoFile(file);

        Assert.Contains(call, text);
        // The stored role must never decide on its own (the bug that locked a configured Owner out).
        Assert.DoesNotMatch(new Regex(@"StaffRole:\s*StaffRole\.None"), text);
        Assert.DoesNotContain("EffectiveRole(", text);
    }

    [Fact]
    public void DevStaffOwnerPersona_IsStoredWithNoRole_LikeProduction() =>
        Assert.Equal(StaffRole.None, DevSignIn.StoredRole("staff-owner"));

    [Fact]
    public async Task StaffOwnerPersona_StoredNone_ReachesStaffPage()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("staff-owner");

        var stored = await factory.Services.GetRequiredService<IUserStore>()
            .FindByUpnAsync(DevSignIn.UpnFor("staff-owner", factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
        Assert.Equal(StaffRole.None, stored!.StaffRole);

        var response = await client.GetAsync("/admin/staff");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-testid=\"admin-staff-table\"", html);
        Assert.DoesNotContain("data-testid=\"pending-access\"", html);
    }

    [Fact]
    public async Task ConfiguredOwnerWithStoredNone_SeesAdminNotPending()
    {
        // staff-none's record holds None and is not an Owner persona; naming its UPN in Authorization:Owners is
        // exactly a production Owner's first sign-in on a fresh database.
        using var factory = new TestAppFactory(("Authorization:Owners", "dev-staff-none@advancedutility.com"));
        var client = await factory.CreateClientAs("staff-none");

        var html = await (await client.GetAsync("/admin/staff")).Content.ReadAsStringAsync();

        Assert.Contains("data-testid=\"admin-staff-table\"", html);
        Assert.DoesNotContain("data-testid=\"pending-access\"", html);
    }

    [Fact]
    public async Task UnconfiguredStaffNone_SeesPending()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("staff-none");

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("data-testid=\"pending-access\"", html);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }
}
