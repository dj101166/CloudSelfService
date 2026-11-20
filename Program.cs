using CloudSelfService;
using CloudSelfService.Services;

var builder = WebApplication.CreateBuilder(args);

// Force en-US culture so date/number formatting is consistent regardless of host locale
var usCulture = new System.Globalization.CultureInfo("en-US");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture   = usCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = usCulture;

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<AppVersionService>();
builder.Services.AddHsts(options => { options.MaxAge = TimeSpan.FromDays(365); });

// ── Auth placeholder ─────────────────────────────────────────────────────────
// Auth is intentionally disabled during initial development (Auth:Provider = None).
//
// To enable Microsoft Entra OIDC, add to appsettings.json:
//   "Auth":  { "Provider": "Entra" },
//   "Entra": { "TenantId": "", "ClientId": "", "ClientSecret": "", "AllowedEmailDomain": "" }
// and reference EscapedDefects/Program.cs for the full OIDC + Graph wiring.
//
// Required packages (currently absent — add to .csproj when enabling):
//   Microsoft.AspNetCore.Authentication.OpenIdConnect
//   Azure.Identity
// ─────────────────────────────────────────────────────────────────────────────

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' wss: ws:; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self';";
    await next();
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
