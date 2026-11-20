using System.Security.Claims;
using CloudSelfService.Authorization;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// Guards against a crash that kills the host process outright: a signed-in principal that FAILS
/// a policy. The <c>NotAuthorized</c> fragment must navigate only for an anonymous caller, whose
/// fragment renders on the renderer's first, non-streaming pass, where <c>NavigateTo</c> becomes a
/// real 302. A signed-in caller's fragment renders on an async completion (policy evaluation awaits
/// <see cref="AppRoleHandler"/>), where the same navigation sends the static HTML renderer into
/// unbounded recursion - measured on TCGenesisArchitect as <c>Stack overflow.</c> at ~1,170 frames.
/// </summary>
public class NotAuthorizedRoutingTests
{
    private static string RepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }

    private static ClaimsPrincipal SignedIn(string name = "user@example.com")
        => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }, "TestAuth"));

    [Fact]
    public void SignedInPrincipal_FailingAPolicy_IsNotSentToLogin()
    {
        Assert.False(NotAuthorizedRouting.ShouldRedirectToLogin(SignedIn()));
    }

    [Fact]
    public void AnonymousPrincipal_IsSentToLogin()
    {
        Assert.True(NotAuthorizedRouting.ShouldRedirectToLogin(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.True(NotAuthorizedRouting.ShouldRedirectToLogin(null));
    }

    [Fact]
    public void NotAuthorizedBranchesOnWhetherTheCallerIsSignedIn()
    {
        // Without this, flattening the fragment back to a bare <RedirectToLogin /> leaves every
        // other assertion green while re-arming the host-killing crash.
        var routes = RepoFile("Routes.razor");

        Assert.Contains("NotAuthorizedRouting.ShouldRedirectToLogin", routes);
        Assert.Contains("<AccessDeniedContent />", routes);
        Assert.Contains("<AccessDeniedContent", RepoFile(Path.Combine("Pages", "AccessDenied.razor")));
        Assert.Contains("data-testid=\"access-denied\"", RepoFile(Path.Combine("Shared", "AccessDeniedContent.razor")));
    }

    [Fact]
    public void RedirectToLoginNavigatesAtMostOnce()
    {
        // Hygiene, NOT the crash fix: the recursion built a fresh component per level, so a
        // per-instance latch could never have stopped it.
        var component = RepoFile(Path.Combine("Shared", "RedirectToLogin.razor"));

        Assert.Contains("_navigated", component);
        Assert.Contains("NavigateTo", component);
    }
}
