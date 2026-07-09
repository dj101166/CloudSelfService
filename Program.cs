using CloudSelfService.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Force en-US culture so date/number formatting is consistent regardless of host locale
System.Globalization.CultureInfo.DefaultThreadCurrentCulture   = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();
builder.Services.AddSingleton<AppVersionService>();

// ── Auth placeholder ─────────────────────────────────────────────────────────
// Auth is intentionally disabled during initial development.
//
// To enable GitHub OAuth, add to appsettings.json:
//   "GitHub": { "ClientId": "", "ClientSecret": "", "Organization": "" }
// and reference EscapedDefects/Program.cs for the full OAuth round-trip wiring.
//
// To enable Microsoft Entra OIDC, add:
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
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' wss: ws:; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self';";
    await next();
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
