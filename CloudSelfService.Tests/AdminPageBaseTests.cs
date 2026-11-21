using System.Security.Claims;
using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Files;
using CloudSelfService.Services.Identity;
using CloudSelfService.Shared;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>A role dropped while a circuit is open must stop reads and reloads, not just writes.</summary>
public class AdminPageBaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedAuth(ClaimsPrincipal p) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(p));
    }

    private sealed class TestPage : AdminPageBase
    {
        public StaffRole Required = StaffRole.StaffAdmin;
        public int Cleared;
        protected override StaffRole RequiredRole => Required;
        protected override void ClearData() { base.ClearData(); Cleared++; }

        public void Wire(CurrentUser cu, OwnerConfig owners, AdminService admin)
        {
            CurrentUser = cu;
            Owners = owners;
            Admin = admin;
            PageAudit = new PageAuditContext();
            Loggers = NullLoggerFactory.Instance;
        }

        public bool IsLoaded => Loaded;
        public bool IsDenied => Denied;
        public bool IsSessionEnded => SessionEnded;
        public int Ver => Version;
        public AdminAlert? CurrentAlert => Alert;
        public Task Load(Func<Task> f) => LoadGuardedAsync(f);
        public Task Act(Func<Guid, AuditContext, Task<AdminAlert>> a, Func<Task> r) => ActAsync(a, r);
        public Task<bool> Run(Func<Task> f) => RunGuardedAsync(f);
    }

    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryCustomerStore _customers = new();
    private readonly AppUser _admin = new(Guid.NewGuid(), UserKind.Staff, null, "ada@advancedutility.com", "Ada", "oid-ada", StaffRole.StaffAdmin, true, Now, null);
    private readonly TestPage _page = new();

    public AdminPageBaseTests()
    {
        _users.UpsertAsync(_admin).Wait();
        Wire(_admin);
    }

    private void Wire(AppUser signedInAs)
    {
        var owners = new OwnerConfig("");
        var cu = new CurrentUser(new FixedAuth(AppClaims.PrincipalFor(signedInAs, "test", Now)), _users, _customers,
            new SessionLifetime(TimeSpan.FromHours(12)), new AppRoleHandlerTests.FixedTime(Now));
        var targets = new InMemoryTargetStore();
        var svc = new AdminService(_users, _customers, targets, new InMemoryGrantStore(targets), new CloudSelfService.Services.Storage.FakeStorageBackend(),
            new AuditService(new InMemoryAuditStore(), NullLogger<AuditService>.Instance), owners,
            new AppRoleHandlerTests.FixedTime(Now), new ConfigurationBuilder().Build());
        _page.Wire(cu, owners, svc);
    }

    [Fact]
    public async Task Load_StillStaffAdmin_RunsQuery()
    {
        var queries = 0;

        await _page.Load(() => { queries++; return Task.CompletedTask; });

        Assert.Equal(1, queries);
        Assert.True(_page.IsLoaded);
        Assert.False(_page.IsDenied);
    }

    [Fact]
    public async Task Run_DemotedBetweenLoadAndApply_NoQuery_DeniedStateAndDataCleared()
    {
        await _page.Load(() => Task.CompletedTask);
        await _users.UpsertAsync(_admin with { StaffRole = StaffRole.Staff });
        var queries = 0;

        var ran = await _page.Run(() => { queries++; return Task.CompletedTask; });

        Assert.False(ran);
        Assert.Equal(0, queries);
        Assert.True(_page.IsDenied);
        Assert.False(_page.IsLoaded);
        Assert.Equal(1, _page.Cleared);
    }

    [Fact]
    public async Task Act_DemotedDuringAction_ReloadNotCalled_DeniedState()
    {
        var reloads = 0;

        await _page.Act(async (actor, _) =>
        {
            await _users.UpsertAsync(_admin with { StaffRole = StaffRole.None });
            return AdminAlert.Success("done");
        }, () => { reloads++; return Task.CompletedTask; });

        Assert.Equal(0, reloads);
        Assert.True(_page.IsDenied);
    }

    [Fact]
    public async Task Act_Allowed_ReloadsAndBumpsVersion()
    {
        var reloads = 0;
        var before = _page.Ver;

        await _page.Act((_, _) => Task.FromResult(AdminAlert.Success("ok")), () => { reloads++; return Task.CompletedTask; });

        Assert.Equal(1, reloads);
        Assert.Equal(before + 1, _page.Ver);
        Assert.Equal("ok", _page.CurrentAlert!.Text);
    }

    [Fact]
    public async Task Act_RefusedChange_StillBumpsVersionSoSelectsRevert()
    {
        var before = _page.Ver;

        await _page.Act((_, _) => Task.FromResult(new AdminAlert("nope", "alert--danger")), () => Task.CompletedTask);

        Assert.Equal(before + 1, _page.Ver);
    }

    [Fact]
    public async Task Page_RequiringOwner_StaffAdminIsDenied()
    {
        _page.Required = StaffRole.Owner;

        await _page.Load(() => Task.CompletedTask);

        Assert.True(_page.IsDenied);
    }

    [Fact]
    public async Task Act_SignedInUserGone_SessionEndedNotStoreDown()
    {
        await _users.UpsertAsync(_admin with { Active = false });

        await _page.Act((_, _) => Task.FromResult(AdminAlert.Success("x")), () => Task.CompletedTask);

        Assert.True(_page.IsSessionEnded);
        Assert.Null(_page.CurrentAlert);
    }

    [Fact]
    public async Task Act_ReloadFailsAfterSuccess_KeepsSuccessAndAddsWarning()
    {
        await _page.Act((_, _) => Task.FromResult(AdminAlert.Success("Saved.")), () => throw new InvalidOperationException("db down"));

        Assert.Equal("Saved." + AdminPageBase.RefreshFailedSuffix, _page.CurrentAlert!.Text);
        Assert.False(_page.IsDenied);
    }

    [Fact]
    public async Task Load_StoreFailure_ShowsUnavailable()
    {
        await _page.Load(() => throw new InvalidOperationException("db down"));

        Assert.Equal(AdminAlert.StoreDownMessage, _page.CurrentAlert!.Text);
        Assert.False(_page.IsLoaded);
    }
}
