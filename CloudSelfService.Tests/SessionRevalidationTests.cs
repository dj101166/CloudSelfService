using System.Security.Claims;
using CloudSelfService.Authorization;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

public class SessionRevalidationTests
{
    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryCustomerStore _customers = new();

    private SessionRevalidatingStateProvider Provider(int revalidateSeconds = 60)
    {
        var services = new ServiceCollection()
            .AddSingleton<IUserStore>(_users)
            .AddSingleton<ICustomerStore>(_customers)
            .AddSingleton<TimeProvider>(new AppRoleHandlerTests.FixedTime(AppRoleHandlerTests.Now))
            .AddSingleton(new SessionLifetime(TimeSpan.FromHours(12)))
            .BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:RevalidateSeconds"] = revalidateSeconds.ToString(),
        }).Build();
        return new SessionRevalidatingStateProvider(NullLoggerFactory.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), config);
    }

    private async Task<AppUser> AddStaff()
    {
        var u = new AppUser(Guid.NewGuid(), UserKind.Staff, null, "sam@advancedutility.com", "Sam", "oid-sam",
            StaffRole.Staff, true, AppRoleHandlerTests.Now, null);
        await _users.UpsertAsync(u);
        return u;
    }

    [Fact]
    public async Task Revalidate_DeactivatedUser_False()
    {
        var user = await AddStaff();
        var state = new AuthenticationState(AppClaims.PrincipalFor(user, "Cookies", AppRoleHandlerTests.Now));
        var provider = Provider();
        Assert.True(await provider.ValidateForTestAsync(state));

        await _users.UpsertAsync(user with { Active = false });

        Assert.False(await provider.ValidateForTestAsync(state));
    }

    [Fact]
    public async Task Revalidate_AnonymousState_False()
    {
        Assert.False(await Provider().ValidateForTestAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "CloudSelfService";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration DevConfig(string provider, string owners = "real@advancedutility.com") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Provider"] = provider,
            ["Authorization:Owners"] = owners,
            ["Entra:StaffEmailDomain"] = "advancedutility.com",
        }).Build();

    [Fact]
    public async Task DevStaffOwnerPersona_PassesOwnerPolicy_InDevelopment()
    {
        var owners = new OwnerConfig(DevSignIn.EffectiveOwners(new Env(Environments.Development), DevConfig("Dev")));
        var persona = new AppUser(Guid.NewGuid(), UserKind.Staff, null, "dev-staff-owner@advancedutility.com",
            "Staff (Owner)", "dev-oid-staff-owner", StaffRole.Owner, true, AppRoleHandlerTests.Now, null);
        await _users.UpsertAsync(persona);

        var context = AppRoleHandlerTests.ContextFor(AppClaims.PrincipalFor(persona, "Cookies", AppRoleHandlerTests.Now), StaffRole.Owner);
        await new AppRoleHandler(_users, _customers, owners, new AppRoleHandlerTests.FixedTime(AppRoleHandlerTests.Now),
            new SessionLifetime(TimeSpan.FromHours(12))).HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.True(owners.IsOwner("real@advancedutility.com"));
    }

    [Theory]
    [InlineData("Production", "Dev")]
    [InlineData("Development", "Entra")]
    [InlineData("Production", "Entra")]
    public void DevStaffOwnerPersona_NotAnOwner_OutsideDevSignIn(string environment, string provider)
    {
        var owners = new OwnerConfig(DevSignIn.EffectiveOwners(new Env(environment), DevConfig(provider)));

        Assert.False(owners.IsOwner("dev-staff-owner@advancedutility.com"));
        Assert.True(owners.IsOwner("real@advancedutility.com"));
    }

    [Fact]
    public void Dockerignore_ExcludesDevelopmentSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, ".dockerignore")).Select(l => l.Trim()).ToList();

        foreach (var required in new[]
                 {
                     "appsettings.Development.json", "appsettings.*.json", "!appsettings.json", "**/bin/", "**/obj/",
                     ".git/", ".claude/", ".superpowers/", "*.user", "*.msg", "docs/",
                 })
        {
            Assert.Contains(required, lines);
        }

        // The negation must come after the wildcard, or the wildcard re-excludes appsettings.json.
        Assert.True(lines.IndexOf("!appsettings.json") > lines.IndexOf("appsettings.*.json"));

        // Task 12 adds a dotnet test gate to the image build, which needs the test sources.
        Assert.DoesNotContain(lines, l => l.TrimEnd('/') is "CloudSelfService.Tests" or "CloudSelfService.Tests/**");
    }
}
