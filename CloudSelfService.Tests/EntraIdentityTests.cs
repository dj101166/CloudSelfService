using System.Security.Claims;
using CloudSelfService.Services.Identity;
using Xunit;

namespace CloudSelfService.Tests;

public class EntraIdentityTests
{
    private const string OurIssuer = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0";

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "oidc"));

    [Fact]
    public void Read_GuestAcctClaim_IsGuest()
    {
        var id = EntraIdentityReader.Read(Principal(
            ("oid", "o-1"), ("tid", "t-1"), ("iss", OurIssuer),
            ("preferred_username", "pat@customer.example"), ("name", "Pat"), ("acct", "1")));

        Assert.NotNull(id);
        Assert.True(id!.IsGuest);
        Assert.Equal("pat@customer.example", id.Upn);
        Assert.Equal("Pat", id.DisplayName);
        Assert.Equal("o-1", id.ObjectId);
        Assert.Equal("t-1", id.TenantId);
    }

    [Fact]
    public void Read_IdpDiffersFromIss_IsGuest()
    {
        var id = EntraIdentityReader.Read(Principal(
            ("oid", "o-1"), ("tid", "t-1"), ("iss", OurIssuer),
            ("idp", "https://sts.windows.net/99999999-9999-9999-9999-999999999999/"),
            ("email", "pat@customer.example")));

        Assert.NotNull(id);
        Assert.True(id!.IsGuest);
        Assert.Equal("pat@customer.example", id.Upn);
    }

    [Fact]
    public void Read_MemberWithoutGuestMarkers_IsNotGuest()
    {
        var id = EntraIdentityReader.Read(Principal(
            ("oid", "o-1"), ("iss", OurIssuer), ("acct", "0"), ("preferred_username", "sam@advancedutility.com")));

        Assert.NotNull(id);
        Assert.False(id!.IsGuest);
    }

    [Fact]
    public void Read_IdpEqualsIss_IsNotGuest()
    {
        var id = EntraIdentityReader.Read(Principal(
            ("oid", "o-1"), ("iss", OurIssuer), ("idp", OurIssuer), ("preferred_username", "sam@advancedutility.com")));

        Assert.NotNull(id);
        Assert.False(id!.IsGuest);
    }

    [Fact]
    public void IsStaff_MemberAdvancedUpn_True()
    {
        var id = new EntraIdentity("o-1", "t-1", "Sam.Smith@AdvancedUtility.com", "Sam", IsGuest: false);

        Assert.True(EntraIdentityReader.IsStaff(id, "advancedutility.com"));
    }

    [Fact]
    public void IsStaff_GuestWithAdvancedEmail_False()
    {
        var id = new EntraIdentity("o-1", "t-1", "sam@advancedutility.com", "Sam", IsGuest: true);

        Assert.False(EntraIdentityReader.IsStaff(id, "advancedutility.com"));
    }

    [Fact]
    public void IsStaff_LookalikeDomain_False()
    {
        var id = new EntraIdentity("o-1", "t-1", "sam@notadvancedutility.com", "Sam", IsGuest: false);

        Assert.False(EntraIdentityReader.IsStaff(id, "advancedutility.com"));
    }

    [Fact]
    public void Read_MissingOid_Null()
    {
        Assert.Null(EntraIdentityReader.Read(Principal(("preferred_username", "sam@advancedutility.com"))));
    }
}
