using System.Net;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSelfService.Tests;

public sealed class DownloadEndpointTests : IDisposable
{
    private readonly TestAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static string Url(StorageTarget t, string path) =>
        $"/api/files/{t.Id}/download?path={Uri.EscapeDataString(path)}";

    private static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    private static HttpRequestMessage Ranged(string url, string range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Range", range);
        return request;
    }

    private async Task<(HttpClient Client, StorageTarget Target)> ReaderWithFileAsync(string path, byte[] data)
    {
        var client = await _factory.CreateClientAs("customer-read");
        var target = await _factory.DemoTargetAsync("Read");
        await _factory.SeedFileAsync(target, path, data);
        return (client, target);
    }

    private async Task<IReadOnlyList<AuditEvent>> DownloadAuditAsync() =>
        await _factory.Services.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Operation: "Download"));

    [Fact]
    public async Task Download_CustomerOwnFile_200_AttachmentOctetStream()
    {
        var data = Bytes(5000);
        var (client, target) = await ReaderWithFileAsync("Docs/report.bin", data);

        var response = await client.GetAsync(Url(target, "Docs/report.bin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(data, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("report.bin", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("bytes", response.Headers.GetValues("Accept-Ranges").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(5000, response.Content.Headers.ContentLength);
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    public async Task Download_Success_WritesOneSuccessAuditRowWithBytes()
    {
        var (client, target) = await ReaderWithFileAsync("a.bin", Bytes(300));

        await (await client.GetAsync(Url(target, "a.bin"))).Content.ReadAsByteArrayAsync();

        var row = Assert.Single(await DownloadAuditAsync());
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Equal(300, row.Bytes);
        Assert.Equal("a.bin", row.Path);
        Assert.Equal(target.Id, row.TargetId);
        Assert.Equal(target.CustomerId, row.CustomerId);
    }

    [Fact]
    public async Task Download_OtherCustomersTarget_403()
    {
        var client = await _factory.CreateClientAs("customer-read");
        await _factory.CreateClientAs("customer-full");
        var other = await _factory.DemoTargetAsync("Full");
        await _factory.SeedFileAsync(other, "secret.txt", Bytes(10));

        var response = await client.GetAsync(Url(other, "secret.txt"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var row = Assert.Single(await DownloadAuditAsync());
        Assert.Equal(AuditResult.Denied, row.Result);
    }

    [Fact]
    public async Task Download_Anonymous_RedirectsOrUnauthorized()
    {
        var (_, target) = await ReaderWithFileAsync("a.bin", Bytes(10));

        var response = await _factory.CreateAnonymousClient().GetAsync(Url(target, "a.bin"));

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(await DownloadAuditAsync());
    }

    [Fact]
    public async Task Download_Range_206_WithContentRange()
    {
        var data = Bytes(1000);
        var (client, target) = await ReaderWithFileAsync("big.bin", data);

        var response = await client.SendAsync(Ranged(Url(target, "big.bin"), "bytes=100-199"));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(data[100..200], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 100-199/1000", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(100, response.Content.Headers.ContentLength);
        var row = Assert.Single(await DownloadAuditAsync());
        Assert.Equal((AuditResult.Success, 100L), (row.Result, row.Bytes));
    }

    [Fact]
    public async Task Download_RangeBeyondEnd_416()
    {
        var (client, target) = await ReaderWithFileAsync("big.bin", Bytes(1000));

        var response = await client.SendAsync(Ranged(Url(target, "big.bin"), "bytes=5000-"));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */1000", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AuditResult.Failed, Assert.Single(await DownloadAuditAsync()).Result);
    }

    [Theory]
    [InlineData("bytes=5-3")]
    [InlineData("bytes=0-10,20-30")]
    [InlineData("garbage")]
    public async Task Download_MalformedOrMultiRange_IgnoredFullFile200(string range)
    {
        var data = Bytes(1000);
        var (client, target) = await ReaderWithFileAsync("big.bin", data);

        var response = await client.SendAsync(Ranged(Url(target, "big.bin"), range));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(data, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Download_EmptyFile_200()
    {
        var (client, target) = await ReaderWithFileAsync("empty.txt", Array.Empty<byte>());

        var response = await client.GetAsync(Url(target, "empty.txt"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Download_NameWithSpecialCharacters_HeaderEncoded()
    {
        const string name = "Q3 #1 50% a+b & c's Ünï.txt";
        var (client, target) = await ReaderWithFileAsync(name, Bytes(20));

        var response = await client.GetAsync(Url(target, name));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("filename*=UTF-8''", response.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(name, response.Content.Headers.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task Download_Folder_400_AuditedFailed()
    {
        var client = await _factory.CreateClientAs("customer-read");
        var target = await _factory.DemoTargetAsync("Read");

        var response = await client.GetAsync(Url(target, "Documents"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var row = Assert.Single(await DownloadAuditAsync());
        Assert.Equal(AuditResult.Failed, row.Result);
    }

    [Fact]
    public async Task Download_InvalidPath_400_NotAudited()
    {
        var client = await _factory.CreateClientAs("customer-read");
        var target = await _factory.DemoTargetAsync("Read");

        var response = await client.GetAsync(Url(target, "../etc/passwd"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await DownloadAuditAsync());
    }

    [Fact]
    public async Task Download_Missing_404_AuditedFailed()
    {
        var client = await _factory.CreateClientAs("customer-read");
        var target = await _factory.DemoTargetAsync("Read");

        var response = await client.GetAsync(Url(target, "nope.txt"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var row = Assert.Single(await DownloadAuditAsync());
        Assert.Equal((AuditResult.Failed, target.Id), (row.Result, row.TargetId));
    }

    [Fact]
    public async Task Download_BackendUnreachable_503_ReadableMessage()
    {
        var (client, target) = await ReaderWithFileAsync("a.bin", Bytes(10));
        _factory.Storage.ThrowUnreachable = true;

        var response = await client.GetAsync(Url(target, "a.bin"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("unreachable", await response.Content.ReadAsStringAsync());
        var row = Assert.Single(await DownloadAuditAsync());
        Assert.Equal(AuditResult.Failed, row.Result);
    }

    [Fact]
    public async Task Download_StoreFailure_503_GenericMessage()
    {
        using var failing = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ITargetStore, ThrowingTargetStore>()));
        var client = failing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await client.GetAsync("/dev/signin?as=staff-owner");

        var response = await client.GetAsync($"/api/files/{Guid.NewGuid()}/download?path=a.bin");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("The service is temporarily unavailable.", await response.Content.ReadAsStringAsync());
    }

    private sealed class ThrowingTargetStore : ITargetStore
    {
        public Task<StorageTarget?> GetAsync(Guid id, CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");
        public Task<IReadOnlyList<StorageTarget>> ListAsync(Guid? customerId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StorageTarget>>(Array.Empty<StorageTarget>());
        public Task UpsertAsync(StorageTarget t, CancellationToken ct = default) => Task.CompletedTask;
    }
}
