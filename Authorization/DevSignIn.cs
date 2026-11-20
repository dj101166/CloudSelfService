using CloudSelfService.Endpoints;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace CloudSelfService.Authorization;

/// <summary>
/// Development-only sign-in as a fixed persona, so the app can be exercised without an Entra
/// registration. Every persona, the stranger included, goes through
/// <see cref="IdentityResolver.AdmitAsync(EntraIdentity, AuditContext?, CancellationToken)"/> with a
/// synthetic token identity: this path tests real admission rather than bypassing it.
/// </summary>
public static class DevSignIn
{
    public const string ProviderName = "Dev";

    private const string DevAccount = "cssptestdemo";
    private const string CustomerDomain = "customer.example";

    private enum PersonaKind { Staff, Customer, Stranger }

    private sealed record Persona(string Key, string Label, PersonaKind Kind, StaffRole Role = StaffRole.None,
        AccessLevel Level = AccessLevel.None);

    private static readonly Persona[] All =
    {
        new("staff-owner", "Staff (Owner)", PersonaKind.Staff, StaffRole.Owner),
        new("staff", "Staff", PersonaKind.Staff, StaffRole.Staff),
        new("staff-none", "Staff (no role yet)", PersonaKind.Staff, StaffRole.None),
        new("customer-read", "Customer (Read)", PersonaKind.Customer, Level: AccessLevel.Read),
        new("customer-contribute", "Customer (Contribute)", PersonaKind.Customer, Level: AccessLevel.Contribute),
        new("customer-full", "Customer (Full)", PersonaKind.Customer, Level: AccessLevel.Full),
        new("stranger", "Stranger (not registered)", PersonaKind.Stranger),
    };

    public static IReadOnlyList<(string Key, string Label)> Personas { get; } =
        All.Select(p => (p.Key, p.Label)).ToList();

    public static bool IsDevProvider(IConfiguration config) =>
        string.Equals(config["Auth:Provider"], ProviderName, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled(IHostEnvironment env, IConfiguration config) =>
        IsDevProvider(config) && env.IsDevelopment();

    public static void AssertSafe(IHostEnvironment env, IConfiguration config)
    {
        if (IsDevProvider(config) && !env.IsDevelopment())
            throw new InvalidOperationException("Dev sign-in cannot run outside Development");
    }

    public static void Map(WebApplication app)
    {
        if (!IsEnabled(app.Environment, app.Configuration)) return;

        app.MapGet("/dev/signin", async ([FromQuery(Name = "as")] string? persona, HttpContext ctx,
            IdentityResolver resolver, IConfiguration config, IUserStore users, ICustomerStore customers,
            ITargetStore targets, IGrantStore grants) =>
        {
            var p = All.FirstOrDefault(x => string.Equals(x.Key, persona, StringComparison.OrdinalIgnoreCase));
            if (p is null)
                return Results.BadRequest($"Unknown persona. Use ?as= one of: {string.Join(", ", All.Select(x => x.Key))}");

            var staffDomain = config["Entra:StaffEmailDomain"] ?? "advancedutility.com";
            var upn = p.Kind == PersonaKind.Staff ? $"dev-{p.Key}@{staffDomain}" : $"dev-{p.Key}@{CustomerDomain}";
            var oid = $"dev-oid-{p.Key}";

            if (p.Kind == PersonaKind.Staff && p.Role != StaffRole.None)
                await EnsureStaffAsync(users, upn, p, ctx.RequestAborted);
            else if (p.Kind == PersonaKind.Customer)
                await EnsureCustomerAsync(users, customers, targets, grants, upn, p, ctx.RequestAborted);

            var identity = new EntraIdentity(oid, "dev-tenant", upn, p.Label, IsGuest: p.Kind != PersonaKind.Staff);
            var admission = await resolver.AdmitAsync(identity, AuthEndpoints.AuditContextFor(ctx), ctx.RequestAborted);

            var denied = AuthEndpoints.DeniedRedirect(admission.Outcome);
            if (denied is not null || admission.User is null)
            {
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect(denied ?? AuthEndpoints.DeniedRedirect(AdmissionOutcome.UnknownCustomer)!);
            }

            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                AppClaims.PrincipalFor(admission.User, CookieAuthenticationDefaults.AuthenticationScheme));
            return Results.Redirect("/");
        }).AllowAnonymous();
    }

    private static async Task EnsureStaffAsync(IUserStore users, string upn, Persona p, CancellationToken ct)
    {
        if (await users.FindByUpnAsync(upn, ct) is not null) return;
        await users.UpsertAsync(new AppUser(Guid.NewGuid(), UserKind.Staff, null, upn, p.Label, null, p.Role, true,
            DateTimeOffset.UtcNow, null), ct);
    }

    // Each customer persona gets its own customer, target and grant: a grant is per customer, so
    // personas sharing one customer could not hold different levels.
    private static async Task EnsureCustomerAsync(IUserStore users, ICustomerStore customers, ITargetStore targets,
        IGrantStore grants, string upn, Persona p, CancellationToken ct)
    {
        var levelName = p.Level.ToString();
        var customerName = $"Demo Customer ({levelName})";
        var customer = (await customers.ListAsync(ct)).FirstOrDefault(c => c.Name == customerName);
        if (customer is null)
        {
            customer = new Customer(Guid.NewGuid(), customerName, true, "Seeded by /dev/signin.", null,
                CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
            await customers.UpsertAsync(customer, ct);
        }

        var share = $"demo-{levelName.ToLowerInvariant()}";
        var target = (await targets.ListAsync(customer.Id, ct))
            .FirstOrDefault(t => t.AccountName == DevAccount && t.ContainerName == share);
        if (target is null)
        {
            target = new StorageTarget(Guid.NewGuid(), customer.Id, $"Demo share ({levelName})", "", "", DevAccount,
                StorageKind.FileShare, share, "core.windows.net", true);
            await targets.UpsertAsync(target, ct);
        }

        if (await grants.GetAsync(customer.Id, target.Id, ct) is null)
            await grants.SetAsync(new Grant(customer.Id, target.Id, p.Level, "dev-signin", DateTimeOffset.UtcNow), ct);

        if (await users.FindByUpnAsync(upn, ct) is null)
            await users.UpsertAsync(new AppUser(Guid.NewGuid(), UserKind.Customer, customer.Id, upn, p.Label, null,
                StaffRole.None, true, DateTimeOffset.UtcNow, null), ct);
    }
}
