using System.Security.Claims;

namespace CloudSelfService.Authorization;

/// <summary>
/// The decision the router's <c>NotAuthorized</c> fragment makes, kept as a pure function so tests
/// can pin it.
/// <para>
/// An <b>anonymous</b> caller renders that fragment on the renderer's first, non-streaming pass,
/// where <c>NavigationManager.NavigateTo</c> raises a <c>NavigationException</c> that
/// <c>EndpointHtmlRenderer</c> turns into a real 302. An <b>authenticated</b> caller that fails a
/// policy renders it on an ASYNC completion, because policy evaluation awaits
/// <see cref="AppRoleHandler"/>; by then the response has started, and the same navigation sent the
/// static HTML renderer into unbounded recursion and killed the host with <c>Stack overflow.</c>
/// (measured on TCGenesisArchitect, with and without <c>forceLoad</c>).
/// </para>
/// </summary>
public static class NotAuthorizedRouting
{
    public static bool ShouldRedirectToLogin(ClaimsPrincipal? user)
        => user?.Identity?.IsAuthenticated != true;
}
