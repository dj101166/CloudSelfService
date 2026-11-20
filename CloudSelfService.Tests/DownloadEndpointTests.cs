using System.Net;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using CloudSelfService.Services.Access;
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

    [Fact]
    public async Task Download_BackendThrowsUnexpectedOnOpen_503_AuditsFailed()
    {
        var (client, target) = await ReaderWithFileAsync("a.bin", Bytes(10));
        _factory.Storage.UnexpectedOnOpenRead = new InvalidOperationException("sdk blew up");

        var response = await client.GetAsync(Url(target, "a.bin"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("sdk blew up", await response.Content.ReadAsStringAsync());
        var row = Assert.Single(await DownloadAuditAsync());
        Assert.Equal((AuditResult.Failed, "a.bin"), (row.Result, row.Path));
    }

    [Fact]
    public async Task Download_BackendThrowsUnexpectedOnGetItem_503_AuditsFailed()
    {
        var (client, target) = await ReaderWithFileAsync("a.bin", Bytes(10));
        _factory.Storage.UnexpectedOnGetItem = new InvalidOperationException("sdk blew up");

        var response = await client.GetAsync(Url(target, "a.bin"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var row = Assert.Single(await DownloadAuditAsync());
        Assert.Equal((AuditResult.Failed, target.CustomerId), (row.Result, row.CustomerId));
    }

    [Fact]
    public async Task Download_AuditPath_IsNormalised()
    {
        var (client, target) = await ReaderWithFileAsync("Docs/a.bin", Bytes(10));

        await (await client.GetAsync(Url(target, "/Docs//a.bin"))).Content.ReadAsByteArrayAsync();

        Assert.Equal("Docs/a.bin", Assert.Single(await DownloadAuditAsync()).Path);
    }

    [Fact]
    public async Task Download_ShortRead_AbortsAndAuditsFailedWithBytesSoFar()
    {
        var (client, target) = await ReaderWithFileAsync("a.bin", Bytes(1000));
        _factory.Storage.WrapReadStream = s => new TruncatingStream(s, 400);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await (await client.GetAsync(Url(target, "a.bin"))).Content.ReadAsByteArrayAsync());

        var row = await SingleAuditEventuallyAsync();
        Assert.Equal((AuditResult.Failed, 400L), (row.Result, row.Bytes));
    }

    [Fact]
    public async Task Download_BackendFailsMidStream_AbortsAndAuditsFailed()
    {
        var (client, target) = await ReaderWithFileAsync("a.bin", Bytes(1000));
        _factory.Storage.WrapReadStream = s => new TruncatingStream(s, 300, throwAtEnd: true);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await (await client.GetAsync(Url(target, "a.bin"))).Content.ReadAsByteArrayAsync());

        var row = await SingleAuditEventuallyAsync();
        Assert.Equal((AuditResult.Failed, 300L), (row.Result, row.Bytes));
    }

    [Fact]
    public async Task Download_ClientDisconnects_AuditsFailedCancelled()
    {
        var (client, target) = await ReaderWithFileAsync("a.bin", Bytes(1000));
        var firstChunkSent = new TaskCompletionSource();
        _factory.Storage.WrapReadStream = s => new BlockingAfterFirstReadStream(s, 100, firstChunkSent);
        using var cts = new CancellationTokenSource();

        using var response = await client.GetAsync(Url(target, "a.bin"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await firstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        response.Dispose();

        var row = await SingleAuditEventuallyAsync();
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal(StorageAccessService.CancelledMessage, row.Error);
        Assert.Equal(100L, row.Bytes);
    }

    private async Task<AuditEvent> SingleAuditEventuallyAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            if ((await DownloadAuditAsync()).Count > 0)
            {
                await Task.Delay(50);
                return Assert.Single(await DownloadAuditAsync());
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("No download audit row was written.");
    }

    private sealed class TruncatingStream(Stream inner, int limit, bool throwAtEnd = false) : Stream
    {
        private int _served;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_served >= limit)
            {
                if (throwAtEnd) throw new IOException("backend connection reset");
                return 0;
            }
            var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, limit - _served)], ct);
            _served += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingAfterFirstReadStream(Stream inner, int firstChunk, TaskCompletionSource signal) : Stream
    {
        private bool _first = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_first)
            {
                _first = false;
                var n = await inner.ReadAsync(buffer[..firstChunk], ct);
                signal.TrySetResult();
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
