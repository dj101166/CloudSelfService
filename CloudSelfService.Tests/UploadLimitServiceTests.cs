using System.Net;
using System.Text.RegularExpressions;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Files;
using CloudSelfService.Services.Transfers;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudSelfService.Tests;

public class UploadLimitServiceTests
{
    private const long GiB = 1L << 30;
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly UploadSessionServiceTests.MutableTime _time = new(Start);

    private sealed class CountingStore : IUploadLimitStore
    {
        public readonly InMemoryUploadLimitStore Inner = new();
        public int Reads;

        public Task<UploadLimitOverride?> GetAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return Inner.GetAsync(ct);
        }

        public Task SetAsync(UploadLimitOverride o, CancellationToken ct = default) => Inner.SetAsync(o, ct);
        public Task ClearAsync(CancellationToken ct = default) => Inner.ClearAsync(ct);
    }

    private static IConfiguration Config(string? maxUploadBytes = null, string? ceiling = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transfers:MaxUploadBytes"] = maxUploadBytes,
            ["Transfers:MaxUploadBytesCeiling"] = ceiling,
        }).Build();

    private static UploadLimitOverride Override(long bytes, DateTimeOffset? expires = null) =>
        new(bytes, expires, Guid.NewGuid(), "ada@advancedutility.com", Start, "quarter end");

    private UploadLimitService Service(IUploadLimitStore store, IConfiguration? config = null) =>
        new(store, _time, config ?? Config());

    [Fact]
    public async Task Effective_NoOverride_IsDefault()
    {
        var state = await Service(new InMemoryUploadLimitStore()).GetStateAsync();

        Assert.Equal(100 * GiB, state.Effective);
        Assert.Equal(UploadLimitService.DefaultMaxUploadBytes, state.Default);
        Assert.Equal(UploadLimitService.DefaultCeilingBytes, state.Ceiling);
        Assert.Null(state.Override);
        Assert.False(state.OverrideActive);
    }

    [Fact]
    public async Task Effective_ActiveOverride_IsOverride()
    {
        var store = new InMemoryUploadLimitStore();
        await store.SetAsync(Override(150 * GiB, Start.AddDays(1)));

        var state = await Service(store).GetStateAsync();

        Assert.Equal(150 * GiB, state.Effective);
        Assert.True(state.OverrideActive);
        Assert.Equal(150 * GiB, state.Override!.Bytes);
    }

    [Fact]
    public async Task Effective_PermanentOverride_IsOverride()
    {
        var store = new InMemoryUploadLimitStore();
        await store.SetAsync(Override(2 * GiB));

        Assert.Equal(2 * GiB, (await Service(store).GetStateAsync()).Effective);
    }

    [Fact]
    public async Task Effective_OverrideExpired_FallsBackToDefault()
    {
        var store = new InMemoryUploadLimitStore();
        await store.SetAsync(Override(150 * GiB, Start.AddHours(1)));
        var svc = Service(store);
        Assert.Equal(150 * GiB, (await svc.GetStateAsync()).Effective);

        _time.Now = Start.AddHours(1);
        var state = await svc.GetStateAsync();

        Assert.Equal(100 * GiB, state.Effective);
        Assert.False(state.OverrideActive);
        Assert.NotNull(state.Override);
    }

    [Fact]
    public async Task Effective_OverrideAboveCeiling_ClampedToCeiling()
    {
        var store = new InMemoryUploadLimitStore();
        await store.SetAsync(Override(2048 * GiB));

        var state = await Service(store).GetStateAsync();

        Assert.Equal(UploadLimitService.DefaultCeilingBytes, state.Effective);
    }

    [Fact]
    public void LimitsFrom_CeilingAboveBlobMax_Clamped()
    {
        var limits = UploadLimitService.LimitsFrom(Config(ceiling: (2048 * GiB).ToString()));

        Assert.Equal(ChunkMath.MaxBlobBytes, limits.Ceiling);
        Assert.Equal(2048 * GiB, limits.ConfiguredCeiling);
        Assert.Equal(1_677_721_600_000L, limits.Ceiling);
    }

    [Fact]
    public void LimitsFrom_DefaultAboveCeiling_Clamped()
    {
        var limits = UploadLimitService.LimitsFrom(Config(maxUploadBytes: (300 * GiB).ToString(), ceiling: (200 * GiB).ToString()));

        Assert.Equal(200 * GiB, limits.Ceiling);
        Assert.Equal(200 * GiB, limits.Default);
        Assert.Equal(300 * GiB, limits.ConfiguredDefault);
    }

    [Fact]
    public async Task GetState_CachesForThirtySeconds()
    {
        var store = new CountingStore();
        var svc = Service(store);

        await svc.GetStateAsync();
        _time.Now = Start.AddSeconds(29);
        await svc.GetStateAsync();
        Assert.Equal(1, store.Reads);

        _time.Now = Start.AddSeconds(31);
        await svc.GetStateAsync();
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task Invalidate_NextReadGoesToTheStore()
    {
        var store = new CountingStore();
        var svc = Service(store);
        await svc.GetStateAsync();
        await store.SetAsync(Override(3 * GiB));

        Assert.Equal(100 * GiB, (await svc.GetStateAsync()).Effective);
        svc.Invalidate();

        Assert.Equal(3 * GiB, (await svc.GetStateAsync()).Effective);
        Assert.Equal(2, store.Reads);
    }

    [Theory]
    [InlineData(1L << 30, "1")]
    [InlineData(1_099_511_627_776L, "1024")]
    [InlineData(1_677_721_600_000L, "1562.5")]
    [InlineData(1_610_612_736L, "1.5")]
    [InlineData(1_000_000_000L, "0.93")]
    public void FormatGb_BinaryGbUpToTwoDecimals(long bytes, string expected) =>
        Assert.Equal(expected, UploadLimitService.FormatGb(bytes));

    [Fact]
    public void TooLargeMessage_UsesFormatGb() =>
        Assert.Equal("File is larger than the 1562.5 GB limit.", UploadSessionService.TooLargeMessage(1_677_721_600_000L));

    [Fact]
    public void Describe_NoActiveOverride_DefaultFromConfiguration()
    {
        var state = new UploadLimitState(100 * GiB, 100 * GiB, 1024 * GiB, Override(5 * GiB, Start.AddHours(-1)), false);

        Assert.Equal("Default from configuration", UploadLimitService.Describe(state, null));
    }

    [Fact]
    public void Describe_ActiveOverride_NamesSetterAndEndInViewerZone()
    {
        var zone = FileDisplay.ResolveZone(null, 600);
        var until = new UploadLimitState(5 * GiB, 100 * GiB, 1024 * GiB, Override(5 * GiB, Start.AddDays(2)), true);
        var forever = until with { Override = Override(5 * GiB) };

        Assert.Equal("Set by ada@advancedutility.com on 2026-11-25 19:00, until 2026-11-27 19:00", UploadLimitService.Describe(until, zone));
        Assert.Equal("Set by ada@advancedutility.com on 2026-11-25 09:00 UTC, permanent", UploadLimitService.Describe(forever, null));
    }

    [Fact]
    public void LocalToUtc_ZoneKnown_ConvertsWithZone()
    {
        var zone = FileDisplay.ResolveZone(null, 600);

        var utc = UploadLimitService.LocalToUtc(new DateTime(2026, 12, 1, 18, 0, 0), zone);

        Assert.Equal(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), utc);
    }

    [Theory]
    [InlineData(1L, 1L << 30)]
    [InlineData(150L, 150L << 30)]
    [InlineData(long.MaxValue, long.MaxValue)]
    [InlineData(8_589_934_592L, long.MaxValue)]
    [InlineData(-3L, -1L)]
    public void BytesFromGb_SaturatesInsteadOfOverflowing(long gb, long expected) =>
        Assert.Equal(expected, UploadLimitService.BytesFromGb(gb));

    [Fact]
    public void UtcToLocal_RoundTripsWithLocalToUtc()
    {
        var zone = FileDisplay.ResolveZone(null, -300);
        var at = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 12, 1, 3, 0, 0), UploadLimitService.UtcToLocal(at, zone));
        Assert.Equal(at, UploadLimitService.LocalToUtc(UploadLimitService.UtcToLocal(at, zone), zone));
        Assert.Equal(new DateTime(2026, 12, 1, 8, 0, 0), UploadLimitService.UtcToLocal(at, null));
    }

    [Fact]
    public void LocalToUtc_NoZone_TakenAsUtc() =>
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 18, 0, 0, TimeSpan.Zero),
            UploadLimitService.LocalToUtc(new DateTime(2026, 12, 1, 18, 0, 0), null));
}

public class SettingsPageTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void SettingsPage_RequiresStaffAdminPolicy()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "Pages", "Admin", "Settings.razor"));

        Assert.Matches(new Regex(@"^@page ""/admin/settings""\s*$", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^@attribute \[Authorize\(Policy = Policies\.StaffAdmin\)\]\s*$", RegexOptions.Multiline), text);
        Assert.Contains("<PageTitle>Settings — Advanced Cloud Self Service</PageTitle>", text);
    }

    [Fact]
    public void SettingsPage_EndTimeUsesBlazorsDatetimeLocalFormat()
    {
        // A custom @bind:format with a literal T renders an unquoted "T", which the browser rejects, so the value never binds.
        var text = File.ReadAllText(Path.Combine(Root(), "Pages", "Admin", "Settings.razor"));
        var input = Regex.Match(text, @"<input[^>]*data-testid=""upload-limit-until-at""[^>]*/>", RegexOptions.Singleline).Value;

        Assert.Contains("type=\"datetime-local\"", input);
        Assert.DoesNotContain("@bind:format", input);
    }

    [Fact]
    public void NavMenu_SettingsLinkFollowsStorageTargets()
    {
        var nav = File.ReadAllText(Path.Combine(Root(), "Shared", "NavMenu.razor"));

        var targets = nav.IndexOf("data-testid=\"nav-admin-targets\"", StringComparison.Ordinal);
        var settings = nav.IndexOf("data-testid=\"nav-admin-settings\"", StringComparison.Ordinal);
        var staff = nav.IndexOf("data-testid=\"nav-admin-staff\"", StringComparison.Ordinal);
        Assert.True(targets > 0 && settings > targets && settings < staff);
        Assert.Contains("href=\"admin/settings\" data-testid=\"nav-admin-settings\"", nav);
    }

    [Fact]
    public async Task StaffAdmin_SeesTheLimitCard()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("staff-admin");

        var response = await client.GetAsync("/admin/settings");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var id in new[] { "upload-limit-effective", "upload-limit-source", "upload-limit-gb", "upload-limit-permanent",
                     "upload-limit-until", "upload-limit-until-at", "upload-limit-note", "upload-limit-save" })
            Assert.Contains($"data-testid=\"{id}\"", html);
        Assert.DoesNotContain("data-testid=\"upload-limit-revert\"", html);
        Assert.Contains("Default from configuration", html);
    }

    [Fact]
    public async Task Staff_DeniedTheSettingsPage()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("staff");

        var html = await (await client.GetAsync("/admin/settings")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("data-testid=\"upload-limit-save\"", html);
    }
}
