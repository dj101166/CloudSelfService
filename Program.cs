using CloudSelfService;
using CloudSelfService.Authorization;
using CloudSelfService.Endpoints;
using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Storage;
using CloudSelfService.Services.Transfers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// Force en-US culture so date/number formatting is consistent regardless of host locale
var usCulture = new System.Globalization.CultureInfo("en-US");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture   = usCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = usCulture;

{
    var keysPath = CloudSelfService.Services.DataProtectionPaths.KeyDirectory(builder.Configuration["DATA_ROOT"]);
    if (keysPath is null)
    {
        Console.WriteLine("WARNING: Data Protection keys are not persisted; sign-ins will not survive a restart. Set DATA_ROOT.");
    }
    else
    {
        try
        {
            var keysDir = new DirectoryInfo(keysPath);
            keysDir.Create();
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(keysDir)
                .SetApplicationName("CloudSelfService");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Data Protection key persistence failed ({ex.Message}). Sign-ins will not survive a restart.");
        }
    }
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .RegisterPersistentService<CloudSelfService.Services.Files.PageAuditContext>(RenderMode.InteractiveServer);
builder.Services.AddSingleton<AppVersionService>();
builder.Services.AddSingleton<PgsqlContext>();
if (StartupChecks.UsePostgres(builder.Environment.IsDevelopment(), builder.Configuration.GetConnectionString("Default")))
{
    builder.Services.AddSingleton<IUserStore, PgUserStore>();
    builder.Services.AddSingleton<ICustomerStore, PgCustomerStore>();
    builder.Services.AddSingleton<ITargetStore, PgTargetStore>();
    builder.Services.AddSingleton<IGrantStore, PgGrantStore>();
    builder.Services.AddSingleton<IAuditStore, PgAuditStore>();
    builder.Services.AddSingleton<IUploadSessionStore, PgUploadSessionStore>();
    builder.Services.AddSingleton<IUploadLimitStore, PgUploadLimitStore>();
}
else
{
    builder.Services.AddSingleton<IUserStore, InMemoryUserStore>();
    builder.Services.AddSingleton<ICustomerStore, InMemoryCustomerStore>();
    builder.Services.AddSingleton<InMemoryTargetStore>();
    builder.Services.AddSingleton<ITargetStore>(sp => sp.GetRequiredService<InMemoryTargetStore>());
    builder.Services.AddSingleton<IGrantStore, InMemoryGrantStore>();
    builder.Services.AddSingleton<IAuditStore, InMemoryAuditStore>();
    builder.Services.AddSingleton<IUploadSessionStore, InMemoryUploadSessionStore>();
    builder.Services.AddSingleton<IUploadLimitStore, InMemoryUploadLimitStore>();
}
builder.Services.AddSingleton<AuditService>();
if (DevSignIn.UseFakeStorage(builder.Configuration) && builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<FakeStorageBackend>();
    builder.Services.AddSingleton<IStorageBackendResolver>(sp => sp.GetRequiredService<FakeStorageBackend>());
}
else
{
    builder.Services.AddSingleton<StorageClientFactory>();
    builder.Services.AddSingleton<FileShareBackend>();
    builder.Services.AddSingleton<BlobBackend>();
    builder.Services.AddSingleton<IStorageBackendResolver, StorageBackendResolver>();
}
builder.Services.AddScoped<StorageAccessService>();
builder.Services.AddSingleton<ProbeCooldown>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<UploadSessionService>();
builder.Services.AddSingleton<PieceMemoryBudget>();
builder.Services.AddSingleton<UploadLimitService>();
builder.Services.AddSingleton<ChunkThroughputLimiter>();
builder.Services.AddHostedService<UploadCleanupService>();
builder.Services.AddRateLimiter(o => RateLimits.Configure(o, builder.Configuration));
builder.Services.AddHsts(options => { options.MaxAge = TimeSpan.FromDays(365); });

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(SessionLifetime.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(new OwnerConfig(DevSignIn.EffectiveOwners(builder.Environment, builder.Configuration)));
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<CloudSelfService.Services.Files.PageAuditContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, CloudSelfService.Services.Files.AuditCircuitHandler>();
builder.Services.AddSignalR(o => o.AddFilter<CloudSelfService.Services.Files.ConnectionIpHubFilter>());
builder.Services.AddSingleton<IdentityResolver>();
builder.Services.AddSingleton<IAuthorizationHandler, AppRoleHandler>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, SessionRevalidatingStateProvider>();

DevSignIn.AssertSafe(builder.Environment, builder.Configuration);
var entraEnabled = AuthEndpoints.IsEntraProvider(builder.Configuration);
if (!entraEnabled && !DevSignIn.IsDevProvider(builder.Configuration))
    throw new InvalidOperationException(
        $"Auth:Provider must be '{AuthEndpoints.EntraProviderName}' or '{DevSignIn.ProviderName}' " +
        $"(found '{builder.Configuration["Auth:Provider"]}').");
if (entraEnabled)
{
    foreach (var key in new[] { "Entra:TenantId", "Entra:ClientId", "Entra:ClientSecret" })
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration[key]))
            throw new InvalidOperationException(
                $"{key} is required when Auth:Provider is Entra. Set it in appsettings.Development.json (dev) " +
                "or the App Service configuration (prod), or use Auth:Provider = Dev for local development.");
    }
    AuthPolicy.ParseTenantId(builder.Configuration["Entra:TenantId"]);
}

var cookieSettings = AuthPolicy.CookieFor(builder.Environment.IsDevelopment());

var authBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Name = cookieSettings.Name;
        options.Cookie.SecurePolicy = cookieSettings.SecurePolicy;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(
            builder.Configuration.GetValue<int?>("Auth:IdleTimeoutMinutes") ?? 60);
        options.SlidingExpiration = true;
        options.Events = new CookieAuthenticationEvents { OnValidatePrincipal = AuthEndpoints.OnValidatePrincipalAsync };
    });

if (entraEnabled)
{
    var instance = builder.Configuration["Entra:Instance"] ?? "https://login.microsoftonline.com";
    authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        // Tenant-specific authority: B2B guests from other tenants (and other clouds) sign in
        // through OUR tenant, which is what makes their oid stable and ours to bind.
        options.Authority = $"{instance.TrimEnd('/')}/{builder.Configuration["Entra:TenantId"]}/v2.0";
        options.ClientId = builder.Configuration["Entra:ClientId"];
        options.ClientSecret = builder.Configuration["Entra:ClientSecret"];
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.CallbackPath = builder.Configuration["Entra:CallbackPath"] ?? "/signin-oidc";
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = AuthEndpoints.OnTokenValidatedAsync,
            OnRemoteFailure = ctx =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CloudSelfService.Auth")
                    .LogWarning("Entra sign-in failed: {Failure} (trace {TraceId})",
                        AuthPolicy.FailureSummary(ctx.Failure), ctx.HttpContext.TraceIdentifier);
                ctx.Response.Redirect("/login?error=signin_failed");
                ctx.HandleResponse();
                return Task.CompletedTask;
            },
        };
    });
}

// No FallbackPolicy: pages carry [Authorize] themselves, and a fallback that requires auth
// makes the cookie handler redirect /login to itself.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.Staff, p => p.Requirements.Add(new AppRoleRequirement(StaffRole.Staff)));
    options.AddPolicy(Policies.StaffAdmin, p => p.Requirements.Add(new AppRoleRequirement(StaffRole.StaffAdmin)));
    options.AddPolicy(Policies.Owner, p => p.Requirements.Add(new AppRoleRequirement(StaffRole.Owner)));
});

var app = builder.Build();

if (AdminService.ParseAllowedSubscriptions(builder.Configuration).Count == 0)
    app.Logger.LogWarning("Storage:AllowedSubscriptions is empty — any subscription the managed identity can reach may be registered");
if (StartupChecks.OwnersWarning(app.Environment.IsDevelopment(), app.Services.GetRequiredService<OwnerConfig>()) is { } ownersWarning)
    app.Logger.LogWarning("{Warning}", ownersWarning);
var uploadLimits = UploadLimitService.LimitsFrom(builder.Configuration);
if (uploadLimits.ConfiguredCeiling > uploadLimits.Ceiling)
    app.Logger.LogWarning("Transfers:MaxUploadBytesCeiling {Configured} exceeds the {Limit}-byte Azure block-blob maximum; using the maximum.",
        uploadLimits.ConfiguredCeiling, uploadLimits.Ceiling);
if (uploadLimits.ConfiguredDefault > uploadLimits.Default)
    app.Logger.LogWarning("Transfers:MaxUploadBytes {Configured} exceeds the {Limit}-byte ceiling (Transfers:MaxUploadBytesCeiling); using the ceiling.",
        uploadLimits.ConfiguredDefault, uploadLimits.Default);

var pg = app.Services.GetRequiredService<PgsqlContext>();
// An unreachable database fails the start on purpose.
if (pg.IsAvailable) await pg.EnsureSchemaAsync();
else app.Logger.LogWarning("ConnectionStrings:Default is not set; using in-memory stores. Data is not persisted.");

// TLS terminates at the App Gateway, the only ingress. Known networks are cleared so the gateway's
// X-Forwarded-Proto is honoured (otherwise the OIDC redirect URI is built as http://). The same
// trust applies to X-Forwarded-For, so the audit SourceIp is only as good as that single-ingress
// guarantee.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

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
    ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    if (AuthPolicy.ShouldDisableCaching(ctx.Request.Path))
        ctx.Response.Headers.CacheControl = "no-store";
    // Deliberately tighter than the suite CSP: no eval, and connect/img limited to this origin.
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self';";
    await next();
});

app.MapStaticAssets();

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

AuthEndpoints.Map(app);
app.MapDownloadEndpoints();
app.MapUploadEndpoints();
app.MapAdminEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = "'none'");

app.Run();

public partial class Program;
