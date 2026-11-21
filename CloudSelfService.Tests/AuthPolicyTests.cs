using CloudSelfService.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CloudSelfService.Tests;

public class AuthPolicyTests
{
    [Fact]
    public void CookieFor_Production_HostPrefixedAndAlwaysSecure()
    {
        var c = AuthPolicy.CookieFor(isDevelopment: false);

        Assert.Equal("__Host-cssp", c.Name);
        Assert.Equal(CookieSecurePolicy.Always, c.SecurePolicy);
    }

    [Fact]
    public void CookieFor_Development_PlainNameSameAsRequest()
    {
        var c = AuthPolicy.CookieFor(isDevelopment: true);

        Assert.Equal("cssp-dev", c.Name);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, c.SecurePolicy);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ParseTenantId_NotAGuid_Throws(string? configured)
    {
        Assert.Throws<InvalidOperationException>(() => AuthPolicy.ParseTenantId(configured));
    }

    [Fact]
    public void ParseTenantId_Guid_Parses()
    {
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"),
            AuthPolicy.ParseTenantId("11111111-2222-3333-4444-555555555555"));
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555", true)]
    [InlineData("AAAAAAAA-2222-3333-4444-555555555555", false)]
    [InlineData("{11111111-2222-3333-4444-555555555555}", true)]
    [InlineData("99999999-2222-3333-4444-555555555555", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOurTenant_ComparesAsGuid(string? tid, bool expected)
    {
        Assert.Equal(expected, AuthPolicy.IsOurTenant(tid, Guid.Parse("11111111-2222-3333-4444-555555555555")));
    }

    [Fact]
    public void IsOurTenant_CaseInsensitive()
    {
        var ours = Guid.Parse("abcdef12-2222-3333-4444-555555555555");

        Assert.True(AuthPolicy.IsOurTenant("ABCDEF12-2222-3333-4444-555555555555", ours));
    }

    [Fact]
    public void FailureSummary_WithAadstsCodes_ReturnsOnlyCodes()
    {
        var ex = new InvalidOperationException(
            "Message contains error: 'invalid_client', error_description: 'AADSTS7000215: Invalid client secret. Trace ID: abc state=secretish', AADSTS7000215");

        Assert.Equal("AADSTS7000215", AuthPolicy.FailureSummary(ex));
    }

    [Fact]
    public void FailureSummary_NoCode_ReturnsTypeNameNotMessage()
    {
        var summary = AuthPolicy.FailureSummary(new TimeoutException("code=sensitive-value"));

        Assert.Equal("TimeoutException", summary);
        Assert.Equal("unknown", AuthPolicy.FailureSummary(null));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/login", true)]
    [InlineData("/keepalive", true)]
    [InlineData("/_blazor", true)]
    [InlineData("/access-denied", true)]
    [InlineData("/_framework/blazor.web.js", false)]
    [InlineData("/css/app.css", false)]
    [InlineData("/js/session-guard.js", false)]
    [InlineData("/images/advanced-logo-a.png", false)]
    [InlineData("/CloudSelfService.styles.css", false)]
    [InlineData("/CloudSelfService.k8bafzscy8.styles.css", false)]
    [InlineData("/favicon.png", false)]
    [InlineData("/favicon.ico", false)]
    [InlineData("/fonts/zenkaku-400.woff2", false)]
    [InlineData("/lib/spark-md5/spark-md5.min.js", false)]
    [InlineData("/Shared/ReconnectModal.razor.js", false)]
    [InlineData("/files/7d1e3a2c-0000-4000-8000-000000000001/2024.01", true)]
    [InlineData("/files/7d1e3a2c-0000-4000-8000-000000000001/Reports/q1.final", true)]
    [InlineData("/files/7d1e3a2c-0000-4000-8000-000000000001", true)]
    [InlineData("/api/files/7d1e3a2c-0000-4000-8000-000000000001/download", true)]
    [InlineData("/api/admin/audit.csv", true)]
    [InlineData("/help", true)]
    [InlineData("/some.page", true)]
    [InlineData("/robots.txt", true)]
    public void ShouldDisableCaching_PagesAndEndpointsOnly(string path, bool expected)
    {
        Assert.Equal(expected, AuthPolicy.ShouldDisableCaching(new PathString(path)));
    }
}
