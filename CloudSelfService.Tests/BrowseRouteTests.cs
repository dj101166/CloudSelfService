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

    private sealed class ThrowingTargetStore : ITargetStore
    {
        public Task<StorageTarget?> GetAsync(Guid id, CancellationToken ct = default) => throw new InvalidOperationException("db down");
        public Task<IReadOnlyList<StorageTarget>> ListAsync(Guid? customerId = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");
        public Task UpsertAsync(StorageTarget t, CancellationToken ct = default) => throw new InvalidOperationException("db down");
    }
}
