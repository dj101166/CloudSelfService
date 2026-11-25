using System.Globalization;
using System.Text.RegularExpressions;
using CloudSelfService.Tests.Infrastructure;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// The document shell (App.razor) tells uploader.js when the sign-in ends, so running uploads can be warned before they
/// pause. That value must exist only for a signed-in user and must come from the cookie's own issue time.
/// </summary>
public sealed class AppShellContractTests : IDisposable
{
    private readonly TestAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static string AppSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.razor")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "App.razor"));
    }

    [Fact]
    public void App_SessionEndsAttribute_OnlyForAuthenticatedUser()
    {
        var app = AppSource();
        Assert.Contains("data-session-ends", app);

        var claim = app.IndexOf("AppClaims.IssuedAt", StringComparison.Ordinal);
        Assert.True(claim >= 0, "The sign-in end must come from the issued-at claim.");
        var guard = app.LastIndexOf("IsAuthenticated", claim, StringComparison.Ordinal);
        Assert.True(guard >= 0, "The issued-at claim must be read only inside a branch guarded by IsAuthenticated.");
        // The guard has to be in the same member as the claim read, not some earlier unrelated check.
        var between = app.Substring(guard, claim - guard);
        Assert.DoesNotMatch(new Regex(@"\b(private|public|protected|internal)\s"), between);
    }

    [Fact]
    public async Task App_SignedIn_BodyCarriesSessionEnd_IssuedAtPlusAbsoluteLifetime()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var client = await _factory.CreateClientAs("customer-read");

        var html = await (await client.GetAsync("/help")).Content.ReadAsStringAsync();

        var match = Regex.Match(html, "<body[^>]*\\sdata-session-ends=\"(\\d+)\"");
        Assert.True(match.Success, "A signed-in page must carry data-session-ends on <body>.");
        var ends = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var twelveHours = (long)TimeSpan.FromHours(12).TotalSeconds;
        Assert.InRange(ends, before + twelveHours - 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + twelveHours + 1);
    }

    [Fact]
    public async Task App_Anonymous_HasNoSessionEnd()
    {
        var html = await (await _factory.CreateAnonymousClient().GetAsync("/login")).Content.ReadAsStringAsync();

        Assert.Contains("data-testid=\"login-card\"", html);
        Assert.DoesNotContain("data-session-ends", html);
    }
}
