using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// Behaviour every <see cref="IUploadSessionStore"/> must share. The scenarios live here once; the in-memory and
/// PostgreSQL subclasses supply the store and carry the test attributes (the PostgreSQL ones skip without CSSP_TEST_PG).
/// </summary>
public abstract class UploadSessionStoreContract
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    protected abstract Task<IUploadSessionStore> CreateStoreAsync();

    private static UploadSession Session(Guid user, string status = UploadStatus.Open, long size = 10, DateTimeOffset? expires = null,
        DateTimeOffset? claimed = null) =>
        new(Guid.NewGuid(), user, Guid.NewGuid(), "Docs/a.bin", "Docs/.a.bin.cssupload-x", size, 4 * 1024 * 1024, false, Now,
            expires ?? Now.AddHours(24), status, claimed);

    protected async Task RequireClaimWithNullClaim_Matches()
    {
        var store = await CreateStoreAsync();
        var open = Session(Guid.NewGuid());
        await store.CreateAsync(open);

        Assert.True(await store.TrySetStatusAsync(open.Id, UploadStatus.Open, UploadStatus.Aborted, requireClaim: true, expectedClaim: null));
        Assert.Equal(UploadStatus.Aborted, (await store.GetAsync(open.Id))!.Status);

        var neverClaimed = Session(Guid.NewGuid(), UploadStatus.Completing);
        await store.CreateAsync(neverClaimed);
        Assert.Contains(await store.ListStuckCompletingAsync(Now), s => s.Id == neverClaimed.Id);
        Assert.False(await store.TrySetStatusAsync(neverClaimed.Id, UploadStatus.Completing, UploadStatus.Aborted,
            requireClaim: true, expectedClaim: Now));
        Assert.True(await store.TrySetStatusAsync(neverClaimed.Id, UploadStatus.Completing, UploadStatus.Aborted,
            requireClaim: true, expectedClaim: null));
    }

    protected async Task CompletingSessionsCountAgainstCaps()
    {
        var store = await CreateStoreAsync();
        var user = Guid.NewGuid();
        await store.CreateAsync(Session(user, UploadStatus.Open, 40));
        await store.CreateAsync(Session(user, UploadStatus.Completing, 60));
        await store.CreateAsync(Session(user, UploadStatus.Completed, 500));
        await store.CreateAsync(Session(user, UploadStatus.Aborted, 500));
        await store.CreateAsync(Session(user, UploadStatus.Completing, 7, expires: Now.AddMinutes(-1)));

        Assert.Equal(2, await store.CountOpenForUserAsync(user, Now));
        Assert.Equal(100, await store.SumOpenSizeForUserAsync(user, Now));
    }

    protected async Task FirstPieceMd5_RoundTrips()
    {
        var store = await CreateStoreAsync();
        var user = Guid.NewGuid();
        var with = Session(user) with { FirstPieceMd5 = "1B2M2Y8AsgTpgAmY7PhCfg==" };
        var without = Session(user);
        await store.CreateAsync(with);
        await store.CreateAsync(without);

        Assert.Equal("1B2M2Y8AsgTpgAmY7PhCfg==", (await store.GetAsync(with.Id))!.FirstPieceMd5);
        Assert.Null((await store.GetAsync(without.Id))!.FirstPieceMd5);
        Assert.Equal(with.FirstPieceMd5,
            (await store.FindResumableAsync(user, with.TargetId, with.FinalPath, with.Size, Now))!.FirstPieceMd5);
    }

    protected async Task PurgeNeverDeletesOpenOrCompletingRows_EvenPastHardCutoff()
    {
        var store = await CreateStoreAsync();
        var user = Guid.NewGuid();
        var longAgo = Now.AddYears(-1);
        var open = Session(user, UploadStatus.Open, expires: longAgo);
        var completing = Session(user, UploadStatus.Completing, expires: longAgo, claimed: longAgo);
        var completed = Session(user, UploadStatus.Completed, expires: longAgo);
        await store.CreateAsync(open);
        await store.CreateAsync(completing);
        await store.CreateAsync(completed);
        await store.MarkChunkAsync(open.Id, 0, longAgo, longAgo);

        await store.PurgeAsync(Now.AddYears(1), Now.AddYears(1));

        Assert.NotNull(await store.GetAsync(open.Id));
        Assert.NotNull(await store.GetAsync(completing.Id));
        Assert.Equal(new[] { 0 }, await store.ReceivedChunksAsync(open.Id));
        Assert.Null(await store.GetAsync(completed.Id));
    }

    protected async Task LaterExpiry_Extends()
    {
        var store = await CreateStoreAsync();
        var s = Session(Guid.NewGuid());
        await store.CreateAsync(s);

        await store.MarkChunkAsync(s.Id, 0, Now.AddHours(1), Now.AddHours(30));

        Assert.Equal(Now.AddHours(30), (await store.GetAsync(s.Id))!.ExpiresAt);
    }

    protected async Task EarlierExpiry_DoesNotShorten()
    {
        var store = await CreateStoreAsync();
        var s = Session(Guid.NewGuid());
        await store.CreateAsync(s);

        await store.MarkChunkAsync(s.Id, 0, Now.AddHours(1), Now.AddHours(2));

        Assert.Equal(Now.AddHours(24), (await store.GetAsync(s.Id))!.ExpiresAt);
    }

    protected async Task NonOpenSession_ExpiryUnchanged()
    {
        var store = await CreateStoreAsync();
        var s = Session(Guid.NewGuid(), UploadStatus.Completing, claimed: Now);
        await store.CreateAsync(s);

        await store.MarkChunkAsync(s.Id, 0, Now.AddHours(1), Now.AddHours(30));

        var after = (await store.GetAsync(s.Id))!;
        Assert.Equal(Now.AddHours(24), after.ExpiresAt);
        Assert.Equal(new[] { 0 }, await store.ReceivedChunksAsync(s.Id));
        Assert.Equal(Now.AddHours(1), after.LastActivityAt);
    }
}

public class InMemoryUploadSessionStoreContractTests : UploadSessionStoreContract
{
    protected override Task<IUploadSessionStore> CreateStoreAsync() => Task.FromResult<IUploadSessionStore>(new InMemoryUploadSessionStore());

    [Fact]
    public Task UploadSessionStore_RequireClaimWithNullClaim_Matches() => RequireClaimWithNullClaim_Matches();

    [Fact]
    public Task UploadSessionStore_CompletingSessionsCountAgainstCaps() => CompletingSessionsCountAgainstCaps();

    [Fact]
    public Task PurgeAsync_NeverDeletesOpenOrCompletingRows_EvenPastHardCutoff() => PurgeNeverDeletesOpenOrCompletingRows_EvenPastHardCutoff();

    [Fact]
    public Task UploadSessionStore_FirstPieceMd5_RoundTrips() => FirstPieceMd5_RoundTrips();

    [Fact]
    public Task MarkChunk_LaterExpiry_Extends() => LaterExpiry_Extends();

    [Fact]
    public Task MarkChunk_EarlierExpiry_DoesNotShorten() => EarlierExpiry_DoesNotShorten();

    [Fact]
    public Task MarkChunk_NonOpenSession_ExpiryUnchanged() => NonOpenSession_ExpiryUnchanged();
}

public class PgUploadSessionStoreContractTests : UploadSessionStoreContract
{
    protected override async Task<IUploadSessionStore> CreateStoreAsync()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = PgStoresIntegrationTests.ConnString }).Build();
        var ctx = new PgsqlContext(cfg, NullLogger<PgsqlContext>.Instance);
        await ctx.EnsureSchemaAsync();
        return new PgUploadSessionStore(ctx);
    }

    [PgFact]
    public Task UploadSessionStore_RequireClaimWithNullClaim_Matches() => RequireClaimWithNullClaim_Matches();

    [PgFact]
    public Task UploadSessionStore_CompletingSessionsCountAgainstCaps() => CompletingSessionsCountAgainstCaps();

    [PgFact]
    public Task PurgeAsync_NeverDeletesOpenOrCompletingRows_EvenPastHardCutoff() => PurgeNeverDeletesOpenOrCompletingRows_EvenPastHardCutoff();

    [PgFact]
    public Task UploadSessionStore_FirstPieceMd5_RoundTrips() => FirstPieceMd5_RoundTrips();

    [PgFact]
    public Task MarkChunk_LaterExpiry_Extends() => LaterExpiry_Extends();

    [PgFact]
    public Task MarkChunk_EarlierExpiry_DoesNotShorten() => EarlierExpiry_DoesNotShorten();

    [PgFact]
    public Task MarkChunk_NonOpenSession_ExpiryUnchanged() => NonOpenSession_ExpiryUnchanged();
}
