using System.Diagnostics;
using CloudSelfService.Authorization;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace CloudSelfService.Endpoints;

public static class AuthEndpoints
{
    public const string EntraProviderName = "Entra";

    public static bool IsEntraProvider(IConfiguration config) =>
        string.Equals(config["Auth:Provider"], EntraProviderName, StringComparison.OrdinalIgnoreCase);

    public static string? DeniedRedirect(AdmissionOutcome outcome) => outcome switch
    {
        AdmissionOutcome.UnknownCustomer => "/access-denied?reason=unknown",
        AdmissionOutcome.Deactivated => "/access-denied?reason=deactivated",
        _ => null,
    };

    public static AuditContext AuditContextFor(HttpContext ctx) =>
        new(ctx.Connection.RemoteIpAddress?.ToString() ?? "",
            Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier);

    /// <summary>
    /// Runs after Entra has authenticated the caller and before any cookie is issued, so a
    /// refused identity leaves with no session at all.
    /// </summary>
    public static async Task OnTokenValidatedAsync(TokenValidatedContext ctx)
    {
        var id = ctx.Principal is null ? null : EntraIdentityReader.Read(ctx.Principal);
        var tenant = AuthPolicy.ParseTenantId(ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Entra:TenantId"]);
        // Every token minted for this single-tenant registration carries our tid - guests included,
        // since they authenticate through our tenant. Anything else is not a token we asked for.
        if (id is null || !AuthPolicy.IsOurTenant(id.TenantId, tenant))
        {
            ctx.Response.Redirect(DeniedRedirect(AdmissionOutcome.UnknownCustomer)!);
            ctx.HandleResponse();
            return;
        }

        var resolver = ctx.HttpContext.RequestServices.GetRequiredService<IdentityResolver>();
        var admission = await resolver.AdmitAsync(id, AuditContextFor(ctx.HttpContext), ctx.HttpContext.RequestAborted);

        var denied = DeniedRedirect(admission.Outcome);
        if (denied is not null || admission.User is null)
        {
            ctx.Response.Redirect(denied ?? DeniedRedirect(AdmissionOutcome.UnknownCustomer)!);
            ctx.HandleResponse();
            return;
        }

        ctx.Principal = AppClaims.PrincipalFor(admission.User, CookieAuthenticationDefaults.AuthenticationScheme, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Re-checks the session against the store on every cookie-authenticated request. Sliding
    /// expiry and /keepalive would otherwise keep a deactivated or deleted user signed in
    /// indefinitely.
    /// </summary>
    public static async Task OnValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        var result = await SessionValidator.ValidateAsync(ctx.Principal, sp.GetRequiredService<IUserStore>(),
            sp.GetRequiredService<ICustomerStore>(), sp.GetRequiredService<TimeProvider>().GetUtcNow(),
            sp.GetRequiredService<SessionLifetime>(), ctx.HttpContext.RequestAborted);
        if (result.Valid) return;

        sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthEndpoints))
            .LogInformation("Session rejected: {Reason}.", result.Reason);
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public static void Map(WebApplication app)
    {
        var entra = IsEntraProvider(app.Configuration);

        // A separate challenge route keeps /signin-oidc (the CallbackPath) for Entra's callback only.
        app.MapGet("/login/entra", (HttpContext ctx) => entra
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" },
                    new[] { OpenIdConnectDefaults.AuthenticationScheme })
                : Results.Redirect("/login?error=entra_not_configured"))
            .AllowAnonymous();

        app.MapGet("/logout", async (HttpContext ctx, string? reason) =>
        {
            var signedOut = reason == "inactivity" ? "/signed-out?reason=inactivity" : "/signed-out";
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!entra)
            {
                ctx.Response.Redirect(signedOut);
                return;
            }

            // Ends the Entra session too, so the next visitor on a shared machine is not signed
            // straight back in; Entra returns the browser to RedirectUri afterwards.
            await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
                new AuthenticationProperties { RedirectUri = signedOut });
        }).AllowAnonymous();

        // Hitting this slides the auth cookie; session-guard.js pings it on user activity because
        // an interactive circuit otherwise makes almost no HTTP requests.
        app.MapGet("/keepalive", () => Results.NoContent()).RequireAuthorization();

        // uploader.js sends this token back in the RequestVerificationToken header on every upload call.
        app.MapGet("/api/antiforgery", (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(ctx);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
        }).RequireAuthorization();

        DevSignIn.Map(app);
    }
}
