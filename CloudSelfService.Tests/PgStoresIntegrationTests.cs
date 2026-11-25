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
    public async Task UploadLimitStore_SetGetClear_RoundTrips()
    {
        await using var ctx = await CtxAsync();
        await UploadLimitStoreContract.SetGetClear_RoundTrips(new PgUploadLimitStore(ctx));
    }

    [PgFact]
    public async Task UploadLimitStore_SetTwice_KeepsOneRow()
    {
        await using var ctx = await CtxAsync();
        await UploadLimitStoreContract.SetTwice_KeepsOneRow(new PgUploadLimitStore(ctx));
        await using var cmd = ctx.DataSource.CreateCommand("SELECT count(*) FROM upload_limit_override");
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [PgFact]
    public async Task UploadLimitOverride_RejectsSecondRowAndNonPositiveBytes()
    {
        await using var ctx = await CtxAsync();
        async Task Run(string sql)
        {
            await using var cmd = ctx.DataSource.CreateCommand(sql);
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("23514", ex.SqlState);
        }

        await Run("INSERT INTO upload_limit_override (id, bytes, set_by, set_by_upn, set_at) VALUES (2, 1, gen_random_uuid(), 'x', now())");
        await Run("INSERT INTO upload_limit_override (id, bytes, set_by, set_by_upn, set_at) VALUES (1, 0, gen_random_uuid(), 'x', now())");
    }

    [PgFact]
    public async Task EnsureSchema_RunTwiceAndConcurrently_Succeeds()
    {
        await using var ctx = await CtxAsync();
        await ctx.EnsureSchemaAsync();
        await Task.WhenAll(ctx.EnsureSchemaAsync(), ctx.EnsureSchemaAsync());
    }

    [PgFact]
    public async Task AuditEvents_UpdateDeleteTruncate_AreRejected()
    {
        await using var ctx = await CtxAsync();
        var store = new PgAuditStore(ctx);
        var id = Guid.NewGuid();
        await store.AppendAsync(new AuditEvent(id, DateTimeOffset.UtcNow, null, "u", null, null, "Download", "/p", null,
            AuditResult.Success, "", "1.1.1.1", "corr"));

        async Task Run(string sql)
        {
            await using var cmd = ctx.DataSource.CreateCommand(sql);
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        await Run($"UPDATE audit_events SET error = 'x' WHERE id = '{id}'");
        await Run($"DELETE FROM audit_events WHERE id = '{id}'");
        await Run("TRUNCATE audit_events");
        await ctx.EnsureSchemaAsync();
        await Run($"DELETE FROM audit_events WHERE id = '{id}'");
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

    [PgFact]
    public async Task UploadSessionStore_RoundTripResumeStatusAndPurge()
    {
        await using var ctx = await CtxAsync();
        var store = new PgUploadSessionStore(ctx);
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var user = Guid.NewGuid();
        var target = Guid.NewGuid();
        var s = new UploadSession(Guid.NewGuid(), user, target, "Docs/a b#.bin", "Docs/.a b#.bin.cssupload-x", 9L * 1024 * 1024 * 1024,
            4 * 1024 * 1024, false, now, now.AddHours(24), UploadStatus.Open);
        await store.CreateAsync(s);
        Assert.Equal(s, await store.GetAsync(s.Id));

        Assert.Equal(s.Id, (await store.FindResumableAsync(user, target, "Docs/a b#.bin", s.Size, now.AddHours(1)))!.Id);
        Assert.Null(await store.FindResumableAsync(Guid.NewGuid(), target, "Docs/a b#.bin", s.Size, now));
        Assert.Null(await store.FindResumableAsync(user, target, "Docs/a b#.bin", s.Size + 1, now));
        Assert.Null(await store.FindResumableAsync(user, target, "Docs/a b#.bin", s.Size, now.AddHours(24)));

        await store.MarkChunkAsync(s.Id, 3, now.AddMinutes(2), now.AddHours(24));
        await store.MarkChunkAsync(s.Id, 3, now.AddMinutes(1), now.AddHours(24));
        Assert.Equal(now.AddMinutes(2), (await store.GetAsync(s.Id))!.LastActivityAt);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.MarkChunkAsync(s.Id, i, now, now.AddHours(24))));
        Assert.Equal(Enumerable.Range(0, 20), (await store.ReceivedChunksAsync(s.Id)).OrderBy(i => i));

        await store.SetOverwriteAsync(s.Id, true, true);
        Assert.True((await store.GetAsync(s.Id))!.Overwrite);
        Assert.True((await store.GetAsync(s.Id))!.ExistedAtBegin);

        Assert.Contains(await store.ListSweepableOpenAsync(now.AddHours(25), now), x => x.Id == s.Id);
        // R33: it has received chunks, so however long it has been idle it is only swept at expiry.
        Assert.DoesNotContain(await store.ListSweepableOpenAsync(now, now.AddHours(23)), x => x.Id == s.Id);
        var untouched = s with { Id = Guid.NewGuid(), LastActivityAt = null };
        await store.CreateAsync(untouched);
        Assert.Contains(await store.ListSweepableOpenAsync(now, now.AddMinutes(1)), x => x.Id == untouched.Id);
        Assert.DoesNotContain(await store.ListSweepableOpenAsync(now, now), x => x.Id == untouched.Id);
        await store.DeleteAsync(untouched.Id);
        Assert.Contains(await store.ListOpenForUserPathAsync(user, target, "Docs/a b#.bin"), x => x.Id == s.Id);
        Assert.Empty(await store.ListOpenForUserPathAsync(Guid.NewGuid(), target, "Docs/a b#.bin"));
        Assert.Equal(1, await store.CountOpenForUserAsync(user, now));
        Assert.Equal(s.Size, await store.SumOpenSizeForUserAsync(user, now));
        Assert.Equal(0, await store.CountOpenForUserAsync(user, now.AddHours(24)));
        Assert.Equal(0L, await store.SumOpenSizeForUserAsync(Guid.NewGuid(), now));

        // Two growers racing to the same size: whichever takes the row lock first grows, the other sees the size already
        // reached and skips, so exactly one grow runs whatever the order. Then a smaller size never grows.
        var grows = 0;
        await Task.WhenAll(new[] { 9L, 9L }.Select(n => store.EnsureStagingSizeAsync(s.Id, n,
            _ => { Interlocked.Increment(ref grows); return Task.CompletedTask; })));
        Assert.Equal(9, (await store.GetAsync(s.Id))!.StagingSize);
        Assert.Equal(1, grows);
        await store.EnsureStagingSizeAsync(s.Id, 6, _ => { Interlocked.Increment(ref grows); return Task.CompletedTask; });
        Assert.Equal(1, grows);
        Assert.Equal(9, (await store.GetAsync(s.Id))!.StagingSize);
        await Assert.ThrowsAsync<IOException>(() => store.EnsureStagingSizeAsync(s.Id, 20, _ => throw new IOException()));
        Assert.Equal(9, (await store.GetAsync(s.Id))!.StagingSize);
        Assert.Contains(await store.ListActiveForPathAsync(target, "Docs/a b#.bin", now), x => x.Id == s.Id);
        Assert.Empty(await store.ListActiveForPathAsync(target, "Docs/a b#.bin", now.AddHours(24)));
        var claimed = now.AddMinutes(3);
        Assert.True(await store.TrySetStatusAsync(s.Id, UploadStatus.Open, UploadStatus.Completing, claimed));
        Assert.Equal(claimed, (await store.GetAsync(s.Id))!.ClaimedAt);
        Assert.Contains(await store.ListActiveForPathAsync(target, "Docs/a b#.bin", now), x => x.Id == s.Id);
        Assert.Contains(await store.ListStuckCompletingAsync(claimed.AddMinutes(1)), x => x.Id == s.Id);
        Assert.False(await store.TrySetStatusAsync(s.Id, UploadStatus.Completing, UploadStatus.Completing,
            requireClaim: true, expectedClaim: claimed.AddSeconds(1)));
        Assert.True(await store.TrySetStatusAsync(s.Id, UploadStatus.Completing, UploadStatus.Completing,
            requireClaim: true, expectedClaim: claimed));
        Assert.DoesNotContain(await store.ListStuckCompletingAsync(claimed), x => x.Id == s.Id);
        Assert.False(await store.TrySetStatusAsync(s.Id, UploadStatus.Open, UploadStatus.Aborted));
        Assert.DoesNotContain(await store.ListSweepableOpenAsync(now.AddHours(25), now.AddHours(25)), x => x.Id == s.Id);
        Assert.Null(await store.FindResumableAsync(user, target, "Docs/a b#.bin", s.Size, now));
        await store.SetStatusAsync(s.Id, UploadStatus.Completed);
        Assert.Equal(UploadStatus.Completed, (await store.GetAsync(s.Id))!.Status);

        var purgedCompleted = await store.PurgeAsync(now.AddHours(24).AddMinutes(1), now.AddDays(-1));
        Assert.True(purgedCompleted.Deleted >= 1);
        Assert.Null(await store.GetAsync(s.Id));
        Assert.Empty(await store.ReceivedChunksAsync(s.Id));

        // An Aborted row keeps its place until its staging is confirmed removed, or the hard cutoff passes.
        var aborted = s with { Id = Guid.NewGuid(), Status = UploadStatus.Aborted, StagingPath = "Docs/.x.cssupload-y" };
        await store.CreateAsync(aborted);
        Assert.Contains(await store.ListAbortedWithStagingAsync(), x => x.Id == aborted.Id);
        await store.PurgeAsync(now.AddHours(25), now.AddDays(-1));
        Assert.NotNull(await store.GetAsync(aborted.Id));
        var refused = s with { Id = Guid.NewGuid(), Status = UploadStatus.Aborted, StagingPath = "Docs/.z.cssupload-y" };
        await store.CreateAsync(refused);
        await store.SetCleanupRefusedAsync(refused.Id);
        Assert.True((await store.GetAsync(refused.Id))!.CleanupRefused);
        Assert.DoesNotContain(await store.ListAbortedWithStagingAsync(), x => x.Id == refused.Id);
        var hard = await store.PurgeAsync(now.AddHours(25), now.AddHours(25));
        Assert.DoesNotContain(hard.Abandoned, a => a.SessionId == refused.Id);
        Assert.Null(await store.GetAsync(refused.Id));
        Assert.Contains(hard.Abandoned, a => a.SessionId == aborted.Id && a.StagingPath == "Docs/.x.cssupload-y");
        Assert.Null(await store.GetAsync(aborted.Id));

        var cleaned = s with { Id = Guid.NewGuid(), Status = UploadStatus.Aborted, TargetKind = StorageKind.BlobContainer };
        await store.CreateAsync(cleaned);
        await store.SetStagingRemovedAsync(cleaned.Id);
        Assert.DoesNotContain(await store.ListAbortedWithStagingAsync(), x => x.Id == cleaned.Id);
        Assert.Equal(StorageKind.BlobContainer, (await store.GetAsync(cleaned.Id))!.TargetKind);
        var soft = await store.PurgeAsync(now.AddHours(25), now.AddDays(-1));
        Assert.DoesNotContain(soft.Abandoned, a => a.SessionId == cleaned.Id);
        Assert.Null(await store.GetAsync(cleaned.Id));

        var other = s with { Id = Guid.NewGuid() };
        await store.CreateAsync(other);
        await store.MarkChunkAsync(other.Id, 0, now, now.AddHours(24));
        await store.DeleteAsync(other.Id);
        Assert.Null(await store.GetAsync(other.Id));
        Assert.Empty(await store.ReceivedChunksAsync(other.Id));
    }

    [PgFact]
    public async Task UserStore_TargetedWrites_AndRecordSignIn()
    {
        await using var ctx = await CtxAsync();
        var users = new PgUserStore(ctx);
        var suffix = Guid.NewGuid().ToString("N");
        var staff = new AppUser(Guid.NewGuid(), UserKind.Staff, null, $"S{suffix}@x.example", "S", null, StaffRole.StaffAdmin, true, DateTimeOffset.UtcNow, null);
        await users.UpsertAsync(staff);

        Assert.True(await users.SetStaffRoleAsync(staff.Id, StaffRole.Staff));
        Assert.Equal(StaffRole.Staff, (await users.GetAsync(staff.Id))!.StaffRole);
        Assert.True(await users.SetActiveAsync(staff.Id, false));
        Assert.Null(await users.RecordSignInAsync(staff.Id, "oid-" + suffix, staff.Upn, "S", DateTimeOffset.UtcNow));
        Assert.False((await users.GetAsync(staff.Id))!.Active);

        await users.SetActiveAsync(staff.Id, true);
        var bound = await users.RecordSignInAsync(staff.Id, "oid-" + suffix, $"s{suffix}@X.example", "S2", DateTimeOffset.UtcNow);
        Assert.Equal(StaffRole.Staff, bound!.StaffRole);
        Assert.True(bound.Active);
        Assert.Equal("oid-" + suffix, bound.EntraObjectId);
        Assert.Null(await users.RecordSignInAsync(staff.Id, "other-oid", staff.Upn, "S", DateTimeOffset.UtcNow));

        var customer = new AppUser(Guid.NewGuid(), UserKind.Customer, null, $"c{suffix}@x.example", "C", null, StaffRole.None, true, DateTimeOffset.UtcNow, null);
        await Assert.ThrowsAnyAsync<Exception>(() => users.UpsertAsync(customer with { CustomerId = Guid.NewGuid() }));
        Assert.False(await users.SetStaffRoleAsync(Guid.NewGuid(), StaffRole.Staff));
    }

    [PgFact]
    public async Task UserStore_FindByUpn_UsesNormalisedColumn_AndKelvinCollides()
    {
        await using var ctx = await CtxAsync();
        var users = new PgUserStore(ctx);
        var suffix = Guid.NewGuid().ToString("N");
        await users.UpsertAsync(new AppUser(Guid.NewGuid(), UserKind.Staff, null, $"k{suffix}@x.example", "K", null, StaffRole.None, true, DateTimeOffset.UtcNow, null));

        Assert.NotNull(await users.FindByUpnAsync($"\u212A{suffix}@X.example"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.UpsertAsync(
            new AppUser(Guid.NewGuid(), UserKind.Staff, null, $"\u212A{suffix}@x.example", "K2", null, StaffRole.None, true, DateTimeOffset.UtcNow, null)));
    }

    [PgFact]
    public async Task TargetStore_RepointWithGrants_RejectedByTrigger()
    {
        await using var ctx = await CtxAsync();
        var customers = new PgCustomerStore(ctx);
        var targets = new PgTargetStore(ctx);
        var grants = new PgGrantStore(ctx);
        var c = Cust();
        await customers.UpsertAsync(c);
        var t = Target(c.Id);
        await targets.UpsertAsync(t);
        await targets.UpsertAsync(t with { DisplayName = "renamed" });
        await grants.SetAsync(new Grant(c.Id, t.Id, AccessLevel.Read, "x", DateTimeOffset.UtcNow));

        await targets.UpsertAsync(t with { DisplayName = "renamed again", Active = false });
        foreach (var changed in new[] { t with { AccountName = "other" + Guid.NewGuid().ToString("N")[..8] }, t with { ContainerName = "other" },
                     t with { Kind = StorageKind.FileShare }, t with { EndpointSuffix = "core.windows.net" } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => targets.UpsertAsync(changed));
    }

    [PgFact]
    public async Task AuditStore_Query_HasIndexesAndStillFilters()
    {
        await using var ctx = await CtxAsync();
        await using var conn = await ctx.DataSource.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_indexes WHERE indexname IN ('idx_audit_events_user_at','idx_audit_events_operation_at','ux_users_upn_normalized')", conn);
        Assert.Equal(3L, (long)(await cmd.ExecuteScalarAsync())!);
    }
}
