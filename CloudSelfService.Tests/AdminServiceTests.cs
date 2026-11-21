using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

public class AdminServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly AuditContext Ctx = new("10.0.0.9", "corr-admin");
    private const string OwnerUpn = "owner@advancedutility.com";

    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryCustomerStore _customers = new();
    private readonly InMemoryTargetStore _targets = new();
    private readonly InMemoryGrantStore _grants;
    private readonly InMemoryAuditStore _audit = new();
    private readonly FakeStorageBackend _fake = new();
    private readonly AdminService _svc;

    private readonly Customer _alpha = new(Guid.NewGuid(), "Alpha", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
    private readonly Customer _bravo = new(Guid.NewGuid(), "Bravo", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
    private readonly AppUser _owner = Staff(OwnerUpn, StaffRole.Owner);
    private readonly AppUser _admin = Staff("ada@advancedutility.com", StaffRole.StaffAdmin);
    private readonly AppUser _staff = Staff("sam@advancedutility.com", StaffRole.Staff);
    private readonly AppUser _custUser;
    private readonly StorageTarget _alphaTarget;

    public AdminServiceTests()
    {
        _grants = new InMemoryGrantStore(_targets);
        _svc = Build(new Dictionary<string, string?> { ["Entra:StaffEmailDomain"] = "advancedutility.com" });

        _custUser = new AppUser(Guid.NewGuid(), UserKind.Customer, _alpha.Id, "pat@alpha.example", "Pat", null, StaffRole.None, true, Now, null);
        _alphaTarget = Target(_alpha.Id, "Alpha share", "alphastore", "alpha-share");
        _customers.UpsertAsync(_alpha).Wait();
        _customers.UpsertAsync(_bravo).Wait();
        foreach (var u in new[] { _owner, _admin, _staff, _custUser }) _users.UpsertAsync(u).Wait();
        _targets.UpsertAsync(_alphaTarget).Wait();
    }

    private AdminService Build(Dictionary<string, string?> settings) =>
        new(_users, _customers, _targets, _grants, _fake, new AuditService(_audit, NullLogger<AuditService>.Instance),
            new OwnerConfig(OwnerUpn), new AppRoleHandlerTests.FixedTime(Now),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    private static AppUser Staff(string upn, StaffRole role, bool active = true) =>
        new(Guid.NewGuid(), UserKind.Staff, null, upn, upn, "oid-" + upn, role, active, Now, null);

    private static StorageTarget Target(Guid? customerId, string name, string account, string container) =>
        new(Guid.NewGuid(), customerId, name, Guid.NewGuid().ToString(), "rg-test", account, StorageKind.FileShare, container,
            "core.windows.net", true);

    private async Task<IReadOnlyList<AuditEvent>> AuditAsync(string? op = null) =>
        await _audit.QueryAsync(new AuditQuery(Operation: op));

    [Fact]
    public async Task SetGrant_TargetOwnedByOtherCustomer_Invalid()
    {
        var result = await _svc.SetGrantAsync(_admin.Id, _bravo.Id, _alphaTarget.Id, AccessLevel.Read, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Null(await _grants.GetAsync(_bravo.Id, _alphaTarget.Id));
    }

    [Fact]
    public async Task SetGrant_InternalTarget_Invalid()
    {
        var internalTarget = Target(null, "Internal", "internalstore", "internal-one");
        await _targets.UpsertAsync(internalTarget);

        var result = await _svc.SetGrantAsync(_admin.Id, _alpha.Id, internalTarget.Id, AccessLevel.Read, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task SetGrant_ByStaffRole_Denied()
    {
        var result = await _svc.SetGrantAsync(_staff.Id, _alpha.Id, _alphaTarget.Id, AccessLevel.Read, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Null(await _grants.GetAsync(_alpha.Id, _alphaTarget.Id));
        var row = Assert.Single(await AuditAsync(AdminService.SetGrantOp));
        Assert.Equal(AuditResult.Denied, row.Result);
    }

    [Fact]
    public async Task SetGrant_ByDeactivatedAdmin_Denied()
    {
        await _users.UpsertAsync(_admin with { Active = false });

        var result = await _svc.SetGrantAsync(_admin.Id, _alpha.Id, _alphaTarget.Id, AccessLevel.Read, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task SetGrant_ByCustomerUser_Denied()
    {
        var result = await _svc.SetGrantAsync(_custUser.Id, _alpha.Id, _alphaTarget.Id, AccessLevel.Full, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task SetGrant_Level_StoresGrantWithGranter()
    {
        var result = await _svc.SetGrantAsync(_admin.Id, _alpha.Id, _alphaTarget.Id, AccessLevel.Contribute, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        var grant = await _grants.GetAsync(_alpha.Id, _alphaTarget.Id);
        Assert.Equal(AccessLevel.Contribute, grant!.Level);
        Assert.Equal(_admin.Upn, grant.GrantedBy);
    }

    [Fact]
    public async Task SetGrant_None_RemovesGrant()
    {
        await _grants.SetAsync(new Grant(_alpha.Id, _alphaTarget.Id, AccessLevel.Full, "x", Now));

        var result = await _svc.SetGrantAsync(_admin.Id, _alpha.Id, _alphaTarget.Id, AccessLevel.None, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Null(await _grants.GetAsync(_alpha.Id, _alphaTarget.Id));
    }

    [Fact]
    public async Task AddCustomerUser_StaffDomainUpn_Invalid()
    {
        var result = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, "someone@AdvancedUtility.com", "Someone", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal("Advanced staff sign in with their own account and don't need to be added.", result.Message);
        Assert.Null(await _users.FindByUpnAsync("someone@advancedutility.com"));
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("@alpha.example")]
    [InlineData("pat@")]
    [InlineData("two@@alpha.example")]
    [InlineData("has space@alpha.example")]
    public async Task AddCustomerUser_MalformedUpn_Invalid(string upn)
    {
        var result = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, upn, "", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task AddCustomerUser_DuplicateUpn_Invalid()
    {
        var result = await _svc.AddCustomerUserAsync(_admin.Id, _bravo.Id, "PAT@alpha.example", "Pat Again", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task AddCustomerUser_Valid_CreatesUnboundCustomerUser_DisplayNameDefaultsToLocalPart()
    {
        var result = await _svc.AddCustomerUserAsync(_admin.Id, _bravo.Id, " lee@bravo.example ", "", Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        var user = result.Value!;
        Assert.Equal(UserKind.Customer, user.Kind);
        Assert.Equal(_bravo.Id, user.CustomerId);
        Assert.Null(user.EntraObjectId);
        Assert.Equal("lee@bravo.example", user.Upn);
        Assert.Equal("lee", user.DisplayName);
        Assert.True(user.Active);
        Assert.NotNull(await _users.FindByUpnAsync("lee@bravo.example"));
    }

    [Fact]
    public async Task AddCustomerUser_ByStaffRole_Denied()
    {
        var result = await _svc.AddCustomerUserAsync(_staff.Id, _alpha.Id, "x@alpha.example", "", Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task SaveTarget_BadAccountName_Invalid()
    {
        foreach (var bad in new[] { "AB", "has-dash", "UPPERCASE", new string('a', 25), "" })
        {
            var result = await _svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "T", bad, "container-one"), Ctx);
            Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        }
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-start")]
    [InlineData("end-")]
    [InlineData("dou--ble")]
    [InlineData("UPPER")]
    [InlineData("under_score")]
    public async Task SaveTarget_BadContainerName_Invalid(string container)
    {
        var result = await _svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "T", "goodaccount", container), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task SaveTarget_BadSubscriptionOrSuffix_Invalid()
    {
        var good = Target(_alpha.Id, "T", "goodaccount", "container-one");

        Assert.Equal(AccessOutcome.Invalid, (await _svc.SaveTargetAsync(_admin.Id, good with { SubscriptionId = "nope" }, Ctx)).Outcome);
        Assert.Equal(AccessOutcome.Invalid, (await _svc.SaveTargetAsync(_admin.Id, good with { EndpointSuffix = "evil.example.com" }, Ctx)).Outcome);
    }

    [Fact]
    public async Task SaveTarget_GovSuffixAndBlankSuffix_Accepted()
    {
        var gov = await _svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "Gov", "govaccount", "gov-one") with { EndpointSuffix = "core.usgovcloudapi.net" }, Ctx);
        var blank = await _svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "Blank", "blankaccount", "blank-one") with { EndpointSuffix = "" }, Ctx);

        Assert.Equal(AccessOutcome.Ok, gov.Outcome);
        Assert.Equal(AccessOutcome.Ok, blank.Outcome);
        Assert.Equal("core.windows.net", blank.Value!.EndpointSuffix);
    }

    [Fact]
    public async Task SaveTarget_SuffixOutsideConfiguredAllowList_Invalid()
    {
        var svc = Build(new Dictionary<string, string?> { ["Storage:AllowedEndpointSuffixes:0"] = "core.windows.net" });

        var result = await svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "Gov", "govaccount", "gov-one") with { EndpointSuffix = "core.usgovcloudapi.net" }, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task SaveTarget_Valid_Stored()
    {
        var result = await _svc.SaveTargetAsync(_admin.Id, Target(_bravo.Id, "Bravo share", "bravostore", "bravo-share"), Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.NotNull(await _targets.GetAsync(result.Value!.Id));
    }

    [Fact]
    public async Task SaveTarget_ChangeOwnerWithGrants_Invalid()
    {
        await _grants.SetAsync(new Grant(_alpha.Id, _alphaTarget.Id, AccessLevel.Read, "x", Now));

        var result = await _svc.SaveTargetAsync(_admin.Id, _alphaTarget with { CustomerId = _bravo.Id }, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal("Remove this target's grants before moving it to another customer.", result.Message);
        Assert.Equal(_alpha.Id, (await _targets.GetAsync(_alphaTarget.Id))!.CustomerId);
    }

    [Fact]
    public async Task SaveTarget_ChangeOwnerWithoutGrants_Ok()
    {
        var result = await _svc.SaveTargetAsync(_admin.Id, _alphaTarget with { CustomerId = _bravo.Id }, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task SaveTarget_ByStaffRole_Denied()
    {
        var result = await _svc.SaveTargetAsync(_staff.Id, Target(_alpha.Id, "T", "goodaccount", "container-one"), Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task SaveCustomer_NewGov_StoredAndDefaultsToPending()
    {
        var created = await _svc.SaveCustomerAsync(_admin.Id,
            new Customer(Guid.Empty, " Gov Co ", true, "", null, CustomerCloud.AzureGov, CrossTenantStatus.NotRequired), Ctx);

        Assert.Equal(AccessOutcome.Ok, created.Outcome);
        Assert.Equal("Gov Co", created.Value!.Name);
        Assert.NotEqual(Guid.Empty, created.Value.Id);
        Assert.Equal(CrossTenantStatus.Pending, created.Value.CrossTenantStatus);
    }

    [Fact]
    public async Task SaveCustomer_CommercialIgnoresCrossTenantStatus()
    {
        var result = await _svc.SaveCustomerAsync(_admin.Id, _alpha with { CrossTenantStatus = CrossTenantStatus.Complete }, Ctx);

        Assert.Equal(CrossTenantStatus.NotRequired, result.Value!.CrossTenantStatus);
    }

    [Fact]
    public async Task SaveCustomer_BadTenantIdOrBlankNameOrDuplicate_Invalid()
    {
        Assert.Equal(AccessOutcome.Invalid, (await _svc.SaveCustomerAsync(_admin.Id, _alpha with { HomeTenantId = "not-a-guid" }, Ctx)).Outcome);
        Assert.Equal(AccessOutcome.Invalid, (await _svc.SaveCustomerAsync(_admin.Id, _alpha with { Name = "  " }, Ctx)).Outcome);
        Assert.Equal(AccessOutcome.Invalid, (await _svc.SaveCustomerAsync(_admin.Id, new Customer(Guid.Empty, "Alpha", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired), Ctx)).Outcome);
    }

    [Fact]
    public async Task SetStaffRole_Self_Invalid()
    {
        var result = await _svc.SetStaffRoleAsync(_owner.Id, _owner.Id, StaffRole.Staff, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task SetStaffRole_ToOwner_Invalid()
    {
        var result = await _svc.SetStaffRoleAsync(_owner.Id, _staff.Id, StaffRole.Owner, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(StaffRole.Staff, (await _users.GetAsync(_staff.Id))!.StaffRole);
    }

    [Fact]
    public async Task SetStaffRole_ByStaffAdmin_Denied()
    {
        var result = await _svc.SetStaffRoleAsync(_admin.Id, _staff.Id, StaffRole.StaffAdmin, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Equal(StaffRole.Staff, (await _users.GetAsync(_staff.Id))!.StaffRole);
    }

    [Fact]
    public async Task SetStaffRole_ByOwnerNoLongerInConfig_Denied()
    {
        var stale = Staff("gone@advancedutility.com", StaffRole.Owner);
        await _users.UpsertAsync(stale);

        var result = await _svc.SetStaffRoleAsync(stale.Id, _staff.Id, StaffRole.StaffAdmin, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task SetStaffRole_ByOwner_ChangesRole_CanClearToNone()
    {
        Assert.Equal(AccessOutcome.Ok, (await _svc.SetStaffRoleAsync(_owner.Id, _staff.Id, StaffRole.StaffAdmin, Ctx)).Outcome);
        Assert.Equal(StaffRole.StaffAdmin, (await _users.GetAsync(_staff.Id))!.StaffRole);
        Assert.Equal(AccessOutcome.Ok, (await _svc.SetStaffRoleAsync(_owner.Id, _staff.Id, StaffRole.None, Ctx)).Outcome);
        Assert.Equal(StaffRole.None, (await _users.GetAsync(_staff.Id))!.StaffRole);
    }

    [Fact]
    public async Task SetStaffRole_CustomerUser_NotFound()
    {
        var result = await _svc.SetStaffRoleAsync(_owner.Id, _custUser.Id, StaffRole.Staff, Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task SetUserActive_StaffUserByStaffAdmin_Denied()
    {
        var result = await _svc.SetUserActiveAsync(_admin.Id, _staff.Id, false, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.True((await _users.GetAsync(_staff.Id))!.Active);
    }

    [Fact]
    public async Task SetUserActive_StaffUserByOwner_Ok_ButNotSelf()
    {
        Assert.Equal(AccessOutcome.Ok, (await _svc.SetUserActiveAsync(_owner.Id, _staff.Id, false, Ctx)).Outcome);
        Assert.False((await _users.GetAsync(_staff.Id))!.Active);
        Assert.Equal(AccessOutcome.Invalid, (await _svc.SetUserActiveAsync(_owner.Id, _owner.Id, false, Ctx)).Outcome);
    }

    [Fact]
    public async Task SetUserActive_CustomerUserByStaffAdmin_Ok()
    {
        var result = await _svc.SetUserActiveAsync(_admin.Id, _custUser.Id, false, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.False((await _users.GetAsync(_custUser.Id))!.Active);
    }

    [Fact]
    public async Task TestTarget_Success_ReportsReachable_WithKindNoun()
    {
        var share = await _svc.TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx);
        var blob = Target(_alpha.Id, "Blob", "blobstore", "blob-one") with { Kind = StorageKind.BlobContainer };
        await _targets.UpsertAsync(blob);
        var container = await _svc.TestTargetAsync(_admin.Id, blob.Id, Ctx);

        Assert.Equal("Reachable — the portal can list this file share.", share.Value);
        Assert.Equal("Reachable — the portal can list this container.", container.Value);
    }

    [Fact]
    public async Task TestTarget_BackendFailure_ReturnsUserMessage_AndAuditsFailed()
    {
        _fake.ThrowNotAuthorised = true;

        var result = await _svc.TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(StorageErrorMapper.NotAuthorisedMessage, result.Message);
        Assert.Equal(AuditResult.Failed, Assert.Single(await AuditAsync(AdminService.TestTargetOp)).Result);
    }

    [Fact]
    public async Task TestTarget_ByStaffRole_Denied_AndProbeNotCalled()
    {
        var result = await _svc.TestTargetAsync(_staff.Id, _alphaTarget.Id, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("Probe"));
    }

    [Fact]
    public async Task EveryMutation_WritesAuditRow()
    {
        var customer = (await _svc.SaveCustomerAsync(_admin.Id, new Customer(Guid.Empty, "Charlie", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired), Ctx)).Value!;
        var user = (await _svc.AddCustomerUserAsync(_admin.Id, customer.Id, "c@charlie.example", "C", Ctx)).Value!;
        await _svc.SetUserActiveAsync(_admin.Id, user.Id, false, Ctx);
        var target = (await _svc.SaveTargetAsync(_admin.Id, Target(customer.Id, "Charlie share", "charliestore", "charlie-one"), Ctx)).Value!;
        await _svc.TestTargetAsync(_admin.Id, target.Id, Ctx);
        await _svc.SetGrantAsync(_admin.Id, customer.Id, target.Id, AccessLevel.Read, Ctx);
        await _svc.SetStaffRoleAsync(_owner.Id, _staff.Id, StaffRole.StaffAdmin, Ctx);

        foreach (var op in new[] { AdminService.SaveCustomerOp, AdminService.AddCustomerUserOp, AdminService.SetUserActiveOp,
                     AdminService.SaveTargetOp, AdminService.TestTargetOp, AdminService.SetGrantOp, AdminService.SetStaffRoleOp })
        {
            var row = Assert.Single(await AuditAsync(op));
            Assert.Equal(AuditResult.Success, row.Result);
            Assert.Equal("10.0.0.9", row.SourceIp);
            Assert.Equal("corr-admin", row.CorrelationId);
        }
        Assert.Equal("Admin.SetGrant", AdminService.SetGrantOp);
        var grant = Assert.Single(await AuditAsync(AdminService.SetGrantOp));
        Assert.Equal("Charlie → Charlie share = Read", grant.Path);
        Assert.Equal(_admin.Upn, grant.UserDisplay);
        Assert.Equal(customer.Id, grant.CustomerId);
        Assert.Equal(target.Id, grant.TargetId);
    }

    [Fact]
    public async Task ValidationFailure_WritesNoAuditRow()
    {
        await _svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "T", "AB", "container-one"), Ctx);

        Assert.Empty(await AuditAsync(AdminService.SaveTargetOp));
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tx", "'\tx")]
    [InlineData("\rx", "\"'\rx\"")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("", "")]
    public void AuditCsv_FormulaPrefix_Escaped(string input, string expected) =>
        Assert.Equal(expected, CsvCell.Escape(input));

    [Fact]
    public void AuditCsv_Null_IsEmpty() => Assert.Equal("", CsvCell.Escape(null));

    [Fact]
    public void AuditCsv_Document_HasHeaderAndEscapedRows()
    {
        var evt = new AuditEvent(Guid.NewGuid(), Now, null, "=evil@x.com", _alpha.Id, _alphaTarget.Id, "Download", "a,b.txt", 12,
            AuditResult.Failed, "boom", "1.2.3.4", "corr");

        var csv = CsvCell.AuditCsv(new[] { evt },
            new Dictionary<Guid, string> { [_alpha.Id] = "Alpha" }, new Dictionary<Guid, string> { [_alphaTarget.Id] = "Alpha share" });

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Time (UTC),User,Customer,Target,Operation,Path,Bytes,Result,Error,Source IP,Correlation ID", lines[0]);
        Assert.Equal("2026-11-20 00:00:00,'=evil@x.com,Alpha,Alpha share,Download,\"a,b.txt\",12,Failed,boom,1.2.3.4,corr", lines[1]);
    }

    [Fact]
    public void AuditFilters_ToQuery_ParsesAndRejects()
    {
        var q = AuditFilters.ToQuery(null, null, null, " Download ", "denied", "2026-11-01", "2026-11-02", 500)!;
        Assert.Equal("Download", q.Operation);
        Assert.Equal(AuditResult.Denied, q.Result);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), q.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero).AddTicks(-1), q.To);

        Assert.Null(AuditFilters.ToQuery(null, null, null, null, "bogus", null, null, 500));
        Assert.Null(AuditFilters.ToQuery(null, null, null, null, null, "11/01/2026", null, 500));
        Assert.NotNull(AuditFilters.ToQuery(null, null, null, null, "", "", "", 500));
    }
}
