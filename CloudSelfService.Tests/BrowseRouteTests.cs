using System.Net;
using System.Text.RegularExpressions;
using CloudSelfService.Models;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// The Files pages, through the real app's prerender. A target the caller cannot reach renders Access Denied in place:
/// the page must neither navigate (a navigation after an await crashes the host) nor throw.
/// </summary>
public sealed class BrowseRouteTests : IDisposable
{
    private readonly TestAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Browse_OtherCustomersTarget_RendersAccessDenied_Not500()
    {
        await _factory.CreateClientAs("customer-full");
        var foreign = await _factory.DemoTargetAsync("Full");
        var reader = await _factory.CreateClientAs("customer-read");

        var response = await reader.GetAsync($"/files/{foreign.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-testid=\"access-denied\"", html);
        Assert.DoesNotContain("Demo share (Full)", html);
        Assert.DoesNotContain("Welcome.txt", html);
    }

    [Fact]
    public async Task Browse_OwnTarget_ListsSeededItems()
    {
        var reader = await _factory.CreateClientAs("customer-read");
        var own = await _factory.DemoTargetAsync("Read");

        var html = await (await reader.GetAsync($"/files/{own.Id}")).Content.ReadAsStringAsync();

        Assert.Contains("data-testid=\"browse-breadcrumb\"", html);
        Assert.Contains("Welcome.txt", html);
        Assert.Contains("Documents", html);
        Assert.DoesNotContain("data-testid=\"action-upload\"", html);
        Assert.DoesNotContain("data-testid=\"action-delete\"", html);
    }

    [Fact]
    public async Task Home_CustomerRead_ShowsOnlyOwnTarget()
    {
        await _factory.CreateClientAs("customer-full");
        var reader = await _factory.CreateClientAs("customer-read");

        var response = await reader.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Demo share (Read)", html);
        Assert.DoesNotContain("Demo share (Full)", html);
        Assert.Single(Regex.Matches(html, "data-testid=\"target-card\""));
    }

    [Fact]
    public async Task Home_StaffOwner_GroupsByCustomer()
    {
        await _factory.CreateClientAs("customer-read");
        await _factory.CreateClientAs("customer-full");
        var owner = await _factory.CreateClientAs("staff-owner");

        var html = await (await owner.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("Demo Customer (Read)", html);
        Assert.Contains("Demo Customer (Full)", html);
        Assert.Contains("data-testid=\"target-filter\"", html);
    }

    [Fact]
    public async Task Help_SignedIn_Renders()
    {
        var reader = await _factory.CreateClientAs("customer-read");

        var response = await reader.GetAsync("/help");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Contribute", html);
        Assert.Contains("same file again", html);
    }

    [Fact]
    public async Task Browse_StoreDown_ShowsUnavailableAlert_Not500()
    {
        using var failing = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ITargetStore, ThrowingTargetStore>()));
        var client = failing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await client.GetAsync("/dev/signin?as=staff-owner");

        var response = await client.GetAsync($"/files/{Guid.NewGuid()}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The service is temporarily unavailable.", html);
    }

    [Fact]
    public async Task Home_StoreDown_ShowsUnavailableAlert_Not500()
    {
        using var failing = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ITargetStore, ThrowingTargetStore>()));
        var client = failing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await client.GetAsync("/dev/signin?as=staff-owner");

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The service is temporarily unavailable.", html);
    }

    [Fact]
    public async Task Home_NoTargets_ShowsEmptyState()
    {
        var owner = await _factory.CreateClientAs("staff-owner");

        var html = await (await owner.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("No storage has been shared with you yet. Contact Advanced support if you expected to see something here.", html);
    }

    [Fact]
    public async Task Browse_FolderLargerThanOnePage_OffersLoadMore()
    {
        var reader = await _factory.CreateClientAs("customer-read");
        var own = await _factory.DemoTargetAsync("Read");
        for (var i = 0; i < 250; i++)
            await _factory.SeedFileAsync(own, $"Big/f{i:D3}.txt", new byte[] { 1 });

        var html = await (await reader.GetAsync($"/files/{own.Id}/Big")).Content.ReadAsStringAsync();

        Assert.Contains("data-testid=\"load-more\"", html);
        Assert.Equal(StorageAccessService.PageSize, Regex.Matches(html, "data-testid=\"browse-row\"").Count);
    }

    [Fact]
    public async Task Browse_FolderOfOnePage_HasNoLoadMore()
    {
        var reader = await _factory.CreateClientAs("customer-read");
        var own = await _factory.DemoTargetAsync("Read");

        var html = await (await reader.GetAsync($"/files/{own.Id}")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("data-testid=\"load-more\"", html);
    }

    [Fact]
    public async Task Browse_GrantRevoked_ListDeniedRefreshesLevel_RendersAccessDenied()
    {
        var reader = await _factory.CreateClientAs("customer-read");
        var own = await _factory.DemoTargetAsync("Read");
        await _factory.Services.GetRequiredService<IGrantStore>().RemoveAsync(own.CustomerId!.Value, own.Id);

        var response = await reader.GetAsync($"/files/{own.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-testid=\"access-denied\"", html);
        Assert.DoesNotContain("Welcome.txt", html);
    }

    [Fact]
    public async Task Browse_BreadcrumbAnchors_CarryTestIds()
    {
        var reader = await _factory.CreateClientAs("customer-read");
        var own = await _factory.DemoTargetAsync("Read");

        var html = await (await reader.GetAsync($"/files/{own.Id}/Reports/2026")).Content.ReadAsStringAsync();

        // Files, the target and Reports are links; the current folder (2026) is not.
        Assert.Equal(3, Regex.Matches(html, "data-testid=\"browse-crumb\"").Count);
    }

    [Fact]
    public async Task Help_SupportContact_ComesFromConfig()
    {
        using var configured = _factory.WithWebHostBuilder(b => b.UseSetting("Support:Contact", "the Example service desk"));
        var client = configured.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await client.GetAsync("/dev/signin?as=customer-read");

        var html = await (await client.GetAsync("/help")).Content.ReadAsStringAsync();

        Assert.Contains("the Example service desk", html);
    }

    [Fact]
    public async Task Help_SupportContact_DefaultsToAccountTeam()
    {
        var reader = await _factory.CreateClientAs("customer-read");

        var html = await (await reader.GetAsync("/help")).Content.ReadAsStringAsync();

        Assert.Contains("your Advanced account team", html);
    }

    private static async Task<Guid> BeginUploadAsync(HttpClient client, Guid targetId, string folder, string name, long size)
    {
        using var tokenJson = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/api/antiforgery")).Content.ReadAsStringAsync());
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/uploads")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { targetId, folder, fileName = name, size, overwrite = false }),
        };
        request.Headers.Add("RequestVerificationToken", tokenJson.RootElement.GetProperty("token").GetString());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sessionId").GetGuid();
    }

    [Fact]
    public async Task Browse_UnfinishedUploadInThisFolder_ShowsWaitingForFileRow()
    {
        var full = await _factory.CreateClientAs("customer-full");
        var own = await _factory.DemoTargetAsync("Full");
        var session = await BeginUploadAsync(full, own.Id, "Documents", "big.bak", 10 * 1024 * 1024);

        var html = await (await full.GetAsync($"/files/{own.Id}/Documents")).Content.ReadAsStringAsync();

        Assert.Contains("data-status=\"waiting-file\"", html);
        Assert.Contains($"data-session-id=\"{session}\"", html);
        Assert.Contains("Waiting for the file: 0% received", html);
        Assert.Contains("data-testid=\"upload-select-file\"", html);
        Assert.Matches(new Regex("<label[^>]*for=\"upload-resume-[0-9a-f]{32}\"[^>]*data-testid=\"upload-select-file\""), html);
        var input = Regex.Match(html, "<input type=\"file\" id=\"upload-resume-[0-9a-f]{32}\"[^>]*>");
        Assert.True(input.Success);
        Assert.DoesNotContain("accept=", input.Value);
    }

    [Fact]
    public async Task Browse_Prerender_RendersSelectFileDisabled_UntilInteractive()
    {
        var full = await _factory.CreateClientAs("customer-full");
        var own = await _factory.DemoTargetAsync("Full");
        await BeginUploadAsync(full, own.Id, "Documents", "big.bak", 10 * 1024 * 1024);

        var html = await (await full.GetAsync($"/files/{own.Id}/Documents")).Content.ReadAsStringAsync();

        Assert.Matches(@"<input type=""file""[^>]*data-testid=""upload-select-input""[^>]*disabled", html);
        Assert.Matches(@"<label[^>]*data-testid=""upload-select-file""[^>]*aria-disabled=""true""", html);
    }

    [Fact]
    public async Task Home_UnfinishedUploadWithNoPiece_ShowsIdleDiscardTime_NotExpiry()
    {
        var full = await _factory.CreateClientAs("customer-full");
        var own = await _factory.DemoTargetAsync("Full");
        var user = (await _factory.Services.GetRequiredService<IUserStore>().ListAsync(UserKind.Customer, own.CustomerId)).Single();
        var sessions = _factory.Services.GetRequiredService<IUploadSessionStore>();
        var created = new DateTimeOffset(DateTimeOffset.UtcNow.Year + 1, 3, 4, 10, 0, 0, TimeSpan.Zero);
        var idle = new UploadSession(Guid.NewGuid(), user.Id, own.Id, "idle.bak", ".idle.bak.cssupload-x", 10, 4 * 1024 * 1024,
            false, created, created.AddHours(24), UploadStatus.Open);
        var active = new UploadSession(Guid.NewGuid(), user.Id, own.Id, "active.bak", ".active.bak.cssupload-y", 10, 4 * 1024 * 1024,
            false, created, created.AddHours(24), UploadStatus.Open);
        await sessions.CreateAsync(idle);
        await sessions.CreateAsync(active);
        await sessions.MarkChunkAsync(active.Id, 0, created.AddMinutes(5), created.AddHours(30));

        var html = await (await full.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Matches(new Regex($"data-session-id=\"{idle.Id}\"[\\s\\S]*?Discarded after 2\\d{{3}}-03-04 12:00 UTC"), html);
        Assert.Matches(new Regex($"data-session-id=\"{active.Id}\"[\\s\\S]*?Discarded after 2\\d{{3}}-03-05 16:00 UTC"), html);
    }

    [Fact]
    public async Task Browse_UnfinishedUploadInAnotherFolder_NoWaitingRow()
    {
        var full = await _factory.CreateClientAs("customer-full");
        var own = await _factory.DemoTargetAsync("Full");
        await BeginUploadAsync(full, own.Id, "Documents", "big.bak", 10 * 1024 * 1024);

        var html = await (await full.GetAsync($"/files/{own.Id}")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("waiting-file", html);
        Assert.DoesNotContain("data-testid=\"upload-select-file\"", html);
    }

    [Fact]
    public async Task Browse_UnfinishedListFails_PageStillRenders()
    {
        using var failing = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IUploadSessionStore, ThrowingSessionStore>()));
        var client = failing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await client.GetAsync("/dev/signin?as=customer-full");
        var own = (await failing.Services.GetRequiredService<ITargetStore>().ListAsync()).Single(t => t.DisplayName == "Demo share (Full)");

        var response = await client.GetAsync($"/files/{own.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Welcome.txt", html);
        Assert.DoesNotContain("waiting-file", html);
    }

    [Fact]
    public async Task Browse_Contributor_ShowsUploadLimit()
    {
        var full = await _factory.CreateClientAs("customer-full");
        var own = await _factory.DemoTargetAsync("Full");

        var html = await (await full.GetAsync($"/files/{own.Id}")).Content.ReadAsStringAsync();

        Assert.Matches(new Regex("data-testid=\"upload-limit-text\"[^>]*>\\s*Files up to 100 GB\\s*<"), html);
    }

    [Fact]
    public async Task Browse_Reader_NoUploadLimit()
    {
        var reader = await _factory.CreateClientAs("customer-read");
        var own = await _factory.DemoTargetAsync("Read");

        var html = await (await reader.GetAsync($"/files/{own.Id}")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("upload-limit-text", html);
    }

    private sealed class ThrowingTargetStore : ITargetStore
    {
        public Task<StorageTarget?> GetAsync(Guid id, CancellationToken ct = default) => throw new InvalidOperationException("db down");
        public Task<IReadOnlyList<StorageTarget>> ListAsync(Guid? customerId = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");
        public Task UpsertAsync(StorageTarget t, CancellationToken ct = default) => throw new InvalidOperationException("db down");
    }
}
