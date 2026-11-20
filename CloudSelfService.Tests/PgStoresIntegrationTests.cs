using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PgStoresIntegrationTests.ConnString))
            Skip = "CSSP_TEST_PG not set";
    }
}

public class PgStoresIntegrationTests
{
    internal static string? ConnString => Environment.GetEnvironmentVariable("CSSP_TEST_PG");

    private static async Task<PgsqlContext> CtxAsync()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString }).Build();
        var ctx = new PgsqlContext(cfg, NullLogger<PgsqlContext>.Instance);
        await ctx.EnsureSchemaAsync();
        return ctx;
    }

    private static Customer Cust() => new(Guid.NewGuid(), "c-" + Guid.NewGuid().ToString("N"), true, "n", null,
        CustomerCloud.AzureGov, CrossTenantStatus.Pending);

    private static StorageTarget Target(Guid? cid) => new(Guid.NewGuid(), cid, "t", "sub", "rg", "acct" + Guid.NewGuid().ToString("N"),
        StorageKind.BlobContainer, "cont", "core.usgovcloudapi.net", true);

    [PgFact]
    public async Task EnsureSchema_RunTwiceAndConcurrently_Succeeds()
    {
        await using var ctx = await CtxAsync();
        await ctx.EnsureSchemaAsync();
        await Task.WhenAll(ctx.EnsureSchemaAsync(), ctx.EnsureSchemaAsync());
    }

    [PgFact]
    public async Task CustomerAndTargetStores_RoundTrip()
    {
        await using var ctx = await CtxAsync();
        var cs = new PgCustomerStore(ctx);
        var ts = new PgTargetStore(ctx);
        var c = Cust();
        await cs.UpsertAsync(c);
        Assert.Equal(c, await cs.GetAsync(c.Id));
        await cs.UpsertAsync(c with { Notes = "x", HomeTenantId = "tid" });
        Assert.Equal("tid", (await cs.GetAsync(c.Id))!.HomeTenantId);
        Assert.Contains(await cs.ListAsync(), x => x.Id == c.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cs.UpsertAsync(c with { Id = Guid.NewGuid() }));

        var t = Target(c.Id);
        await ts.UpsertAsync(t);
        Assert.Equal(t, await ts.GetAsync(t.Id));
        var unowned = Target(null);
        await ts.UpsertAsync(unowned);
        Assert.Null((await ts.GetAsync(unowned.Id))!.CustomerId);
        Assert.Single(await ts.ListAsync(c.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ts.UpsertAsync(t with { Id = Guid.NewGuid() }));
    }

    [PgFact]
    public async Task UserStore_RoundTripAndDuplicateUpn()
    {
        await using var ctx = await CtxAsync();
        var us = new PgUserStore(ctx);
        var upn = "U" + Guid.NewGuid().ToString("N") + "@x.com";
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var u = new AppUser(Guid.NewGuid(), UserKind.Staff, null, upn, "Dn", null, StaffRole.Owner, true, created, null);
        await us.UpsertAsync(u);
        Assert.Equal(u, await us.GetAsync(u.Id));
        var oid = Guid.NewGuid().ToString();
        var signed = u with { EntraObjectId = oid, LastSignInAt = created.AddDays(1) };
        await us.UpsertAsync(signed);
        Assert.Equal(signed, await us.FindByObjectIdAsync(oid));
        Assert.Equal(u.Id, (await us.FindByUpnAsync(upn.ToLowerInvariant()))!.Id);
        Assert.Contains(await us.ListAsync(UserKind.Staff), x => x.Id == u.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            us.UpsertAsync(u with { Id = Guid.NewGuid(), Upn = upn.ToLowerInvariant(), EntraObjectId = null }));
    }

    [PgFact]
    public async Task GrantStore_OwnerRules()
    {
        await using var ctx = await CtxAsync();
        var cs = new PgCustomerStore(ctx);
        var ts = new PgTargetStore(ctx);
        var gs = new PgGrantStore(ctx);
        var a = Cust();
        var b = Cust();
        await cs.UpsertAsync(a);
        await cs.UpsertAsync(b);
        var t = Target(a.Id);
        await ts.UpsertAsync(t);

        var at = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
        await gs.SetAsync(new Grant(a.Id, t.Id, AccessLevel.Read, "adm", at));
        await gs.SetAsync(new Grant(a.Id, t.Id, AccessLevel.Full, "adm", at));
        Assert.Equal(new Grant(a.Id, t.Id, AccessLevel.Full, "adm", at), await gs.GetAsync(a.Id, t.Id));
        Assert.Single(await gs.ListForCustomerAsync(a.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gs.SetAsync(new Grant(b.Id, t.Id, AccessLevel.Read, "adm", at)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ts.UpsertAsync(t with { CustomerId = b.Id }));

        await gs.RemoveAsync(a.Id, t.Id);
        Assert.Null(await gs.GetAsync(a.Id, t.Id));
        await ts.UpsertAsync(t with { CustomerId = b.Id });
    }

    [PgFact]
    public async Task AuditStore_AppendAndQueryFilters()
    {
        await using var ctx = await CtxAsync();
        var store = new PgAuditStore(ctx);
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        AuditEvent E(Guid c, AuditResult r, int min, long? bytes) =>
            new(Guid.NewGuid(), now.AddMinutes(min), null, "u", c, null, "Download", "/p", bytes, r, "", "1.1.1.1", "corr");
        var denied = E(c1, AuditResult.Denied, -1, 5);
        await store.AppendAsync(E(c1, AuditResult.Success, -2, null));
        await store.AppendAsync(denied);
        await store.AppendAsync(E(c2, AuditResult.Denied, 0, null));

        var row = Assert.Single(await store.QueryAsync(new AuditQuery(CustomerId: c1, Result: AuditResult.Denied)));
        Assert.Equal(denied.Id, row.Id);
        Assert.Equal(5, row.Bytes);
        Assert.Null(row.UserId);
        Assert.Equal(2, (await store.QueryAsync(new AuditQuery(CustomerId: c1))).Count);
        Assert.Single(await store.QueryAsync(new AuditQuery(CustomerId: c1, From: now.AddMinutes(-1.5))));
        Assert.Single(await store.QueryAsync(new AuditQuery(Limit: 1)));
    }
}
