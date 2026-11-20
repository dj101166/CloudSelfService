using System.Net;
using CloudSelfService.Models;
using CloudSelfService.Endpoints;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Storage;
using CloudSelfService.Services.Transfers;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSelfService.Tests;

public sealed class DownloadHardeningTests
{
    private static string Url(StorageTarget t, string path) =>
        $"/api/files/{t.Id}/download?path={Uri.EscapeDataString(path)}";

    private static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    private static async Task<IReadOnlyList<AuditEvent>> AuditAsync(TestAppFactory f) =>
        await f.Services.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Operation: "Download"));

    private static async Task<AuditEvent> SingleAuditEventuallyAsync(TestAppFactory f)
    {
        for (var i = 0; i < 100; i++)
        {
            if ((await AuditAsync(f)).Count > 0)
            {
                await Task.Delay(50);
                return Assert.Single(await AuditAsync(f));
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("No download audit row was written.");
    }

    // ---- rate limiting ----

    [Fact]
    public async Task Download_FifthConcurrentForOneUser_429_OtherUserUnaffected()
    {
        using var factory = new TestAppFactory();
        var reader = await factory.CreateClientAs("customer-read");
        var other = await factory.CreateClientAs("customer-full");
        var readTarget = await factory.DemoTargetAsync("Read");
        var fullTarget = await factory.DemoTargetAsync("Full");
        await factory.SeedFileAsync(readTarget, "a.bin", Bytes(100));
        await factory.SeedFileAsync(fullTarget, "a.bin", Bytes(100));

        var gate = new TaskCompletionSource();
        var opened = 0;
        var allFourOpen = new TaskCompletionSource();
        factory.Storage.WrapReadStream = s =>
        {
            if (Interlocked.Increment(ref opened) == 4) allFourOpen.TrySetResult();
            return opened <= 4 ? new GatedStream(s, gate.Task) : s;
        };

        var running = Enumerable.Range(0, 4).Select(_ => reader.GetAsync(Url(readTarget, "a.bin"))).ToList();
        await allFourOpen.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var fifth = await reader.GetAsync(Url(readTarget, "a.bin"));
        var otherUser = await other.GetAsync(Url(fullTarget, "a.bin"));
        gate.SetResult();
        var finished = await Task.WhenAll(running);

        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.Equal(RateLimits.RejectedMessage, await fifth.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", fifth.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
        Assert.All(finished, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task Download_BeyondBurstLimit_429()
    {
        using var factory = new TestAppFactory(("Transfers:RequestsPerMinute", "3"));
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(10));

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(target, "a.bin"))).StatusCode);
        var fourth = await client.GetAsync(Url(target, "a.bin"));

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
    }

    // ---- Sec-Fetch-Site ----

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task Download_CrossOrSameSiteFetch_403_NoAudit(string site)
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(10));
        var request = new HttpRequestMessage(HttpMethod.Get, Url(target, "a.bin"));
        request.Headers.Add("Sec-Fetch-Site", site);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DownloadEndpoints.CrossSiteMessage, await response.Content.ReadAsStringAsync());
        Assert.Empty(await AuditAsync(factory));
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    [InlineData(null)]
    public async Task Download_SameOriginNoneOrAbsentFetch_200(string? site)
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(10));
        var request = new HttpRequestMessage(HttpMethod.Get, Url(target, "a.bin"));
        if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    // ---- re-authorisation during a long stream ----

    [Fact]
    public async Task Download_GrantRevokedMidStream_AbortsAndAuditsFailed()
    {
        using var factory = new TestAppFactory(("Transfers:ReauthorizeBytes", "1"));
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(1000));
        var gate = new TaskCompletionSource();
        var firstChunk = new TaskCompletionSource();
        factory.Storage.WrapReadStream = s => new GatedStream(s, gate.Task, chunk: 100, afterFirstChunk: firstChunk);

        var download = Task.Run(async () =>
            await (await client.GetAsync(Url(target, "a.bin"), HttpCompletionOption.ResponseHeadersRead)).Content.ReadAsByteArrayAsync());
        await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await factory.Services.GetRequiredService<IGrantStore>().RemoveAsync(target.CustomerId!.Value, target.Id);
        gate.SetResult();

        await Assert.ThrowsAnyAsync<Exception>(() => download);
        var row = await SingleAuditEventuallyAsync(factory);
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal(DownloadEndpoints.RevokedMessage, row.Error);
        Assert.InRange(row.Bytes!.Value, 100L, 200L);
    }

    // ---- ETag / If-Range ----

    [Fact]
    public async Task Download_Response_CarriesETagAndLastModified()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(10));

        var response = await client.GetAsync(Url(target, "a.bin"));

        Assert.NotNull(response.Headers.ETag);
        Assert.StartsWith("\"", response.Headers.ETag!.Tag);
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task Download_IfRangeMatches_206()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(1000));
        var etag = (await client.GetAsync(Url(target, "a.bin"))).Headers.ETag!.Tag;
        var request = new HttpRequestMessage(HttpMethod.Get, Url(target, "a.bin"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        request.Headers.TryAddWithoutValidation("If-Range", etag);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(10, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Download_IfRangeMismatch_FullFile200()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(1000));
        var request = new HttpRequestMessage(HttpMethod.Get, Url(target, "a.bin"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        request.Headers.TryAddWithoutValidation("If-Range", "\"stale\"");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1000, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Download_IfRangeWeakValidator_FullFile200()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(1000));
        var etag = (await client.GetAsync(Url(target, "a.bin"))).Headers.ETag!.Tag;
        var request = new HttpRequestMessage(HttpMethod.Get, Url(target, "a.bin"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        request.Headers.TryAddWithoutValidation("If-Range", "W/" + etag);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Fake_OpenRead_ChangedETag_ConflictFailure()
    {
        using var factory = new TestAppFactory();
        await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(10));
        var item = (await factory.Storage.GetItemAsync(target, "a.bin", default))!;
        await factory.SeedFileAsync(target, "a.bin", Bytes(20));

        var ex = await Assert.ThrowsAsync<StorageFailureException>(() =>
            factory.Storage.OpenReadAsync(target, "a.bin", 0, 10, default, item.ETag));

        Assert.Equal(StorageFailureKind.Conflict, ex.Kind);
    }

    // ---- audit identity ----

    [Fact]
    public async Task Download_AuditRow_CarriesStoreReadUserName()
    {
        using var factory = new TestAppFactory();
        var client = await factory.CreateClientAs("customer-read");
        var target = await factory.DemoTargetAsync("Read");
        await factory.SeedFileAsync(target, "a.bin", Bytes(10));

        await (await client.GetAsync(Url(target, "a.bin"))).Content.ReadAsByteArrayAsync();

        Assert.Equal("dev-customer-read@customer.example", Assert.Single(await AuditAsync(factory)).UserDisplay);
    }

    // ---- name hardening ----

    [Theory]
    [InlineData("invoice‮fdp.exe")]
    [InlineData("a‎b")]
    [InlineData("a‏b")]
    [InlineData("a‪b")]
    [InlineData("a⁦b")]
    [InlineData("a⁩b")]
    [InlineData("a﻿b")]
    [InlineData("a\u0080b")]
    [InlineData("a\u009Fb")]
    public void TryValidateSegment_InvisibleOrBidiCharacter_Rejected(string name)
    {
        Assert.False(StoragePath.TryValidateSegment(name, out var error));
        Assert.Equal(StoragePath.InvisibleCharactersMessage, error);
        Assert.False(StoragePath.TryValidateName(name, out _));
        Assert.False(StoragePath.TryNormalizePath("Docs/" + name, out _, out _));
    }

    [Fact]
    public void TryValidateSegment_OrdinaryUnicode_Accepted() =>
        Assert.True(StoragePath.TryValidateSegment("Q3 Ünï 日本語 \U0001F600.txt", out _));

    [Fact]
    public void Attachment_BidiOverride_ReplacedInBothFilenames()
    {
        var header = ContentDispositionHeader.Attachment("invoice‮fdp.exe");

        Assert.Contains("filename=\"invoice_fdp.exe\"", header);
        Assert.Contains("filename*=UTF-8''invoice_fdp.exe", header);
        Assert.DoesNotContain("%E2%80%AE", header);
    }

    [Theory]
    [InlineData("a‎b")]
    [InlineData("a﻿b")]
    [InlineData("a\u0085b")]
    public void Attachment_InvisibleCharacters_Replaced(string name) =>
        Assert.Contains("filename*=UTF-8''a_b", ContentDispositionHeader.Attachment(name));

    // U+017F (long s) upper-cases to 'S', so it can spell "cssupload" without matching a lower-case-only pattern.
    [Theory]
    [InlineData(".x.cſſupload-0123456789abcdef0123456789abcdef", true)]
    [InlineData(".x.cssupload-0123456789abcdef0123456789abcdef", true)]
    [InlineData(".x.CSSUPLOAD-0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("x.cſſupload-0123456789abcdef0123456789abcdef", false)]
    public void IsHiddenName_UsesUpperInvariantFolding(string name, bool hidden) =>
        Assert.Equal(hidden, StoragePaths.IsHiddenName(name));

    private sealed class GatedStream(Stream inner, Task gate, int chunk = 80 * 1024, TaskCompletionSource? afterFirstChunk = null) : Stream
    {
        private bool _first = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // With a first-chunk signal the first read is free and later reads wait; otherwise every read waits.
            if (afterFirstChunk is null || !_first) await gate.WaitAsync(ct);
            var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
            if (_first) afterFirstChunk?.TrySetResult();
            _first = false;
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
}
