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
        _svc = Build(new Dictionary<string, string?>
        {
            ["Entra:StaffEmailDomain"] = "advancedutility.com",
            ["Storage:AllowedEndpointSuffixes:0"] = "core.windows.net",
            ["Storage:AllowedEndpointSuffixes:1"] = "core.usgovcloudapi.net",
        });

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
        var container = await _svc.TestTargetAsync(_owner.Id, blob.Id, Ctx);

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
    [InlineData("=SUM(A1)", "\"'=SUM(A1)\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-1", "\"'-1\"")]
    [InlineData("@cmd", "\"'@cmd\"")]
    [InlineData("\tx", "\"'\tx\"")]
    [InlineData("\rx", "\"'\rx\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("", "\"\"")]
    public void AuditCsv_FormulaPrefix_Escaped(string input, string expected) =>
        Assert.Equal(expected, CsvCell.Escape(input));

    [Fact]
    public void AuditCsv_Null_IsEmpty() => Assert.Equal("\"\"", CsvCell.Escape(null));

    [Fact]
    public void AuditCsv_Document_HasHeaderAndEscapedRows()
    {
        var evt = new AuditEvent(Guid.NewGuid(), Now, null, "=evil@x.com", _alpha.Id, _alphaTarget.Id, "Download", "a,b.txt", 12,
            AuditResult.Failed, "boom", "1.2.3.4", "corr");

        var csv = CsvCell.AuditCsv(new[] { evt },
            new Dictionary<Guid, string> { [_alpha.Id] = "Alpha" }, new Dictionary<Guid, string> { [_alphaTarget.Id] = "Alpha share" });

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("\"Time (UTC)\",\"User\",\"Customer\"", lines[0]);
        Assert.Equal("\"2026-11-20 00:00:00\",\"'=evil@x.com\",\"Alpha\",\"Alpha share\",\"Download\",\"a,b.txt\",\"12\",\"Failed\",\"boom\",\"1.2.3.4\",\"corr\"", lines[1]);
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

    private AdminService WithOwners(string owners) =>
        new(_users, _customers, _targets, _grants, _fake, new AuditService(_audit, NullLogger<AuditService>.Instance),
            new OwnerConfig(owners), new AppRoleHandlerTests.FixedTime(Now), new ConfigurationBuilder().Build());

    [Theory]
    [InlineData("a\u0000b@x.example")]
    [InlineData("a​b@x.example")]
    [InlineData("a\u0085b@x.example")]
    [InlineData("a\tb@x.example")]
    [InlineData("a b@x.example")]
    [InlineData("=a@x.example")]
    [InlineData("+a@x.example")]
    [InlineData("a@=x.example")]
    [InlineData("a@-x.example")]
    public async Task AddCustomerUser_HiddenOrFormulaCharacters_Invalid(string upn)
    {
        var result = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, upn, "", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task AddCustomerUser_UpnOfExistingStaffRecordInOtherDomain_Invalid()
    {
        await _users.UpsertAsync(Staff("contractor@partner.example", StaffRole.Staff));

        var result = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, "Contractor@Partner.example", "", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(UserKind.Staff, (await _users.FindByUpnAsync("contractor@partner.example"))!.Kind);
    }

    [Fact]
    public async Task SetStaffRole_ConfigOwnerTarget_Refused()
    {
        var second = Staff("owner2@advancedutility.com", StaffRole.Owner);
        await _users.UpsertAsync(second);

        var result = await WithOwners(OwnerUpn + ",owner2@advancedutility.com").SetStaffRoleAsync(_owner.Id, second.Id, StaffRole.Staff, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(StaffRole.Owner, (await _users.GetAsync(second.Id))!.StaffRole);
    }

    [Fact]
    public async Task SetUserActive_ConfigOwnerTarget_Refused()
    {
        var second = Staff("owner2@advancedutility.com", StaffRole.Owner);
        await _users.UpsertAsync(second);

        var result = await WithOwners(OwnerUpn + ",owner2@advancedutility.com").SetUserActiveAsync(_owner.Id, second.Id, false, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.True((await _users.GetAsync(second.Id))!.Active);
    }

    [Fact]
    public async Task SaveTarget_RepointStorageWithGrants_Invalid()
    {
        await _grants.SetAsync(new Grant(_alpha.Id, _alphaTarget.Id, AccessLevel.Read, "x", Now));

        foreach (var changed in new[]
                 {
                     _alphaTarget with { AccountName = "otheraccount" },
                     _alphaTarget with { ContainerName = "other-share" },
                     _alphaTarget with { Kind = StorageKind.BlobContainer },
                     _alphaTarget with { EndpointSuffix = "core.usgovcloudapi.net" },
                 })
        {
            var result = await _svc.SaveTargetAsync(_admin.Id, changed, Ctx);
            Assert.Equal(AccessOutcome.Invalid, result.Outcome);
            Assert.Equal("Remove this target's grants before pointing it at different storage.", result.Message);
        }
        Assert.Equal("alphastore", (await _targets.GetAsync(_alphaTarget.Id))!.AccountName);
    }

    [Fact]
    public async Task SaveTarget_RenameWithGrants_Ok()
    {
        await _grants.SetAsync(new Grant(_alpha.Id, _alphaTarget.Id, AccessLevel.Read, "x", Now));

        var result = await _svc.SaveTargetAsync(_admin.Id, _alphaTarget with { DisplayName = "Renamed", Active = false }, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task SaveTargetAndCustomer_StoreGuidsInDForm()
    {
        var sub = Guid.NewGuid();
        var target = await _svc.SaveTargetAsync(_admin.Id,
            Target(_alpha.Id, "G", "gstore", "g-one") with { SubscriptionId = sub.ToString("N").ToUpperInvariant() }, Ctx);
        var tenant = Guid.NewGuid();
        var customer = await _svc.SaveCustomerAsync(_admin.Id, _alpha with { HomeTenantId = tenant.ToString("N").ToUpperInvariant() }, Ctx);

        Assert.Equal(sub.ToString("D"), target.Value!.SubscriptionId);
        Assert.Equal(tenant.ToString("D"), customer.Value!.HomeTenantId);
    }

    [Fact]
    public async Task RecordExportAsync_WritesRowWithFilterAndCount()
    {
        await _svc.RecordExportAsync(_admin.Id, _admin.Upn, "operation=Download", 7, Ctx);

        var row = Assert.Single(await AuditAsync(AdminService.ExportAuditOp));
        Assert.Equal("operation=Download", row.Path);
        Assert.Equal(7, row.Bytes);
        Assert.Equal(_admin.Upn, row.UserDisplay);
    }

    [Fact]
    public void AuditCsv_EmbeddedLineBreakWithFormulaPrefix_QuotedAndPrefixed()
    {
        Assert.Equal("\"'=a\r\nb\"", CsvCell.Escape("=a\r\nb"));
        Assert.Equal("\"'@x\nsecond\"", CsvCell.Escape("@x\nsecond"));
    }

    // ---- Task 11 gap tests ----

    private static string Upn(int total)
    {
        const string domain = "@x.example";
        return new string('a', total - domain.Length) + domain;
    }

    [Fact]
    public void IsValidUpn_LoneSurrogate_False()
    {
        Assert.False(AdminService.IsValidUpn("a" + (char)0xD800 + "b@x.example", out _));
        Assert.False(AdminService.IsValidUpn("a" + (char)0xDC00 + "b@x.example", out _));
        Assert.False(AdminService.IsValidUpn("ab" + (char)0xD800 + "@x.example", out _));
    }

    [Theory]
    [InlineData("a�b@x.example", false)]
    [InlineData("ab@x.example", false)]
    [InlineData("-a@x.example", false)]
    [InlineData("a\U0001F600b@x.example", true)]
    [InlineData("ok@x.example", true)]
    public void IsValidUpn_SurrogatesPrivateUseAndFormulaStart(string upn, bool expected) =>
        Assert.Equal(expected, AdminService.IsValidUpn(upn, out _));

    [Fact]
    public void IsValidUpn_LengthLimitIs320Characters()
    {
        Assert.True(AdminService.IsValidUpn(Upn(320), out _));
        Assert.False(AdminService.IsValidUpn(Upn(321), out _));
    }

    [Theory]
    [InlineData("lone")]
    [InlineData("-a@x.example")]
    public async Task AddCustomerUser_LoneSurrogateOrFormulaStart_Invalid_NothingStored(string kind)
    {
        var upn = kind == "lone" ? "a" + (char)0xD800 + "b@x.example" : kind;
        var before = (await _users.ListAsync()).Count;

        var result = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, upn, "", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(before, (await _users.ListAsync()).Count);
        Assert.Empty(await AuditAsync(AdminService.AddCustomerUserOp));
    }

    [Fact]
    public async Task AddCustomerUser_MissingCustomer_NotFound()
    {
        var result = await _svc.AddCustomerUserAsync(_admin.Id, Guid.NewGuid(), "x@y.example", "", Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        Assert.Null(await _users.FindByUpnAsync("x@y.example"));
    }

    [Fact]
    public async Task AddCustomerUser_DisplayNameLimit_200Ok_201Invalid()
    {
        var ok = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, "d200@y.example", new string('n', 200), Ctx);
        var bad = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, "d201@y.example", new string('n', 201), Ctx);

        Assert.Equal(AccessOutcome.Ok, ok.Outcome);
        Assert.Equal(AccessOutcome.Invalid, bad.Outcome);
        Assert.Null(await _users.FindByUpnAsync("d201@y.example"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task SetStaffRole_UndefinedRole_Invalid_RoleUnchanged(int role)
    {
        var result = await _svc.SetStaffRoleAsync(_owner.Id, _staff.Id, (StaffRole)role, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(StaffRole.Staff, (await _users.GetAsync(_staff.Id))!.StaffRole);
        Assert.Empty(await AuditAsync(AdminService.SetStaffRoleOp));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task SetGrant_UndefinedLevel_Invalid_NoGrantStored(int level)
    {
        var result = await _svc.SetGrantAsync(_admin.Id, _alpha.Id, _alphaTarget.Id, (AccessLevel)level, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Null(await _grants.GetAsync(_alpha.Id, _alphaTarget.Id));
        Assert.Empty(await AuditAsync(AdminService.SetGrantOp));
    }

    [Fact]
    public async Task SaveTarget_MoveToInternalWithGrants_Invalid()
    {
        await _grants.SetAsync(new Grant(_alpha.Id, _alphaTarget.Id, AccessLevel.Read, "x", Now));

        var byAdmin = await _svc.SaveTargetAsync(_admin.Id, _alphaTarget with { CustomerId = null }, Ctx);
        var byOwner = await WithOwners(OwnerUpn).SaveTargetAsync(_owner.Id, _alphaTarget with { CustomerId = null }, Ctx);

        Assert.Equal(AccessOutcome.Denied, byAdmin.Outcome);
        Assert.Equal(AccessOutcome.Invalid, byOwner.Outcome);
        Assert.Equal(_alpha.Id, (await _targets.GetAsync(_alphaTarget.Id))!.CustomerId);
        Assert.NotNull(await _grants.GetAsync(_alpha.Id, _alphaTarget.Id));
    }

    [Fact]
    public async Task SetUserActive_StaffTargetByOwnerNotInConfig_Denied_SingleDeniedRow()
    {
        var gone = Staff("gone@advancedutility.com", StaffRole.Owner);
        await _users.UpsertAsync(gone);

        var result = await _svc.SetUserActiveAsync(gone.Id, _staff.Id, false, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.True((await _users.GetAsync(_staff.Id))!.Active);
        var row = Assert.Single(await AuditAsync(AdminService.SetUserActiveOp));
        Assert.Equal(AuditResult.Denied, row.Result);
    }

    private async Task<AccessOutcome> InvokeAsync(string op, Guid actor)
    {
        static async Task<AccessOutcome> O(Task<AccessResult> t) => (await t).Outcome;
        static async Task<AccessOutcome> OT<T>(Task<AccessResult<T>> t) => (await t).Outcome;
        return op switch
        {
            AdminService.SaveCustomerOp => await OT(_svc.SaveCustomerAsync(actor,
                new Customer(Guid.Empty, "Zed", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired), Ctx)),
            AdminService.AddCustomerUserOp => await OT(_svc.AddCustomerUserAsync(actor, _alpha.Id, "new@alpha.example", "", Ctx)),
            AdminService.SetUserActiveOp => await O(_svc.SetUserActiveAsync(actor, _custUser.Id, false, Ctx)),
            AdminService.SaveTargetOp => await OT(_svc.SaveTargetAsync(actor, Target(_alpha.Id, "New", "newstore", "new-one"), Ctx)),
            AdminService.TestTargetOp => await OT(_svc.TestTargetAsync(actor, _alphaTarget.Id, Ctx)),
            AdminService.SetGrantOp => await O(_svc.SetGrantAsync(actor, _alpha.Id, _alphaTarget.Id, AccessLevel.Read, Ctx)),
            AdminService.SetStaffRoleOp => await O(_svc.SetStaffRoleAsync(actor, _staff.Id, StaffRole.StaffAdmin, Ctx)),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
    }

    private async Task<string> SnapshotAsync()
    {
        var users = (await _users.ListAsync()).OrderBy(u => u.Id).Select(u => u.ToString());
        var customers = (await _customers.ListAsync()).OrderBy(c => c.Id).Select(c => c.ToString());
        var targets = (await _targets.ListAsync()).OrderBy(t => t.Id).Select(t => t.ToString());
        var grants = (await _grants.ListForCustomerAsync(_alpha.Id)).Concat(await _grants.ListForCustomerAsync(_bravo.Id)).Select(g => g.ToString());
        return string.Join("\n", users.Concat(customers).Concat(targets).Concat(grants));
    }

    [Theory]
    [InlineData(AdminService.SaveCustomerOp, false)]
    [InlineData(AdminService.AddCustomerUserOp, false)]
    [InlineData(AdminService.SetUserActiveOp, false)]
    [InlineData(AdminService.SaveTargetOp, false)]
    [InlineData(AdminService.TestTargetOp, false)]
    [InlineData(AdminService.SetGrantOp, false)]
    [InlineData(AdminService.SetStaffRoleOp, false)]
    [InlineData(AdminService.SaveCustomerOp, true)]
    [InlineData(AdminService.AddCustomerUserOp, true)]
    [InlineData(AdminService.SetUserActiveOp, true)]
    [InlineData(AdminService.SaveTargetOp, true)]
    [InlineData(AdminService.TestTargetOp, true)]
    [InlineData(AdminService.SetGrantOp, true)]
    [InlineData(AdminService.SetStaffRoleOp, true)]
    public async Task Mutation_ByStaffRoleOrUnknownActor_DeniedWithExactlyOneDeniedRow(string op, bool unknownActor)
    {
        var actor = unknownActor ? Guid.NewGuid() : _staff.Id;
        var before = await SnapshotAsync();

        var outcome = await InvokeAsync(op, actor);

        Assert.Equal(AccessOutcome.Denied, outcome);
        Assert.Equal(before, await SnapshotAsync());
        var row = Assert.Single(await AuditAsync());
        Assert.Equal(op, row.Operation);
        Assert.Equal(AuditResult.Denied, row.Result);
        Assert.Equal(unknownActor ? "" : _staff.Upn, row.UserDisplay);
        Assert.Equal("10.0.0.9", row.SourceIp);
        Assert.Equal("corr-admin", row.CorrelationId);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("Probe"));
    }

    [Fact]
    public async Task SaveTarget_Boundaries_And_Normalisation()
    {
        async Task<AccessOutcome> Save(Func<StorageTarget, StorageTarget> edit, string container)
        {
            var t = edit(Target(_alpha.Id, "T", "acct" + Guid.NewGuid().ToString("N")[..8], container));
            return (await _svc.SaveTargetAsync(_admin.Id, t, Ctx)).Outcome;
        }

        Assert.Equal(AccessOutcome.Ok, (await _svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "T", "abc", "abc"), Ctx)).Outcome);
        Assert.Equal(AccessOutcome.Ok, (await _svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "T", new string('a', 24), "abc"), Ctx)).Outcome);
        Assert.Equal(AccessOutcome.Ok, await Save(t => t, new string('c', 63)));
        Assert.Equal(AccessOutcome.Invalid, await Save(t => t, new string('c', 64)));
        Assert.Equal(AccessOutcome.Ok, await Save(t => t with { DisplayName = new string('d', 100) }, "disp-ok"));
        Assert.Equal(AccessOutcome.Invalid, await Save(t => t with { DisplayName = new string('d', 101) }, "disp-bad"));
        Assert.Equal(AccessOutcome.Invalid, await Save(t => t with { ResourceGroup = "" }, "rg-empty"));
        Assert.Equal(AccessOutcome.Invalid, await Save(t => t with { ResourceGroup = "rg/x" }, "rg-slash"));
        Assert.Equal(AccessOutcome.Ok, await Save(t => t with { ResourceGroup = new string('r', 90) }, "rg-90"));
        Assert.Equal(AccessOutcome.Invalid, await Save(t => t with { ResourceGroup = new string('r', 91) }, "rg-91"));
        Assert.Equal(AccessOutcome.Invalid, await Save(t => t with { Kind = (StorageKind)9 }, "kind-bad"));
    }

    [Fact]
    public async Task SaveTarget_DeletedCustomer_Invalid()
    {
        var result = await _svc.SaveTargetAsync(_admin.Id, Target(Guid.NewGuid(), "T", "ghoststore", "ghost-one"), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal("That customer no longer exists.", result.Message);
    }

    [Fact]
    public async Task SaveTarget_SuffixIsTrimmedAndLowercased()
    {
        var result = await _svc.SaveTargetAsync(_admin.Id,
            Target(_alpha.Id, "T", "casestore", "case-one") with { EndpointSuffix = " CORE.Windows.NET " }, Ctx);

        Assert.Equal("core.windows.net", result.Value!.EndpointSuffix);
        Assert.Equal("core.windows.net", (await _targets.GetAsync(result.Value.Id))!.EndpointSuffix);
    }

    [Fact]
    public void AllowedEndpointSuffixes_ConfiguredEntriesNormalised()
    {
        var svc = Build(new Dictionary<string, string?>
        {
            ["Storage:AllowedEndpointSuffixes:0"] = " Foo.Example ",
            ["Storage:AllowedEndpointSuffixes:1"] = "FOO.example",
            ["Storage:AllowedEndpointSuffixes:2"] = "  ",
        });

        Assert.Equal(new[] { "foo.example" }, svc.AllowedEndpointSuffixes);
    }

    [Fact]
    public void AllowedEndpointSuffixes_NoUsableEntries_FallsBackToDefaults()
    {
        var blank = Build(new Dictionary<string, string?> { ["Storage:AllowedEndpointSuffixes:0"] = " " });
        var none = Build(new Dictionary<string, string?>());

        var defaults = new[] { AdminService.DefaultEndpointSuffix };
        Assert.Equal(defaults, blank.AllowedEndpointSuffixes);
        Assert.Equal(defaults, none.AllowedEndpointSuffixes);
    }

    [Fact]
    public async Task SaveCustomer_Boundaries_And_Enums()
    {
        Customer New(string name, string notes = "", CustomerCloud cloud = CustomerCloud.Commercial,
            CrossTenantStatus status = CrossTenantStatus.NotRequired) => new(Guid.Empty, name, true, notes, null, cloud, status);
        async Task<AccessOutcome> Save(Customer c) => (await _svc.SaveCustomerAsync(_admin.Id, c, Ctx)).Outcome;

        Assert.Equal(AccessOutcome.Ok, await Save(New(new string('n', 200))));
        Assert.Equal(AccessOutcome.Invalid, await Save(New(new string('m', 201))));
        Assert.Equal(AccessOutcome.Ok, await Save(New("Notes ok", new string('x', 2000))));
        Assert.Equal(AccessOutcome.Invalid, await Save(New("Notes bad", new string('x', 2001))));
        Assert.Equal(AccessOutcome.Invalid, await Save(New("Cloud bad", cloud: (CustomerCloud)5)));
        Assert.Equal(AccessOutcome.Invalid, await Save(New("Status bad", status: (CrossTenantStatus)7)));
        var gov = await _svc.SaveCustomerAsync(_admin.Id, New("Gov done", cloud: CustomerCloud.AzureGov, status: CrossTenantStatus.Complete), Ctx);
        Assert.Equal(CrossTenantStatus.Complete, gov.Value!.CrossTenantStatus);
    }

    [Fact]
    public async Task SaveCustomer_DuplicateName_Invalid_WritesNoRow()
    {
        var result = await _svc.SaveCustomerAsync(_admin.Id,
            new Customer(Guid.Empty, "Alpha", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(2, (await _customers.ListAsync()).Count);
        Assert.Empty(await AuditAsync(AdminService.SaveCustomerOp));
    }

    [Fact]
    public async Task TestTarget_MissingTarget_NotFound_NoProbe()
    {
        var result = await _svc.TestTargetAsync(_admin.Id, Guid.NewGuid(), Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("Probe"));
        Assert.Empty(await AuditAsync(AdminService.TestTargetOp));
    }

    [Fact]
    public async Task TestTarget_CallerCancelled_Propagates_NoFailedRow()
    {
        using var cts = new CancellationTokenSource();
        _fake.FailOnCall = c => c.StartsWith("Probe") ? new OperationCanceledException(cts.Token) : null;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _svc.TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx, cts.Token));

        Assert.Empty(await AuditAsync(AdminService.TestTargetOp));
    }

    [Fact]
    public async Task TestTarget_TimeoutNotCallerCancel_Unavailable_OneFailedRow()
    {
        _fake.FailOnCall = c => c.StartsWith("Probe") ? new OperationCanceledException() : null;

        var result = await _svc.TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.Equal(AuditResult.Failed, Assert.Single(await AuditAsync(AdminService.TestTargetOp)).Result);
    }

    [Fact]
    public void AdminAlert_For_MapsOutcomes()
    {
        var ok = AdminAlert.For(AccessOutcome.Ok, null, "Done.");
        Assert.Equal("Done.", ok.Text);
        Assert.False(ok.IsError);
        Assert.Equal("Custom", AdminAlert.For(AccessOutcome.Ok, "Custom").Text);
        Assert.Equal("Saved.", AdminAlert.For(AccessOutcome.Ok, "").Text);

        foreach (var o in new[] { AccessOutcome.Unavailable, AccessOutcome.Busy, AccessOutcome.Conflict })
        {
            Assert.Equal("alert--warning", AdminAlert.For(o, "m").Css);
            Assert.Equal("m", AdminAlert.For(o, "m").Text);
            Assert.Equal(AdminAlert.StoreDownMessage, AdminAlert.For(o, null).Text);
            Assert.True(AdminAlert.For(o, "m").IsError);
        }
        foreach (var o in new[] { AccessOutcome.Denied, AccessOutcome.NotFound, AccessOutcome.Invalid, AccessOutcome.AlreadyExists })
        {
            Assert.Equal("alert--danger", AdminAlert.For(o, "m").Css);
            Assert.Equal("That didn't work.", AdminAlert.For(o, null).Text);
        }
    }

    [Fact]
    public void AuditFilters_EdgeInputs()
    {
        Assert.NotNull(AuditFilters.ToQuery(null, null, null, "   ", "  ", " 2026-11-01 ", null, 5));
        Assert.Null(AuditFilters.ToQuery(null, null, null, null, "7", null, null, 5));
        Assert.Null(AuditFilters.ToQuery(null, null, null, null, null, "2026-02-30", null, 5));
        Assert.Null(AuditFilters.ToQuery(null, null, null, null, null, null, "2026-11-1", 5));
        Assert.Equal(AuditResult.Success, AuditFilters.ToQuery(null, null, null, null, " success ", null, null, 5)!.Result);
        Assert.Null(AuditFilters.ToQuery(null, null, null, "  ", null, null, null, 5)!.Operation);
        Assert.True(AuditFilters.TryParseDate("  ", out var none) && none is null);
        Assert.Equal(5, AuditFilters.ToQuery(null, null, null, null, null, null, null, 5)!.Limit);
    }

    [Fact]
    public void AuditFilters_MaxDateTo_DoesNotThrow()
    {
        var q = AuditFilters.ToQuery(null, null, null, null, null, null, "9999-12-31", 5);

        Assert.NotNull(q);
        Assert.NotNull(q!.To);
    }

    [Fact]
    public async Task SaveTarget_DefaultSuffixes_RefuseGovStorage()
    {
        var svc = Build(new Dictionary<string, string?>());

        var result = await svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "Gov", "govaccount", "gov-one") with { EndpointSuffix = "core.usgovcloudapi.net" }, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task SaveTarget_SubscriptionNotInAllowList_Invalid_InListOk_EmptyAny()
    {
        var allowed = Guid.NewGuid();
        var svc = Build(new Dictionary<string, string?> { ["Storage:AllowedSubscriptions:0"] = allowed.ToString() });

        var bad = await svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "T", "goodaccount", "container-one"), Ctx);
        var good = await svc.SaveTargetAsync(_admin.Id, Target(_alpha.Id, "T", "goodaccount", "container-one") with { SubscriptionId = allowed.ToString().ToUpperInvariant() }, Ctx);
        var open = await Build(new Dictionary<string, string?>()).SaveTargetAsync(_admin.Id, Target(_alpha.Id, "T2", "goodaccount2", "container-two"), Ctx);

        Assert.Equal(AccessOutcome.Invalid, bad.Outcome);
        Assert.Equal(AccessOutcome.Ok, good.Outcome);
        Assert.Equal(AccessOutcome.Ok, open.Outcome);
    }

    [Fact]
    public async Task SaveTarget_NewInternalTarget_StaffAdminDenied_OwnerOk()
    {
        var internalTarget = Target(null, "Internal", "internalstore", "internal-one");

        var byAdmin = await _svc.SaveTargetAsync(_admin.Id, internalTarget, Ctx);
        var byOwner = await WithOwners(OwnerUpn).SaveTargetAsync(_owner.Id, internalTarget, Ctx);

        Assert.Equal(AccessOutcome.Denied, byAdmin.Outcome);
        Assert.Equal(AdminService.InternalOwnerOnlyMessage, byAdmin.Message);
        Assert.Equal(AccessOutcome.Ok, byOwner.Outcome);
    }

    [Fact]
    public async Task SaveTarget_MoveExistingToInternal_StaffAdminDenied()
    {
        var result = await _svc.SaveTargetAsync(_admin.Id, _alphaTarget with { CustomerId = null }, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Equal(_alpha.Id, (await _targets.GetAsync(_alphaTarget.Id))!.CustomerId);
    }

    [Fact]
    public async Task SaveTarget_EditExistingInternal_ByStaffAdmin_Allowed()
    {
        var internalTarget = Target(null, "Internal", "internalstore", "internal-one");
        await _targets.UpsertAsync(internalTarget);

        var rename = await _svc.SaveTargetAsync(_admin.Id, internalTarget with { DisplayName = "Renamed" }, Ctx);

        Assert.Equal(AccessOutcome.Ok, rename.Outcome);
    }

    [Fact]
    public async Task SetUserActive_RacingSignIn_CannotReactivate()
    {
        // The admission read the user while active; the admin then deactivates; the admission's write must not win.
        var user = await _users.GetAsync(_custUser.Id);
        await _svc.SetUserActiveAsync(_admin.Id, _custUser.Id, false, Ctx);

        var recorded = await _users.RecordSignInAsync(user!.Id, "oid-pat", user.Upn, user.DisplayName, Now);

        Assert.Null(recorded);
        Assert.False((await _users.GetAsync(_custUser.Id))!.Active);
    }

    [Fact]
    public async Task SetStaffRole_RacingSignIn_RoleSurvives()
    {
        await _svc.SetStaffRoleAsync(_owner.Id, _staff.Id, StaffRole.None, Ctx);

        var recorded = await _users.RecordSignInAsync(_staff.Id, "oid-" + _staff.Upn, _staff.Upn, "Sam", Now);

        Assert.Equal(StaffRole.None, recorded!.StaffRole);
        Assert.Equal(StaffRole.None, (await _users.GetAsync(_staff.Id))!.StaffRole);
    }

    [Fact]
    public async Task SetStaffRole_StoreRefusesNonStaff_NotFound()
    {
        Assert.False(await _users.SetStaffRoleAsync(_custUser.Id, StaffRole.Staff));
        Assert.False(await _users.SetActiveAsync(Guid.NewGuid(), false));
    }

    [Theory]
    [InlineData("report;=HYPERLINK(\"x\")", "\"report;'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("a,+1", "\"a,'+1\"")]
    [InlineData("a| -2", "\"a| '-2\"")]
    [InlineData("a;  @b", "\"a;  '@b\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("x-y+z=w@q", "\"x-y+z=w@q\"")]
    [InlineData("", "\"\"")]
    public void CsvCell_NeutralisesAfterSeparators_AndQuotesEveryCell(string input, string expected) =>
        Assert.Equal(expected, CsvCell.Escape(input));

    [Fact]
    public void AuditCsv_Truncated_AddsFinalLine()
    {
        var csv = CsvCell.AuditCsv(Array.Empty<AuditEvent>(), new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), 5000);

        Assert.EndsWith("# truncated at 5000 rows — narrow the filters to see more\r\n", csv);
        Assert.Equal("audit-20261120-0905-truncated.csv", CloudSelfService.Endpoints.AdminEndpoints.FileNameFor(new DateTimeOffset(2026, 11, 20, 9, 5, 0, TimeSpan.Zero), true));
    }

    [Fact]
    public async Task AddCustomerUser_NormalisedStaffDomain_OrConfigOwner_Invalid()
    {
        var dotted = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, "x@advancedut\u0130lity.com", "", Ctx);
        var wide = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, "x@\uFF41dvancedutility.com", "", Ctx);
        var owner = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, "OWNER@advancedutility.com", "", Ctx);
        var external = await WithOwners("boss@partner.example").AddCustomerUserAsync(_admin.Id, _alpha.Id, "Boss@Partner.Example", "", Ctx);

        Assert.All(new[] { dotted, wide, owner, external }, r => Assert.Equal(AccessOutcome.Invalid, r.Outcome));
    }

    [Theory]
    [InlineData("pat@d\u00F6main.example")]
    [InlineData("pat@\u212Aelvin.example")]
    public async Task AddCustomerUser_NonAsciiDomain_Invalid(string upn)
    {
        var result = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, upn, "", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void UpnNormalizer_FoldsKelvinAndCompatibilityForms()
    {
        Assert.Equal("kelvin@x.example", UpnNormalizer.Normalize("\u212Aelvin@X.example"));
        Assert.Equal("a@b.example", UpnNormalizer.Normalize("\uFF21@B.example"));
        Assert.Equal("", UpnNormalizer.Normalize(null));
    }

    [Fact]
    public async Task InMemoryUserStore_FindByUpn_UsesNormalisedForm()
    {
        await _users.UpsertAsync(Staff("k@x.example", StaffRole.None));

        Assert.NotNull(await _users.FindByUpnAsync("K@X.example"));
        Assert.NotNull(await _users.FindByUpnAsync("\u212A@x.example"));
    }

    [Fact]
    public async Task TestTarget_SecondCallWithinCooldown_Busy_NoSecondProbe()
    {
        await _svc.TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx);

        var second = await _svc.TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx);

        Assert.Equal(AccessOutcome.Busy, second.Outcome);
        Assert.Equal(1, _fake.Calls.Count(c => c.StartsWith("Probe")));
    }

    [Fact]
    public async Task TestTarget_HungProbe_TimesOutWithMessage()
    {
        _fake.FailOnCall = c => c.StartsWith("Probe") ? new OperationCanceledException() : null;
        _fake.FailOnCall = c =>
        {
            if (!c.StartsWith("Probe")) return null;
            Thread.Sleep(400);
            return new OperationCanceledException();
        };
        var svc = Build(new Dictionary<string, string?> { ["Admin:TestAccessTimeoutSeconds"] = "0.05" });

        var timedOutByUs = await svc.TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, timedOutByUs.Outcome);
        Assert.Equal(AdminService.TestTimeoutMessage, timedOutByUs.Message);
        Assert.Equal(AdminService.TestTimeoutMessage, Assert.Single(await AuditAsync(AdminService.TestTargetOp)).Error);
    }

    [Fact]
    public void OwnerConfig_Upns_IsACopy_AndMatchesNormalised()
    {
        var owners = new OwnerConfig("Boss@x.example");
        var copy = (string[])owners.Upns;
        copy[0] = "attacker@x.example";

        Assert.True(owners.IsOwner("boss@X.EXAMPLE"));
        Assert.False(owners.IsOwner("attacker@x.example"));
        Assert.True(owners.IsOwner("\u212Aoss@x.example") == false);
    }

    [Theory]
    [InlineData("https://sts.windows.net/11111111-2222-3333-4444-555555555555/", true)]
    [InlineData("https://login.microsoftonline.us/11111111-2222-3333-4444-555555555555/v2.0", true)]
    [InlineData("live.com", false)]
    [InlineData("http://sts.windows.net/11111111-2222-3333-4444-555555555555/", false)]
    [InlineData("https://sts.windows.net/not-a-guid/", false)]
    [InlineData("", false)]
    public void HomeTenantOf_ParsesIdpClaim(string idp, bool parsed)
    {
        var id = new EntraIdentity("o", "t", "u@x.example", "U", IsGuest: true) { Idp = idp };

        Assert.Equal(parsed, IdentityResolver.HomeTenantOf(id) is not null);
        if (parsed) Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), IdentityResolver.HomeTenantOf(id));
    }

    [Fact]
    public void HomeTenantOf_Member_Null() =>
        Assert.Null(IdentityResolver.HomeTenantOf(new EntraIdentity("o", "t", "u@x", "U", IsGuest: false) { Idp = "https://sts.windows.net/11111111-2222-3333-4444-555555555555/" }));

    [Fact]
    public void HomeTenantOf_Member_UsesTid() =>
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"),
            IdentityResolver.HomeTenantOf(new EntraIdentity("o", "11111111-2222-3333-4444-555555555555", "u@x", "U", IsGuest: false)));

    [Theory]
    [InlineData("prod-sub")]
    [InlineData("11111111-2222-3333-4444-55555555555")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void AllowedSubscriptions_NonGuidEntry_FailsFast(string entry)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:AllowedSubscriptions:0"] = Guid.NewGuid().ToString(),
            ["Storage:AllowedSubscriptions:1"] = entry,
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => AdminService.ParseAllowedSubscriptions(config));
        Assert.Contains("Storage:AllowedSubscriptions contains an invalid subscription id: " + entry, ex.Message);
        Assert.Throws<InvalidOperationException>(() => Build(new Dictionary<string, string?> { ["Storage:AllowedSubscriptions:0"] = entry }));
    }

    [Fact]
    public void AllowedSubscriptions_BlankEntriesIgnored_ValidParsed()
    {
        var id = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:AllowedSubscriptions:0"] = " ",
            ["Storage:AllowedSubscriptions:1"] = " " + id.ToString("N").ToUpperInvariant() + " ",
        }).Build();

        Assert.Equal(new[] { id }, AdminService.ParseAllowedSubscriptions(config));
        Assert.Empty(AdminService.ParseAllowedSubscriptions(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task TestTarget_CooldownSharedAcrossAdminServiceInstances()
    {
        var shared = new ProbeCooldown(new AppRoleHandlerTests.FixedTime(Now));
        AdminService Make() => new(_users, _customers, _targets, _grants, _fake, new AuditService(_audit, NullLogger<AuditService>.Instance),
            new OwnerConfig(OwnerUpn), new AppRoleHandlerTests.FixedTime(Now), new ConfigurationBuilder().Build(), shared);

        var first = await Make().TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx);
        var secondCircuit = await Make().TestTargetAsync(_admin.Id, _alphaTarget.Id, Ctx);
        var otherActor = await Make().TestTargetAsync(_owner.Id, _alphaTarget.Id, Ctx);

        Assert.Equal(AccessOutcome.Ok, first.Outcome);
        Assert.Equal(AccessOutcome.Busy, secondCircuit.Outcome);
        Assert.Equal(AccessOutcome.Ok, otherActor.Outcome);
    }

    private sealed class MovingTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ProbeCooldown_AllowsAgainAfterWindow()
    {
        var clock = new MovingTime(Now);
        var cooldown = new ProbeCooldown(clock);
        var actor = Guid.NewGuid();

        Assert.True(cooldown.TryEnter(actor, TimeSpan.FromSeconds(5)));
        Assert.False(cooldown.TryEnter(actor, TimeSpan.FromSeconds(5)));
        clock.Now = Now.AddSeconds(6);
        Assert.True(cooldown.TryEnter(actor, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SaveTarget_ExistingInternal_HandedToCustomerOrRepointed_StaffAdminDenied_OwnerOk()
    {
        var internalTarget = Target(null, "Internal", "internalstore", "internal-one");
        await _targets.UpsertAsync(internalTarget);
        var owner = WithOwners(OwnerUpn);

        foreach (var changed in new[]
                 {
                     internalTarget with { CustomerId = _alpha.Id },
                     internalTarget with { AccountName = "otheraccount" },
                     internalTarget with { ContainerName = "other-share" },
                     internalTarget with { Kind = StorageKind.BlobContainer },
                     internalTarget with { EndpointSuffix = "core.usgovcloudapi.net" },
                 })
        {
            var byAdmin = await _svc.SaveTargetAsync(_admin.Id, changed, Ctx);
            Assert.Equal(AccessOutcome.Denied, byAdmin.Outcome);
            Assert.Equal(AdminService.InternalOwnerOnlyMessage, byAdmin.Message);
        }
        var stored = (await _targets.GetAsync(internalTarget.Id))!;
        Assert.Null(stored.CustomerId);
        Assert.Equal("internalstore", stored.AccountName);

        var handed = await owner.SaveTargetAsync(_owner.Id, internalTarget with { CustomerId = _alpha.Id }, Ctx);
        Assert.Equal(AccessOutcome.Ok, handed.Outcome);
    }

    [Fact]
    public async Task SaveTarget_ExistingInternal_DisplayNameAndActiveEdit_StaffAdminAllowed()
    {
        var internalTarget = Target(null, "Internal", "internalstore", "internal-one");
        await _targets.UpsertAsync(internalTarget);

        var result = await _svc.SaveTargetAsync(_admin.Id, internalTarget with { DisplayName = "New name", Active = false }, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal("New name", (await _targets.GetAsync(internalTarget.Id))!.DisplayName);
    }

    [Fact]
    public async Task AddCustomerUser_ConfigOwnerUpn_UsesOwnersMessage()
    {
        var result = await _svc.AddCustomerUserAsync(_admin.Id, _alpha.Id, OwnerUpn, "", Ctx);
        var external = await WithOwners("boss@partner.example").AddCustomerUserAsync(_admin.Id, _alpha.Id, "boss@partner.example", "", Ctx);

        Assert.Equal(AdminService.OwnersFromConfigMessage, result.Message);
        Assert.Equal(AdminService.OwnersFromConfigMessage, external.Message);
    }
}
