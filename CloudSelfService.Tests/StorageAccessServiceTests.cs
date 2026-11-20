using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

public class StorageAccessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly AuditContext Ctx = new("10.0.0.1", "corr-1");
    private const string OwnerUpn = "owner@advancedutility.com";

    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryCustomerStore _customers = new();
    private readonly InMemoryTargetStore _targets = new();
    private readonly InMemoryGrantStore _grants;
    private readonly InMemoryAuditStore _audit = new();
    private readonly FakeStorageBackend _fake = new();
    private readonly ListLogger _log = new();
    private readonly StorageAccessService _svc;

    private readonly Customer _custA = new(Guid.NewGuid(), "Alpha", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
    private readonly Customer _custB = new(Guid.NewGuid(), "Bravo", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
    private readonly StorageTarget _ta;
    private readonly StorageTarget _ta2;
    private readonly StorageTarget _tb;
    private readonly StorageTarget _internal;
    private readonly StorageTarget _inactive;
    private readonly AppUser _readUserA;
    private readonly AppUser _fullUserA;
    private readonly AppUser _userB;
    private readonly AppUser _staffNone;
    private readonly AppUser _staff;
    private readonly AppUser _staffAdmin;

    public StorageAccessServiceTests()
    {
        _grants = new InMemoryGrantStore(_targets);
        _ta = Target(_custA.Id, "A Share", StorageKind.FileShare, "ta");
        _ta2 = Target(_custA.Id, "B Ungranted", StorageKind.FileShare, "ta2");
        _tb = Target(_custB.Id, "Bravo Container", StorageKind.BlobContainer, "tb");
        _internal = Target(null, "Internal", StorageKind.BlobContainer, "internal");
        _inactive = Target(_custA.Id, "Retired", StorageKind.FileShare, "retired", active: false);

        _readUserA = User(UserKind.Customer, _custA.Id, "read@alpha.example", StaffRole.None);
        _fullUserA = User(UserKind.Customer, _custA.Id, "full@alpha.example", StaffRole.None);
        _userB = User(UserKind.Customer, _custB.Id, "pat@bravo.example", StaffRole.None);
        _staffNone = User(UserKind.Staff, null, "new@advancedutility.com", StaffRole.None);
        _staff = User(UserKind.Staff, null, "sam@advancedutility.com", StaffRole.Staff);
        _staffAdmin = User(UserKind.Staff, null, "ada@advancedutility.com", StaffRole.StaffAdmin);

        _customers.UpsertAsync(_custA).Wait();
        _customers.UpsertAsync(_custB).Wait();
        foreach (var t in new[] { _ta, _ta2, _tb, _internal, _inactive }) _targets.UpsertAsync(t).Wait();
        foreach (var u in new[] { _readUserA, _fullUserA, _userB, _staffNone, _staff, _staffAdmin }) _users.UpsertAsync(u).Wait();
        _grants.SetAsync(new Grant(_custA.Id, _ta.Id, AccessLevel.Full, "admin", Now)).Wait();
        _grants.SetAsync(new Grant(_custA.Id, _inactive.Id, AccessLevel.Full, "admin", Now)).Wait();
        _grants.SetAsync(new Grant(_custB.Id, _tb.Id, AccessLevel.Read, "admin", Now)).Wait();

        _svc = Service();
    }

    private StorageAccessService Service(IGrantStore? grants = null, IStorageBackendResolver? backends = null) =>
        new(_users, _customers, _targets, grants ?? _grants, backends ?? _fake,
            new AuditService(_audit, NullLogger<AuditService>.Instance), new OwnerConfig(OwnerUpn),
            new AppRoleHandlerTests.FixedTime(Now), _log);

    private static StorageTarget Target(Guid? customerId, string name, StorageKind kind, string container, bool active = true) =>
        new(Guid.NewGuid(), customerId, name, "sub", "rg", "acct", kind, container, "core.windows.net", active);

    private static AppUser User(UserKind kind, Guid? customerId, string upn, StaffRole role, bool active = true) =>
        new(Guid.NewGuid(), kind, customerId, upn, upn, "oid-" + upn, role, active, Now, null);

    // Grants are per customer, so the "Read user" and "Full user" at A are told apart by A's grant level.
    private Task GrantA(AccessLevel level) => _grants.SetAsync(new Grant(_custA.Id, _ta.Id, level, "admin", Now));

    private async Task<IReadOnlyList<AuditEvent>> Audits() => await _audit.QueryAsync(new AuditQuery());

    private Task Seed(StorageTarget t, params string[] folders) =>
        Task.WhenAll(folders.Select(f => _fake.CreateFolderAsync(t, f, default)));

    // ---- ListReachableTargetsAsync ----

    [Fact]
    public async Task ListReachable_CustomerUser_OnlyOwnGrantedTargets()
    {
        var result = await _svc.ListReachableTargetsAsync(_fullUserA.Id);

        var only = Assert.Single(result);
        Assert.Equal(_ta.Id, only.Target.Id);
        Assert.Equal(_custA.Id, only.Customer!.Id);
        Assert.Equal(AccessLevel.Full, only.Level);
    }

    [Fact]
    public async Task ListReachable_Staff_AllActiveTargets()
    {
        var result = await _svc.ListReachableTargetsAsync(_staff.Id);

        Assert.Equal(new[] { _internal.Id, _ta.Id, _ta2.Id, _tb.Id }, result.Select(r => r.Target.Id));
        Assert.All(result, r => Assert.Equal(AccessLevel.Contribute, r.Level));
        Assert.Equal(_custB.Id, result.Single(r => r.Target.Id == _tb.Id).Customer!.Id);
        Assert.Null(result.Single(r => r.Target.Id == _internal.Id).Customer);
    }

    [Fact]
    public async Task ListReachable_StaffRoleNone_Empty() =>
        Assert.Empty(await _svc.ListReachableTargetsAsync(_staffNone.Id));

    [Fact]
    public async Task ListReachable_CustomerUserWithOwnerStaffRole_OnlyOwnGrantedTargets()
    {
        var sneaky = User(UserKind.Customer, _custA.Id, OwnerUpn, StaffRole.Owner);
        await _users.UpsertAsync(sneaky);

        var result = await _svc.ListReachableTargetsAsync(sneaky.Id);

        Assert.Equal(_ta.Id, Assert.Single(result).Target.Id);
    }

    [Fact]
    public async Task ListReachable_InactiveCustomer_Empty()
    {
        await _customers.UpsertAsync(_custA with { Active = false });
        Assert.Empty(await _svc.ListReachableTargetsAsync(_fullUserA.Id));
    }

    [Fact]
    public async Task ListReachable_UnknownUser_Empty() =>
        Assert.Empty(await _svc.ListReachableTargetsAsync(Guid.NewGuid()));

    // ---- GetLevelAsync ----

    [Fact]
    public async Task GetLevel_DeconfiguredOwner_IsStaffAdminFull()
    {
        var formerOwner = User(UserKind.Staff, null, "gone@advancedutility.com", StaffRole.Owner);
        await _users.UpsertAsync(formerOwner);

        Assert.Equal(AccessLevel.Full, await _svc.GetLevelAsync(formerOwner.Id, _tb.Id));
    }

    [Fact]
    public async Task GetLevel_EmptyTargetId_None() =>
        Assert.Equal(AccessLevel.None, await _svc.GetLevelAsync(_staffAdmin.Id, Guid.Empty));

    [Fact]
    public async Task GetLevel_CustomerUserOtherCustomersTarget_None() =>
        Assert.Equal(AccessLevel.None, await _svc.GetLevelAsync(_fullUserA.Id, _tb.Id));

    // ---- ListAsync ----

    [Fact]
    public async Task List_CustomerAOnTargetB_Denied_AndAudited_AndBackendNotCalled()
    {
        var result = await _svc.ListAsync(_fullUserA.Id, _tb.Id, "", null, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Equal(StorageAccessService.DeniedMessage, result.Message);
        Assert.Null(result.Value);
        Assert.Empty(_fake.Calls);
        var row = Assert.Single(await Audits());
        Assert.Equal(AuditResult.Denied, row.Result);
        Assert.Equal("List", row.Operation);
        Assert.Equal(_tb.Id, row.TargetId);
        Assert.Equal(_fullUserA.Id, row.UserId);
        Assert.Equal(_custA.Id, row.CustomerId);
        Assert.Equal("10.0.0.1", row.SourceIp);
        Assert.Equal("corr-1", row.CorrelationId);
        Assert.Equal(Now, row.At);
    }

    [Fact]
    public async Task List_OtherCustomersTarget_IndistinguishableFromNonexistentTarget()
    {
        var foreign = await _svc.ListAsync(_fullUserA.Id, _tb.Id, "x", null, Ctx);
        var missing = await _svc.ListAsync(_fullUserA.Id, Guid.NewGuid(), "x", null, Ctx);
        var empty = await _svc.ListAsync(_fullUserA.Id, Guid.Empty, "x", null, Ctx);

        Assert.Equal(foreign, missing);
        Assert.Equal(foreign, empty);
        Assert.Equal(AccessOutcome.Denied, foreign.Outcome);
        Assert.Equal(3, (await Audits()).Count(a => a.Result == AuditResult.Denied));
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public async Task List_InactiveTarget_Denied() =>
        Assert.Equal(AccessOutcome.Denied, (await _svc.ListAsync(_fullUserA.Id, _inactive.Id, "", null, Ctx)).Outcome);

    [Fact]
    public async Task List_StaffRoleNone_Denied()
    {
        var result = await _svc.ListAsync(_staffNone.Id, _ta.Id, "", null, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public async Task List_OwnTarget_Ok_NotAudited()
    {
        await Seed(_ta, "Docs");

        var result = await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal("Docs", Assert.Single(result.Value!.Items).Name);
        Assert.Empty(await Audits());
    }

    [Fact]
    public async Task List_GrantRevokedBetweenCalls_SecondCallDenied()
    {
        Assert.Equal(AccessOutcome.Ok, (await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx)).Outcome);

        await _grants.RemoveAsync(_custA.Id, _ta.Id);
        var second = await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx);

        Assert.Equal(AccessOutcome.Denied, second.Outcome);
        Assert.Single(_fake.Calls);
    }

    [Fact]
    public async Task List_UserDeactivatedBetweenCalls_SecondCallDenied()
    {
        Assert.Equal(AccessOutcome.Ok, (await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx)).Outcome);

        await _users.UpsertAsync(_fullUserA with { Active = false });
        var second = await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx);

        Assert.Equal(AccessOutcome.Denied, second.Outcome);
        Assert.Single(_fake.Calls);
        Assert.Equal(AuditResult.Denied, Assert.Single(await Audits()).Result);
    }

    [Fact]
    public async Task List_CustomerDeactivatedBetweenCalls_SecondCallDenied()
    {
        Assert.Equal(AccessOutcome.Ok, (await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx)).Outcome);

        await _customers.UpsertAsync(_custA with { Active = false });

        Assert.Equal(AccessOutcome.Denied, (await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx)).Outcome);
        Assert.Single(_fake.Calls);
    }

    [Fact]
    public async Task List_StaffDemotedToNoneBetweenCalls_SecondCallDenied()
    {
        Assert.Equal(AccessOutcome.Ok, (await _svc.ListAsync(_staff.Id, _tb.Id, "", null, Ctx)).Outcome);

        await _users.UpsertAsync(_staff with { StaffRole = StaffRole.None });

        Assert.Equal(AccessOutcome.Denied, (await _svc.ListAsync(_staff.Id, _tb.Id, "", null, Ctx)).Outcome);
    }

    [Fact]
    public async Task List_UnknownUser_DeniedAndAudited()
    {
        var ghost = Guid.NewGuid();

        var result = await _svc.ListAsync(ghost, _ta.Id, "", null, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Equal(ghost, Assert.Single(await Audits()).UserId);
        Assert.Empty(_fake.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_BackendUnavailable_ReturnsUnavailableWithReadableMessage(bool notAuthorised)
    {
        _fake.ThrowUnreachable = !notAuthorised;
        _fake.ThrowNotAuthorised = notAuthorised;

        var result = await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(notAuthorised ? StorageErrorMapper.NotAuthorisedMessage : StorageErrorMapper.UnreachableMessage, result.Message);
    }

    [Fact]
    public async Task List_MissingFolder_NotFound()
    {
        var result = await _svc.ListAsync(_fullUserA.Id, _ta.Id, "nope", null, Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        Assert.Equal(StorageErrorMapper.NotFoundMessage, result.Message);
    }

    [Fact]
    public async Task List_PassesContinuationToken()
    {
        await Seed(_ta, Enumerable.Range(0, 250).Select(i => $"f{i:D3}").ToArray());

        var first = await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx);
        Assert.Equal(AccessOutcome.Ok, first.Outcome);
        Assert.Equal(StorageAccessService.PageSize, first.Value!.Items.Count);
        Assert.NotNull(first.Value.ContinuationToken);

        var second = await _svc.ListAsync(_fullUserA.Id, _ta.Id, "", first.Value.ContinuationToken, Ctx);
        Assert.Equal(50, second.Value!.Items.Count);
        Assert.Null(second.Value.ContinuationToken);
        Assert.Equal(250, first.Value.Items.Concat(second.Value.Items).Select(i => i.Name).Distinct().Count());
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/../..")]
    [InlineData("a\\b")]
    [InlineData("a/.folder")]
    [InlineData(".x.cssupload-0123456789abcdef0123456789abcdef")]
    public async Task List_InvalidPath_Invalid_NoAudit_NoBackend(string folder)
    {
        var result = await _svc.ListAsync(_fullUserA.Id, _ta.Id, folder, null, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.Empty(await Audits());
        Assert.Empty(_fake.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public async Task List_BlankPath_ListsRoot(string? folder)
    {
        var result = await _svc.ListAsync(_fullUserA.Id, _ta.Id, folder!, null, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal("List:", Assert.Single(_fake.Calls));
    }

    [Fact]
    public async Task List_BackendUnavailable_LogsInnerException()
    {
        var inner = new HttpRequestException("connection refused");
        var svc = Service(backends: new ThrowingBackend(StorageErrorMapper.Map(inner)));

        var result = await svc.ListAsync(_fullUserA.Id, _ta.Id, "Docs", null, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(StorageErrorMapper.UnreachableMessage, result.Message);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(inner, entry.Exception);
        Assert.Contains("corr-1", entry.Message);
        Assert.Contains(_ta.Id.ToString(), entry.Message);
        Assert.Contains("Unreachable", entry.Message);
    }

    [Fact]
    public async Task AuthorizeDownload_BackendFailure_LogsWarning()
    {
        var svc = Service(backends: new ThrowingBackend(StorageErrorMapper.Map(new Exception("boom"))));

        var result = await svc.AuthorizeDownloadAsync(_fullUserA.Id, _ta.Id, "a.txt", Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(LogLevel.Warning, Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public async Task Delete_BackendFailure_LogsWarningAndAuditsFailed()
    {
        _fake.ThrowNotAuthorised = true;

        await _svc.DeleteAsync(_fullUserA.Id, _ta.Id, "a.txt", Ctx);

        Assert.Equal(LogLevel.Warning, Assert.Single(_log.Entries).Level);
        Assert.Equal(AuditResult.Failed, Assert.Single(await Audits()).Result);
    }

    [Fact]
    public async Task Delete_UnexpectedBackendException_AuditsFailed_LogsError_Propagates()
    {
        var svc = Service(backends: new ThrowingBackend(new InvalidOperationException("bug")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync(_fullUserA.Id, _ta.Id, "a.txt", Ctx));

        var row = Assert.Single(await Audits());
        Assert.Equal((AuditResult.Failed, StorageErrorMapper.OtherMessage), (row.Result, row.Error));
        Assert.Equal(LogLevel.Error, Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public async Task List_TargetReassignedToOtherCustomerUnderExistingGrant_Denied()
    {
        // The stores forbid this state; a stale grant row must still not open a target now owned by B.
        await _grants.RemoveAsync(_custA.Id, _ta.Id);
        await _targets.UpsertAsync(_ta with { CustomerId = _custB.Id });
        var svc = Service(grants: new StaleGrantStore(new Grant(_custA.Id, _ta.Id, AccessLevel.Full, "admin", Now)));

        var result = await svc.ListAsync(_fullUserA.Id, _ta.Id, "", null, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Empty(_fake.Calls);
        Assert.Empty(await svc.ListReachableTargetsAsync(_fullUserA.Id));
    }

    [Fact]
    public async Task List_PercentEncodedDotDot_TreatedAsLiteralName()
    {
        var result = await _svc.ListAsync(_fullUserA.Id, _ta.Id, "%2e%2e/%2E%2E", null, Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        Assert.Equal("List:%2e%2e/%2E%2E", Assert.Single(_fake.Calls));
    }

    [Fact]
    public async Task List_ContinuationTokenFromOtherTarget_UsesRequestedTarget()
    {
        await Seed(_ta, Enumerable.Range(0, 250).Select(i => $"a{i:D3}").ToArray());
        await Seed(_tb, Enumerable.Range(0, 205).Select(i => $"b{i:D3}").ToArray());
        var token = (await _svc.ListAsync(_staffAdmin.Id, _ta.Id, "", null, Ctx)).Value!.ContinuationToken;

        var result = await _svc.ListAsync(_staffAdmin.Id, _tb.Id, "", token, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.NotEmpty(result.Value!.Items);
        Assert.All(result.Value.Items, i => Assert.StartsWith("b", i.Name));
    }

    // ---- AuthorizeDownloadAsync ----

    [Fact]
    public async Task AuthorizeDownload_PathTraversal_Invalid()
    {
        var result = await _svc.AuthorizeDownloadAsync(_fullUserA.Id, _ta.Id, "../TB/secret.txt", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Empty(_fake.Calls);
        Assert.Empty(await Audits());
    }

    [Fact]
    public async Task AuthorizeDownload_OwnFile_OkWithTargetAndItem_NotAudited()
    {
        await Seed(_ta, "Docs");
        await PutFile(_ta, "Docs/a.txt", "hello");

        var result = await _svc.AuthorizeDownloadAsync(_fullUserA.Id, _ta.Id, "/Docs//a.txt", Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal(_ta.Id, result.Value.Target.Id);
        Assert.Equal("Docs/a.txt", result.Value.Item.Path);
        Assert.Equal(5, result.Value.Item.Size);
        Assert.Empty(await Audits());
    }

    [Fact]
    public async Task AuthorizeDownload_OtherCustomersTarget_DeniedAndAudited()
    {
        var result = await _svc.AuthorizeDownloadAsync(_fullUserA.Id, _tb.Id, "secret.txt", Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        var row = Assert.Single(await Audits());
        Assert.Equal("Download", row.Operation);
        Assert.Equal(AuditResult.Denied, row.Result);
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public async Task AuthorizeDownload_Folder_Invalid()
    {
        await Seed(_ta, "Docs");
        var result = await _svc.AuthorizeDownloadAsync(_fullUserA.Id, _ta.Id, "Docs", Ctx);
        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task AuthorizeDownload_Root_Invalid_NoBackend()
    {
        var result = await _svc.AuthorizeDownloadAsync(_fullUserA.Id, _ta.Id, "", Ctx);
        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public async Task AuthorizeDownload_Missing_NotFound()
    {
        var result = await _svc.AuthorizeDownloadAsync(_fullUserA.Id, _ta.Id, "ghost.txt", Ctx);
        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        Assert.Empty(await Audits());
    }

    [Fact]
    public async Task AuthorizeDownload_FolderMarker_Invalid() =>
        Assert.Equal(AccessOutcome.Invalid,
            (await _svc.AuthorizeDownloadAsync(_fullUserA.Id, _ta.Id, "Docs/.folder", Ctx)).Outcome);

    // ---- CreateFolderAsync ----

    [Fact]
    public async Task CreateFolder_InvalidName_Invalid_NoAudit()
    {
        var result = await _svc.CreateFolderAsync(_fullUserA.Id, _ta.Id, "", "bad:name", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Empty(await Audits());
        Assert.Empty(_fake.Calls);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(".folder")]
    public async Task CreateFolder_BadNames_Invalid(string name) =>
        Assert.Equal(AccessOutcome.Invalid, (await _svc.CreateFolderAsync(_fullUserA.Id, _ta.Id, "", name, Ctx)).Outcome);

    [Fact]
    public async Task CreateFolder_Contribute_Succeeds_AndAuditsSuccess()
    {
        await GrantA(AccessLevel.Contribute);

        var result = await _svc.CreateFolderAsync(_fullUserA.Id, _ta.Id, "Docs", "New", Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Contains("CreateFolder:Docs/New", _fake.Calls);
        var row = Assert.Single(await Audits());
        Assert.Equal(("CreateFolder", AuditResult.Success, "Docs/New"), (row.Operation, row.Result, row.Path));
    }

    [Fact]
    public async Task CreateFolder_ReadUser_Denied()
    {
        await GrantA(AccessLevel.Read);

        var result = await _svc.CreateFolderAsync(_readUserA.Id, _ta.Id, "", "New", Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public async Task CreateFolder_Existing_AlreadyExists_AuditsFailed()
    {
        await Seed(_ta, "Docs");

        var result = await _svc.CreateFolderAsync(_fullUserA.Id, _ta.Id, "", "Docs", Ctx);

        Assert.Equal(AccessOutcome.AlreadyExists, result.Outcome);
        var row = Assert.Single(await Audits());
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal(StorageErrorMapper.AlreadyExistsMessage, row.Error);
    }

    // ---- MoveAsync ----

    [Fact]
    public async Task Move_IntoOwnSubtree_Invalid()
    {
        await Seed(_ta, "Docs/Sub");

        var result = await _svc.MoveAsync(_fullUserA.Id, _ta.Id, "Docs", "Docs/Sub/Docs", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("Move:"));
        Assert.Empty(await Audits());
    }

    [Fact]
    public async Task Move_IntoOwnSubtreeDifferentCase_Invalid()
    {
        await Seed(_ta, "Docs/Sub");

        var result = await _svc.MoveAsync(_fullUserA.Id, _ta.Id, "Docs", "docs/Sub/Docs", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("Move:"));
    }

    [Fact]
    public async Task Move_Files_CaseDifferentSubtree_Invalid()
    {
        await Seed(_ta, "Docs");

        var result = await _svc.MoveAsync(_staffAdmin.Id, _ta.Id, "Docs", "docs/Docs", Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(StorageErrorMapper.MoveIntoItselfMessage, result.Message);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("Move:"));
    }

    [Fact]
    public async Task Move_Blob_CaseDifferentSubtree_Allowed()
    {
        await Seed(_tb, "Docs");

        var result = await _svc.MoveAsync(_staffAdmin.Id, _tb.Id, "Docs", "docs/Docs", Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Contains("Move:Docs->docs/Docs", _fake.Calls);
    }

    [Fact]
    public async Task Move_CaseOnlyRename_Allowed()
    {
        await PutFile(_ta, "a.txt", "x");

        Assert.Equal(AccessOutcome.Ok, (await _svc.MoveAsync(_fullUserA.Id, _ta.Id, "a.txt", "A.txt", Ctx)).Outcome);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("x", "")]
    [InlineData("/", "x")]
    [InlineData("Docs", "Docs")]
    [InlineData("Docs", "../Docs")]
    [InlineData("Docs", "Docs2/.folder")]
    public async Task Move_InvalidPaths_Invalid_NoBackend(string from, string to)
    {
        await Seed(_ta, "Docs");
        var before = _fake.Calls.Count;

        var result = await _svc.MoveAsync(_fullUserA.Id, _ta.Id, from, to, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(before, _fake.Calls.Count);
    }

    [Fact]
    public async Task Move_SameParent_AuditedAsMove()
    {
        await GrantA(AccessLevel.Contribute);
        await Seed(_ta, "Docs");
        await PutFile(_ta, "Docs/a.txt", "x");

        var result = await _svc.MoveAsync(_fullUserA.Id, _ta.Id, "Docs/a.txt", "Docs/b.txt", Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Contains("Move:Docs/a.txt->Docs/b.txt", _fake.Calls);
        var row = Assert.Single(await Audits());
        Assert.Equal(("Move", AuditResult.Success), (row.Operation, row.Result));
        Assert.Contains("Docs/a.txt", row.Path);
        Assert.Contains("Docs/b.txt", row.Path);
    }

    [Fact]
    public async Task Move_DifferentParent_Folder_AuditedAsMove()
    {
        await Seed(_ta, "Docs/Sub", "Archive");

        var result = await _svc.MoveAsync(_fullUserA.Id, _ta.Id, "Docs/Sub", "Archive/Sub", Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal("Move", Assert.Single(await Audits()).Operation);
        Assert.NotNull(await _fake.GetItemAsync(_ta, "Archive/Sub", default));
    }

    [Fact]
    public async Task Move_ReadUser_Denied()
    {
        await GrantA(AccessLevel.Read);
        await Seed(_ta, "Docs");
        var before = _fake.Calls.Count;

        var result = await _svc.MoveAsync(_readUserA.Id, _ta.Id, "Docs", "Docs2", Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Equal(before, _fake.Calls.Count);
        Assert.Equal(AuditResult.Denied, Assert.Single(await Audits()).Result);
    }

    [Fact]
    public async Task Move_MissingSource_NotFound_AuditsFailed()
    {
        var result = await _svc.MoveAsync(_fullUserA.Id, _ta.Id, "ghost.txt", "g.txt", Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        Assert.Equal(AuditResult.Failed, Assert.Single(await Audits()).Result);
    }

    // ---- DeleteAsync ----

    [Fact]
    public async Task Delete_ReadUser_Denied()
    {
        await GrantA(AccessLevel.Read);
        await PutFile(_ta, "a.txt", "x");
        var before = _fake.Calls.Count;

        var result = await _svc.DeleteAsync(_readUserA.Id, _ta.Id, "a.txt", Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Equal(StorageAccessService.DeniedMessage, result.Message);
        Assert.Equal(before, _fake.Calls.Count);
        var row = Assert.Single(await Audits());
        Assert.Equal(("Delete", AuditResult.Denied), (row.Operation, row.Result));
    }

    [Fact]
    public async Task Delete_ContributeStaff_Denied() =>
        Assert.Equal(AccessOutcome.Denied, (await _svc.DeleteAsync(_staff.Id, _ta.Id, "a.txt", Ctx)).Outcome);

    [Fact]
    public async Task Delete_FullUser_Succeeds_AndAuditsSuccess()
    {
        await PutFile(_ta, "a.txt", "x");

        var result = await _svc.DeleteAsync(_fullUserA.Id, _ta.Id, "a.txt", Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Null(await _fake.GetItemAsync(_ta, "a.txt", default));
        var row = Assert.Single(await Audits());
        Assert.Equal(("Delete", AuditResult.Success, "a.txt", _ta.Id), (row.Operation, row.Result, row.Path, row.TargetId));
    }

    [Fact]
    public async Task Delete_Folder_Recursive_Succeeds()
    {
        await Seed(_ta, "Docs/Sub");
        await PutFile(_ta, "Docs/Sub/a.txt", "x");

        Assert.Equal(AccessOutcome.Ok, (await _svc.DeleteAsync(_staffAdmin.Id, _ta.Id, "Docs", Ctx)).Outcome);
        Assert.Null(await _fake.GetItemAsync(_ta, "Docs/Sub/a.txt", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("  ")]
    [InlineData("//")]
    public async Task Delete_Root_Invalid(string path)
    {
        var result = await _svc.DeleteAsync(_fullUserA.Id, _ta.Id, path, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Empty(_fake.Calls);
        Assert.Empty(await Audits());
    }

    [Fact]
    public async Task Delete_Missing_NotFound_AuditsFailed()
    {
        var result = await _svc.DeleteAsync(_fullUserA.Id, _ta.Id, "ghost.txt", Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        var row = Assert.Single(await Audits());
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal(StorageErrorMapper.NotFoundMessage, row.Error);
    }

    [Fact]
    public async Task Delete_BackendUnreachable_Unavailable_AuditsFailed()
    {
        _fake.ThrowUnreachable = true;

        var result = await _svc.DeleteAsync(_fullUserA.Id, _ta.Id, "a.txt", Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(StorageErrorMapper.UnreachableMessage, result.Message);
        var row = Assert.Single(await Audits());
        Assert.Equal((AuditResult.Failed, StorageErrorMapper.UnreachableMessage), (row.Result, row.Error));
    }

    [Fact]
    public async Task Delete_StaffOnCustomerTarget_AuditRowCarriesTargetsCustomer()
    {
        await PutFile(_tb, "a.txt", "x");

        Assert.Equal(AccessOutcome.Ok, (await _svc.DeleteAsync(_staffAdmin.Id, _tb.Id, "a.txt", Ctx)).Outcome);

        Assert.Equal(_custB.Id, Assert.Single(await Audits()).CustomerId);
    }

    [Fact]
    public async Task Delete_DeconfiguredOwner_ActsAsStaffAdmin()
    {
        var formerOwner = User(UserKind.Staff, null, "gone@advancedutility.com", StaffRole.Owner);
        await _users.UpsertAsync(formerOwner);
        await PutFile(_tb, "a.txt", "x");

        Assert.Equal(AccessOutcome.Ok, (await _svc.DeleteAsync(formerOwner.Id, _tb.Id, "a.txt", Ctx)).Outcome);
    }

    // ---- AuthorizeAsync ----

    [Fact]
    public async Task Authorize_UploadContribute_OkWithTarget_NoAudit_NoBackend()
    {
        await GrantA(AccessLevel.Contribute);

        var result = await _svc.AuthorizeAsync(_fullUserA.Id, _ta.Id, StorageOperation.Upload, "Docs/new.bin", Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal(_ta.Id, result.Value!.Id);
        Assert.Empty(await Audits());
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public async Task Authorize_UploadRead_DeniedAndAudited()
    {
        await GrantA(AccessLevel.Read);

        var result = await _svc.AuthorizeAsync(_readUserA.Id, _ta.Id, StorageOperation.Upload, "new.bin", Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Null(result.Value);
        var row = Assert.Single(await Audits());
        Assert.Equal(("Upload", AuditResult.Denied, "new.bin"), (row.Operation, row.Result, row.Path));
    }

    [Theory]
    [InlineData(StorageOperation.Upload, "")]
    [InlineData(StorageOperation.Upload, "../x.bin")]
    [InlineData(StorageOperation.Overwrite, "a/.x.bin.cssupload-0123456789abcdef0123456789abcdef")]
    public async Task Authorize_InvalidPath_Invalid_NoAudit(StorageOperation op, string path)
    {
        var result = await _svc.AuthorizeAsync(_fullUserA.Id, _ta.Id, op, path, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Empty(await Audits());
    }

    [Fact]
    public async Task Authorize_ListRoot_Ok() =>
        Assert.Equal(AccessOutcome.Ok, (await _svc.AuthorizeAsync(_userB.Id, _tb.Id, StorageOperation.List, "", Ctx)).Outcome);

    private sealed class ListLogger : ILogger<StorageAccessService>
    {
        public readonly List<(LogLevel Level, Exception? Exception, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    private sealed class StaleGrantStore(Grant grant) : IGrantStore
    {
        public Task<Grant?> GetAsync(Guid customerId, Guid targetId, CancellationToken ct = default) => Task.FromResult<Grant?>(grant);
        public Task<IReadOnlyList<Grant>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Grant>>(new[] { grant });
        public Task SetAsync(Grant g, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid customerId, Guid targetId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingBackend(Exception ex) : IStorageBackend, IStorageBackendResolver
    {
        public IStorageBackend For(StorageTarget t) => this;
        private Task<T> Fail<T>() => Task.FromException<T>(ex);
        public Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct) => Fail<StorageListing>();
        public Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct) => Fail<StorageItem?>();
        public Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct) => Fail<Stream>();
        public Task CreateFolderAsync(StorageTarget t, string path, CancellationToken ct) => Fail<bool>();
        public Task MoveAsync(StorageTarget t, string from, string to, bool isFolder, CancellationToken ct) => Fail<bool>();
        public Task DeleteAsync(StorageTarget t, string path, bool isFolder, CancellationToken ct) => Fail<bool>();
        public Task BeginUploadAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct) => Fail<bool>();
        public Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset,
            Stream data, long length, byte[] md5, CancellationToken ct) => Fail<bool>();
        public Task CompleteUploadAsync(StorageTarget t, string stagingPath, string finalPath, IReadOnlyList<string> blockIds,
            bool overwrite, CancellationToken ct) => Fail<bool>();
        public Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct) => Fail<bool>();
        public Task ProbeAsync(StorageTarget t, CancellationToken ct) => Fail<bool>();
    }

    private async Task PutFile(StorageTarget t, string path, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var staging = StoragePaths.Join(StoragePaths.Parent(path), $".{StoragePaths.Leaf(path)}.cssupload-{Guid.NewGuid():N}");
        await _fake.BeginUploadAsync(t, staging, bytes.Length, default);
        await _fake.WriteChunkAsync(t, staging, path, "b0", 0, new MemoryStream(bytes), bytes.Length,
            System.Security.Cryptography.MD5.HashData(bytes), default);
        await _fake.CompleteUploadAsync(t, staging, path, new[] { "b0" }, false, default);
    }
}
