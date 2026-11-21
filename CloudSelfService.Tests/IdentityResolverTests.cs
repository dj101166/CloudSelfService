using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

public class IdentityResolverTests
{
    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryCustomerStore _customers = new();
    private readonly InMemoryAuditStore _audit = new();

    private IdentityResolver Resolver(string owners = "") =>
        new(_users, _customers, new AuditService(_audit, NullLogger<AuditService>.Instance),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Entra:StaffEmailDomain"] = "advancedutility.com",
            }).Build(),
            new OwnerConfig(owners));

    private static EntraIdentity Staff(string upn = "sam@advancedutility.com", string oid = "oid-sam") =>
        new(oid, "our-tenant", upn, "Sam", IsGuest: false);

    private static EntraIdentity Guest(string upn = "pat@customer.example", string oid = "oid-pat") =>
        new(oid, "our-tenant", upn, "Pat", IsGuest: true);

    private async Task<Customer> AddCustomer(bool active = true)
    {
        var c = new Customer(Guid.NewGuid(), "Contoso", active, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
        await _customers.UpsertAsync(c);
        return c;
    }

    private async Task<AppUser> AddCustomerUser(Guid customerId, string upn = "pat@customer.example", string? oid = null, bool active = true)
    {
        var u = new AppUser(Guid.NewGuid(), UserKind.Customer, customerId, upn, "Pat", oid, StaffRole.None, active,
            DateTimeOffset.UtcNow, null);
        await _users.UpsertAsync(u);
        return u;
    }

    [Fact]
    public async Task Admit_NewStaff_CreatedWithRoleNone_PendingRole()
    {
        var result = await Resolver().AdmitAsync(Staff());

        Assert.Equal(AdmissionOutcome.AdmittedPendingRole, result.Outcome);
        var stored = await _users.FindByObjectIdAsync("oid-sam");
        Assert.NotNull(stored);
        Assert.Equal(UserKind.Staff, stored!.Kind);
        Assert.Equal(StaffRole.None, stored.StaffRole);
        Assert.True(stored.Active);
        Assert.Null(stored.CustomerId);
        Assert.NotNull(stored.LastSignInAt);
        Assert.Equal(stored.Id, result.User!.Id);
    }

    [Fact]
    public async Task Admit_OwnerFromConfig_PromotedToOwner()
    {
        await _users.UpsertAsync(new AppUser(Guid.NewGuid(), UserKind.Staff, null, "boss@advancedutility.com", "Boss",
            "oid-boss", StaffRole.Staff, true, DateTimeOffset.UtcNow, null));

        var result = await Resolver(owners: "someone@advancedutility.com, BOSS@advancedutility.com")
            .AdmitAsync(Staff("boss@advancedutility.com", "oid-boss"));

        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
        Assert.Equal(StaffRole.Owner, result.User!.StaffRole);
        Assert.Equal(StaffRole.Staff, (await _users.FindByObjectIdAsync("oid-boss"))!.StaffRole);
    }

    [Fact]
    public async Task Admit_OwnerRemovedFromConfig_DemotedToStaffAdmin()
    {
        await _users.UpsertAsync(new AppUser(Guid.NewGuid(), UserKind.Staff, null, "boss@advancedutility.com", "Boss",
            "oid-boss", StaffRole.Owner, true, DateTimeOffset.UtcNow, null));

        var result = await Resolver(owners: "someone@advancedutility.com").AdmitAsync(Staff("boss@advancedutility.com", "oid-boss"));

        // A sign-in never writes a role: the stored Owner stays and EffectiveRole reads it as StaffAdmin.
        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
        Assert.Equal(StaffRole.StaffAdmin, result.User!.StaffRole);
        Assert.Equal(StaffRole.Owner, (await _users.FindByObjectIdAsync("oid-boss"))!.StaffRole);
    }

    [Fact]
    public async Task Admit_RenamedOwner_StoredUpnUpdated_RemovalFromConfigTakesEffect()
    {
        var id = Guid.NewGuid();
        await _users.UpsertAsync(new AppUser(id, UserKind.Staff, null, "old.name@advancedutility.com", "Old Name",
            "oid-boss", StaffRole.Owner, true, DateTimeOffset.UtcNow, null));
        var renamed = Staff("new.name@advancedutility.com", "oid-boss") with { DisplayName = "New Name" };

        // Config still lists only the OLD upn, so the renamed account is no longer an owner.
        var result = await Resolver(owners: "old.name@advancedutility.com").AdmitAsync(renamed);

        var stored = await _users.GetAsync(id);
        Assert.Equal("new.name@advancedutility.com", stored!.Upn);
        Assert.Equal("New Name", stored.DisplayName);
        Assert.Equal(StaffRole.StaffAdmin, result.User!.StaffRole);
        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
    }

    [Fact]
    public async Task Admit_RenamedCustomer_StoredUpnAndNameUpdated()
    {
        var customer = await AddCustomer();
        var user = await AddCustomerUser(customer.Id, oid: "oid-pat");

        await Resolver().AdmitAsync(Guest("pat.renamed@customer.example", "oid-pat") with { DisplayName = "Pat R" });

        var stored = await _users.GetAsync(user.Id);
        Assert.Equal("pat.renamed@customer.example", stored!.Upn);
        Assert.Equal("Pat R", stored.DisplayName);
    }

    [Fact]
    public async Task Admit_EmailOnlyIdentityForBoundUser_DoesNotRename()
    {
        var customer = await AddCustomer();
        var user = await AddCustomerUser(customer.Id, oid: "oid-pat");

        await Resolver().AdmitAsync(Guest("spoofed@elsewhere.example", "oid-pat") with { UpnFromEmail = true });

        Assert.Equal("pat@customer.example", (await _users.GetAsync(user.Id))!.Upn);
    }

    [Fact]
    public async Task Admit_RenameToUpnHeldByAnotherRecord_KeepsOldUpn()
    {
        var customer = await AddCustomer();
        var user = await AddCustomerUser(customer.Id, oid: "oid-pat");
        await AddCustomerUser(customer.Id, upn: "taken@customer.example", oid: "oid-other");

        var result = await Resolver().AdmitAsync(Guest("taken@customer.example", "oid-pat"));

        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
        Assert.Equal("pat@customer.example", (await _users.GetAsync(user.Id))!.Upn);
    }

    [Fact]
    public async Task Admit_NewStaffListedAsOwner_CreatedAsOwner()
    {
        var result = await Resolver(owners: "sam@advancedutility.com").AdmitAsync(Staff());

        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
        Assert.Equal(StaffRole.Owner, result.User!.StaffRole);
        Assert.Equal(StaffRole.None, (await _users.FindByObjectIdAsync("oid-sam"))!.StaffRole);
    }

    [Fact]
    public async Task Admit_DeactivatedBetweenReadAndWrite_StaysInactiveAndRefused()
    {
        var customer = await AddCustomer();
        var user = await AddCustomerUser(customer.Id, oid: "oid-pat");
        var racing = new DeactivateAfterReadUsers(_users, user.Id);
        var resolver = new IdentityResolver(racing, _customers, new AuditService(_audit, NullLogger<AuditService>.Instance),
            new ConfigurationBuilder().Build(), new OwnerConfig(""));

        var result = await resolver.AdmitAsync(Guest("pat@customer.example", "oid-pat"));

        Assert.Equal(AdmissionOutcome.Deactivated, result.Outcome);
        Assert.False((await _users.GetAsync(user.Id))!.Active);
    }

    [Fact]
    public async Task Admit_StaffDemotedBetweenReadAndWrite_RoleSurvives()
    {
        var staff = new AppUser(Guid.NewGuid(), UserKind.Staff, null, "sam@advancedutility.com", "Sam", "oid-sam", StaffRole.StaffAdmin, true, DateTimeOffset.UtcNow, null);
        await _users.UpsertAsync(staff);
        var racing = new DeactivateAfterReadUsers(_users, staff.Id, demoteTo: StaffRole.None);
        var resolver = new IdentityResolver(racing, _customers, new AuditService(_audit, NullLogger<AuditService>.Instance),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Entra:StaffEmailDomain"] = "advancedutility.com" }).Build(),
            new OwnerConfig(""));

        var result = await resolver.AdmitAsync(Staff());

        Assert.Equal(AdmissionOutcome.AdmittedPendingRole, result.Outcome);
        Assert.Equal(StaffRole.None, (await _users.GetAsync(staff.Id))!.StaffRole);
    }

    /// <summary>Lets the resolver read the user, then makes the admin's change before the resolver writes.</summary>
    private sealed class DeactivateAfterReadUsers(IUserStore inner, Guid victim, StaffRole? demoteTo = null) : IUserStore
    {
        private bool _fired;

        private async Task FireAsync()
        {
            if (_fired) return;
            _fired = true;
            if (demoteTo is { } role) await inner.SetStaffRoleAsync(victim, role);
            else await inner.SetActiveAsync(victim, false);
        }

        public Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public async Task<AppUser?> FindByObjectIdAsync(string oid, CancellationToken ct = default)
        {
            var user = await inner.FindByObjectIdAsync(oid, ct);
            await FireAsync();
            return user;
        }
        public Task<AppUser?> FindByUpnAsync(string upn, CancellationToken ct = default) => inner.FindByUpnAsync(upn, ct);
        public Task<IReadOnlyList<AppUser>> ListAsync(UserKind? kind = null, Guid? customerId = null, CancellationToken ct = default) => inner.ListAsync(kind, customerId, ct);
        public Task UpsertAsync(AppUser user, CancellationToken ct = default) => inner.UpsertAsync(user, ct);
        public Task<bool> SetActiveAsync(Guid userId, bool active, CancellationToken ct = default) => inner.SetActiveAsync(userId, active, ct);
        public Task<bool> SetStaffRoleAsync(Guid userId, StaffRole role, CancellationToken ct = default) => inner.SetStaffRoleAsync(userId, role, ct);
        public Task<AppUser?> RecordSignInAsync(Guid userId, string oid, string upn, string displayName, DateTimeOffset now, CancellationToken ct = default) =>
            inner.RecordSignInAsync(userId, oid, upn, displayName, now, ct);
    }

    [Fact]
    public async Task Admit_FirstBind_HomeTenantMismatch_Refused_Match_Admitted()
    {
        var tenant = Guid.NewGuid();
        var c = new Customer(Guid.NewGuid(), "Gov Co", true, "", tenant.ToString(), CustomerCloud.AzureGov, CrossTenantStatus.Complete);
        await _customers.UpsertAsync(c);
        await AddCustomerUser(c.Id);

        var wrong = await Resolver().AdmitAsync(Guest() with { Idp = $"https://login.microsoftonline.us/{Guid.NewGuid()}/v2.0" });
        var none = await Resolver().AdmitAsync(Guest());
        var right = await Resolver().AdmitAsync(Guest() with { Idp = $"https://login.microsoftonline.us/{tenant}/v2.0" });

        Assert.Equal(AdmissionOutcome.UnknownCustomer, wrong.Outcome);
        Assert.Equal(AdmissionOutcome.UnknownCustomer, none.Outcome);
        Assert.Equal(AdmissionOutcome.Admitted, right.Outcome);
        var denied = (await _audit.QueryAsync(new AuditQuery(Operation: "SignIn", Result: AuditResult.Denied))).Select(e => e.Error);
        Assert.Contains(denied, e => e.Contains("Home tenant does not match"));
    }

    [Fact]
    public async Task Admit_FirstBind_CrossTenantPending_Refused()
    {
        var c = new Customer(Guid.NewGuid(), "Gov Co", true, "", null, CustomerCloud.AzureGov, CrossTenantStatus.Pending);
        await _customers.UpsertAsync(c);
        await AddCustomerUser(c.Id);

        var result = await Resolver().AdmitAsync(Guest());

        Assert.Equal(AdmissionOutcome.UnknownCustomer, result.Outcome);
    }

    [Fact]
    public async Task Admit_BoundCustomer_HomeTenantNotRecheckedOnLaterSignIns()
    {
        var c = new Customer(Guid.NewGuid(), "Gov Co", true, "", Guid.NewGuid().ToString(), CustomerCloud.AzureGov, CrossTenantStatus.Complete);
        await _customers.UpsertAsync(c);
        await AddCustomerUser(c.Id, oid: "oid-pat");

        var result = await Resolver().AdmitAsync(Guest());

        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
    }

    [Fact]
    public void EntraIdentityReader_KeepsIdpClaim()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("oid", "o"), new System.Security.Claims.Claim("preferred_username", "a@b.example"),
            new System.Security.Claims.Claim("idp", "https://sts.windows.net/11111111-2222-3333-4444-555555555555/"),
        }));

        Assert.Equal("https://sts.windows.net/11111111-2222-3333-4444-555555555555/", EntraIdentityReader.Read(principal)!.Idp);
    }

    [Fact]
    public async Task Admit_ExistingStaffByUpn_BindsOidAndAdmitted()
    {
        await _users.UpsertAsync(new AppUser(Guid.NewGuid(), UserKind.Staff, null, "sam@advancedutility.com", "Sam",
            null, StaffRole.StaffAdmin, true, DateTimeOffset.UtcNow, null));

        var result = await Resolver().AdmitAsync(Staff());

        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
        Assert.Equal("oid-sam", (await _users.FindByUpnAsync("sam@advancedutility.com"))!.EntraObjectId);
    }

    [Fact]
    public async Task Admit_InactiveStaff_Deactivated()
    {
        await _users.UpsertAsync(new AppUser(Guid.NewGuid(), UserKind.Staff, null, "sam@advancedutility.com", "Sam",
            "oid-sam", StaffRole.StaffAdmin, false, DateTimeOffset.UtcNow, null));

        var result = await Resolver().AdmitAsync(Staff());

        Assert.Equal(AdmissionOutcome.Deactivated, result.Outcome);
    }

    [Fact]
    public async Task Admit_UnknownGuest_UnknownCustomer_NoRecordCreated()
    {
        var result = await Resolver().AdmitAsync(Guest());

        Assert.Equal(AdmissionOutcome.UnknownCustomer, result.Outcome);
        Assert.Null(result.User);
        Assert.Empty(await _users.ListAsync());
    }

    [Fact]
    public async Task Admit_GuestWithStaffDomainEmail_NotTreatedAsStaff()
    {
        var result = await Resolver().AdmitAsync(Guest("sam@advancedutility.com", "oid-x"));

        Assert.Equal(AdmissionOutcome.UnknownCustomer, result.Outcome);
        Assert.Empty(await _users.ListAsync());
    }

    [Fact]
    public async Task Admit_GuestMatchingStaffRecordByUpn_UnknownCustomer()
    {
        await _users.UpsertAsync(new AppUser(Guid.NewGuid(), UserKind.Staff, null, "sam@advancedutility.com", "Sam",
            null, StaffRole.Owner, true, DateTimeOffset.UtcNow, null));

        var result = await Resolver().AdmitAsync(Guest("sam@advancedutility.com", "oid-x"));

        Assert.Equal(AdmissionOutcome.UnknownCustomer, result.Outcome);
        Assert.Null((await _users.FindByUpnAsync("sam@advancedutility.com"))!.EntraObjectId);
    }

    [Fact]
    public async Task Admit_MemberMatchingCustomerRecordByOid_NotAdmittedAsStaff()
    {
        var customer = await AddCustomer();
        await AddCustomerUser(customer.Id, upn: "pat@customer.example", oid: "oid-sam");

        var result = await Resolver().AdmitAsync(Staff());

        Assert.Equal(AdmissionOutcome.UnknownCustomer, result.Outcome);
    }

    [Fact]
    public async Task Admit_PreRegisteredGuestFirstSignIn_BindsOid()
    {
        var customer = await AddCustomer();
        var user = await AddCustomerUser(customer.Id, upn: "Pat@Customer.example");

        var result = await Resolver().AdmitAsync(Guest("pat@customer.example", "oid-pat"));

        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
        var stored = await _users.GetAsync(user.Id);
        Assert.Equal("oid-pat", stored!.EntraObjectId);
        Assert.NotNull(stored.LastSignInAt);
        Assert.Equal(UserKind.Customer, stored.Kind);
        Assert.Equal(StaffRole.None, stored.StaffRole);
    }

    [Fact]
    public async Task Admit_OnlyEmailClaim_DoesNotBind()
    {
        var customer = await AddCustomer();
        var user = await AddCustomerUser(customer.Id, upn: "pat@customer.example");
        var emailOnly = Guest("pat@customer.example", "oid-attacker") with { UpnFromEmail = true };

        var result = await Resolver().AdmitAsync(emailOnly);

        Assert.Equal(AdmissionOutcome.UnknownCustomer, result.Outcome);
        Assert.Null((await _users.GetAsync(user.Id))!.EntraObjectId);
    }

    [Fact]
    public async Task Admit_EmailOnlyMemberWithStaffDomain_NotCreatedAsStaff()
    {
        var result = await Resolver().AdmitAsync(Staff() with { UpnFromEmail = true });

        Assert.Equal(AdmissionOutcome.UnknownCustomer, result.Outcome);
        Assert.Empty(await _users.ListAsync());
    }

    [Fact]
    public async Task Admit_BoundGuestReturnsWithChangedEmail_AdmittedByOid()
    {
        var customer = await AddCustomer();
        var user = await AddCustomerUser(customer.Id, oid: "oid-pat");

        var result = await Resolver().AdmitAsync(Guest("pat.new@customer.example", "oid-pat"));

        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
        Assert.Equal(user.Id, result.User!.Id);
    }

    [Fact]
    public async Task Admit_UpnMatchButDifferentBoundOid_UnknownCustomer()
    {
        var customer = await AddCustomer();
        var user = await AddCustomerUser(customer.Id, oid: "oid-original");

        var result = await Resolver().AdmitAsync(Guest("pat@customer.example", "oid-impostor"));

        Assert.Equal(AdmissionOutcome.UnknownCustomer, result.Outcome);
        Assert.Equal("oid-original", (await _users.GetAsync(user.Id))!.EntraObjectId);
    }

    [Fact]
    public async Task Admit_InactiveCustomer_Deactivated()
    {
        var customer = await AddCustomer(active: false);
        await AddCustomerUser(customer.Id, oid: "oid-pat");

        var result = await Resolver().AdmitAsync(Guest());

        Assert.Equal(AdmissionOutcome.Deactivated, result.Outcome);
    }

    [Fact]
    public async Task Admit_InactiveCustomerUser_Deactivated()
    {
        var customer = await AddCustomer();
        await AddCustomerUser(customer.Id, oid: "oid-pat", active: false);

        var result = await Resolver().AdmitAsync(Guest());

        Assert.Equal(AdmissionOutcome.Deactivated, result.Outcome);
    }

    [Fact]
    public async Task Admit_Denied_WritesDeniedAuditRow()
    {
        await Resolver().AdmitAsync(Guest(), new AuditContext("203.0.113.9", "corr-1"));

        var row = Assert.Single(await _audit.QueryAsync(new AuditQuery()));
        Assert.Equal("SignIn", row.Operation);
        Assert.Equal(AuditResult.Denied, row.Result);
        Assert.Equal("pat@customer.example", row.UserDisplay);
        Assert.Equal("203.0.113.9", row.SourceIp);
        Assert.Equal("corr-1", row.CorrelationId);
    }

    [Fact]
    public async Task Admit_Deactivated_WritesDeniedAuditRow()
    {
        var customer = await AddCustomer(active: false);
        var user = await AddCustomerUser(customer.Id, oid: "oid-pat");

        await Resolver().AdmitAsync(Guest());

        var row = Assert.Single(await _audit.QueryAsync(new AuditQuery()));
        Assert.Equal(AuditResult.Denied, row.Result);
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(customer.Id, row.CustomerId);
    }

    [Fact]
    public async Task Admit_Admitted_WritesSuccessAuditRow()
    {
        var result = await Resolver().AdmitAsync(Staff());

        var row = Assert.Single(await _audit.QueryAsync(new AuditQuery()));
        Assert.Equal("SignIn", row.Operation);
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Equal(result.User!.Id, row.UserId);
    }
}
