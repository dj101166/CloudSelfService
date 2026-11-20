using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using Xunit;

namespace CloudSelfService.Tests;

public class InMemoryStoresTests
{
    private static StorageTarget Target(Guid id, Guid? customerId) =>
        new(id, customerId, "t", "sub", "rg", "acct", StorageKind.FileShare, "share", "core.windows.net", true);

    private static AppUser User(Guid id, string upn, string? oid = null) =>
        new(id, UserKind.Staff, null, upn, "Name", oid, StaffRole.Staff, true, DateTimeOffset.UtcNow, null);

    private static AuditEvent Audit(Guid? customerId, AuditResult result, DateTimeOffset at) =>
        new(Guid.NewGuid(), at, null, "u", customerId, null, "Download", "/a", null, result, "", "1.2.3.4", "c");

    [Fact]
    public async Task GrantStore_Set_TargetOwnedByOtherCustomer_Throws()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targets = new InMemoryTargetStore();
        await targets.UpsertAsync(Target(targetId, owner));
        var grants = new InMemoryGrantStore(targets);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grants.SetAsync(new Grant(other, targetId, AccessLevel.Read, "admin", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task GrantStore_Set_TargetOwnedByCustomer_RoundTripsAndRemoves()
    {
        var owner = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targets = new InMemoryTargetStore();
        await targets.UpsertAsync(Target(targetId, owner));
        var grants = new InMemoryGrantStore(targets);

        await grants.SetAsync(new Grant(owner, targetId, AccessLevel.Contribute, "admin", DateTimeOffset.UtcNow));
        Assert.Equal(AccessLevel.Contribute, (await grants.GetAsync(owner, targetId))!.Level);
        Assert.Single(await grants.ListForCustomerAsync(owner));

        await grants.RemoveAsync(owner, targetId);
        Assert.Null(await grants.GetAsync(owner, targetId));
    }

    [Fact]
    public async Task UserStore_FindByUpn_CaseInsensitive()
    {
        var store = new InMemoryUserStore();
        var id = Guid.NewGuid();
        await store.UpsertAsync(User(id, "Jane.Doe@Example.com"));

        var found = await store.FindByUpnAsync("jane.doe@example.COM");

        Assert.Equal(id, found?.Id);
    }

    [Fact]
    public async Task UserStore_FindByObjectId_ReturnsBound()
    {
        var store = new InMemoryUserStore();
        var id = Guid.NewGuid();
        await store.UpsertAsync(User(id, "a@x.com", "oid-1"));
        await store.UpsertAsync(User(Guid.NewGuid(), "b@x.com"));

        Assert.Equal(id, (await store.FindByObjectIdAsync("oid-1"))?.Id);
        Assert.Null(await store.FindByObjectIdAsync("missing"));
    }

    [Fact]
    public async Task AuditStore_Query_FiltersByCustomerAndResult()
    {
        var store = new InMemoryAuditStore();
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(Audit(c1, AuditResult.Success, now.AddMinutes(-3)));
        await store.AppendAsync(Audit(c1, AuditResult.Denied, now.AddMinutes(-2)));
        await store.AppendAsync(Audit(c2, AuditResult.Denied, now.AddMinutes(-1)));

        var rows = await store.QueryAsync(new AuditQuery(CustomerId: c1, Result: AuditResult.Denied));

        var row = Assert.Single(rows);
        Assert.Equal(c1, row.CustomerId);
        Assert.Equal(AuditResult.Denied, row.Result);
    }

    [Fact]
    public async Task AuditStore_Query_NewestFirstAndLimited()
    {
        var store = new InMemoryAuditStore();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await store.AppendAsync(Audit(null, AuditResult.Success, now.AddMinutes(i)));

        var rows = await store.QueryAsync(new AuditQuery(Limit: 2));

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].At > rows[1].At);
    }
}
