using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CloudSelfService.Endpoints;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Transfers;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSelfService.Tests;

public sealed class UploadEndpointTests : IDisposable
{
    private readonly TestAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)((i * 13 + 5) % 251)).ToArray();

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (token is not null) request.Headers.Add("RequestVerificationToken", token);
        return request;
    }

    private static HttpRequestMessage Begin(string? token, Guid targetId, string folder, string name, long size, bool overwrite = false) =>
        Request(HttpMethod.Post, "/api/uploads", token,
            JsonContent.Create(new { targetId, folder, fileName = name, size, overwrite }));

    private static HttpRequestMessage Chunk(string? token, Guid session, int index, byte[] data, string? md5 = null)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new("application/octet-stream");
        content.Headers.TryAddWithoutValidation("Content-MD5", md5 ?? Convert.ToBase64String(MD5.HashData(data)));
        return Request(HttpMethod.Put, $"/api/uploads/{session}/chunks/{index}", token, content);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonOf(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    private async Task<(HttpClient Client, string Token, StorageTarget Target)> ContributorAsync()
    {
        var client = await _factory.CreateClientAs("customer-contribute");
        return (client, await TokenAsync(client), await _factory.DemoTargetAsync("Contribute"));
    }

    private async Task<Guid> BeginOkAsync(HttpClient client, string token, StorageTarget target, string name, long size)
    {
        var response = await client.SendAsync(Begin(token, target.Id, "Uploads", name, size));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("sessionId").GetGuid();
    }

    private async Task<IReadOnlyList<AuditEvent>> UploadAuditAsync() =>
        await _factory.Services.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Operation: "Upload"));

    [Fact]
    public async Task Upload_EndToEnd_CustomerContribute_Succeeds()
    {
        var (client, token, target) = await ContributorAsync();
        var file = Bytes(ChunkMath.ChunkSize + 10);

        var begin = await client.SendAsync(Begin(token, target.Id, "Uploads", "big file #1.bin", file.Length));
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        Assert.Equal("application/json", begin.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", begin.Headers.CacheControl?.ToString());
        var body = await JsonOf(begin);
        var session = body.GetProperty("sessionId").GetGuid();
        Assert.Equal(ChunkMath.ChunkSize, body.GetProperty("chunkSize").GetInt32());
        Assert.Equal(2, body.GetProperty("chunkCount").GetInt32());
        Assert.Equal(4, body.GetProperty("parallelChunks").GetInt32());
        Assert.Equal(0, body.GetProperty("received").GetArrayLength());

        var second = await client.SendAsync(Chunk(token, session, 1, file[ChunkMath.ChunkSize..]));
        var first = await client.SendAsync(Chunk(token, session, 0, file[..ChunkMath.ChunkSize]));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var status = await client.SendAsync(Request(HttpMethod.Get, $"/api/uploads/{session}", token));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(new[] { 0, 1 }, (await JsonOf(status)).GetProperty("received").EnumerateArray().Select(e => e.GetInt32()).OrderBy(i => i));

        var complete = await client.SendAsync(Request(HttpMethod.Post, $"/api/uploads/{session}/complete", token));
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var download = await client.GetAsync($"/api/files/{target.Id}/download?path={Uri.EscapeDataString("Uploads/big file #1.bin")}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(SHA256.HashData(file), SHA256.HashData(await download.Content.ReadAsByteArrayAsync()));
        var row = Assert.Single(await UploadAuditAsync());
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Equal(file.Length, row.Bytes);
    }

    [Fact]
    public async Task Upload_ZeroByteFile_EndToEnd_Succeeds()
    {
        var (client, token, target) = await ContributorAsync();
        var session = await BeginOkAsync(client, token, target, "empty.txt", 0);

        var complete = await client.SendAsync(Request(HttpMethod.Post, $"/api/uploads/{session}/complete", token));

        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        var download = await client.GetAsync($"/api/files/{target.Id}/download?path=Uploads/empty.txt");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Empty(await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PutChunk_WithoutAntiforgery_400()
    {
        var (client, token, target) = await ContributorAsync();
        var data = Bytes(10);
        var session = await BeginOkAsync(client, token, target, "a.bin", data.Length);

        var missing = await client.SendAsync(Chunk(null, session, 0, data));
        var wrong = await client.SendAsync(Chunk("not-a-token", session, 0, data));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.DoesNotContain(_factory.Storage.Calls, c => c.StartsWith("WriteChunk"));
    }

    [Fact]
    public async Task UploadEndpoints_WithoutAntiforgery_400()
    {
        var (client, token, target) = await ContributorAsync();
        var session = await BeginOkAsync(client, token, target, "a.bin", 0);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Begin(null, target.Id, "", "b.bin", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Get, $"/api/uploads/{session}", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Post, $"/api/uploads/{session}/complete", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Delete, $"/api/uploads/{session}", null))).StatusCode);
        Assert.DoesNotContain(_factory.Storage.Calls, c => c.StartsWith("CompleteUpload") || c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task PutChunk_BodyLargerThanChunk_413()
    {
        var (client, token, target) = await ContributorAsync();
        var session = await BeginOkAsync(client, token, target, "a.bin", 3L * ChunkMath.ChunkSize);

        var response = await client.SendAsync(Chunk(token, session, 0, Bytes(ChunkMath.ChunkSize + 1)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain(_factory.Storage.Calls, c => c.StartsWith("WriteChunk"));
    }

    [Fact]
    public async Task Begin_Anonymous_NotAuthorised()
    {
        var (_, _, target) = await ContributorAsync();
        var anonymous = _factory.CreateAnonymousClient();

        var begin = await anonymous.SendAsync(Begin(null, target.Id, "", "a.bin", 1));
        var token = await anonymous.GetAsync("/api/antiforgery");

        Assert.True(begin.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized);
        Assert.True(token.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized);
        Assert.DoesNotContain(_factory.Storage.Calls, c => c.StartsWith("BeginUpload"));
        Assert.Empty(await _factory.Services.GetRequiredService<IUploadSessionStore>().ListExpiredOpenAsync(DateTimeOffset.MaxValue));
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task UploadEndpoints_CrossOrSameSiteFetch_403_NoAudit(string site)
    {
        var (client, token, target) = await ContributorAsync();
        var data = Bytes(10);
        var session = await BeginOkAsync(client, token, target, "a.bin", data.Length);

        var requests = new[]
        {
            Begin(token, target.Id, "", "b.bin", 1),
            Request(HttpMethod.Get, $"/api/uploads/{session}", token),
            Chunk(token, session, 0, data),
            Request(HttpMethod.Post, $"/api/uploads/{session}/complete", token),
            Request(HttpMethod.Delete, $"/api/uploads/{session}", token),
        };
        foreach (var request in requests)
        {
            request.Headers.Add("Sec-Fetch-Site", site);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        }
        Assert.DoesNotContain(_factory.Storage.Calls, c => c.StartsWith("WriteChunk") || c.StartsWith("CompleteUpload"));
        Assert.Empty(await UploadAuditAsync());
    }

    [Fact]
    public async Task Begin_ReadPersona_403()
    {
        var client = await _factory.CreateClientAs("customer-read");
        var target = await _factory.DemoTargetAsync("Read");

        var response = await client.SendAsync(Begin(await TokenAsync(client), target.Id, "", "a.bin", 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Begin_ExistingFile_409()
    {
        var (client, token, target) = await ContributorAsync();
        await _factory.SeedFileAsync(target, "Uploads/a.bin", Bytes(3));

        var response = await client.SendAsync(Begin(token, target.Id, "Uploads", "a.bin", 5));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Begin_MalformedBody_400()
    {
        var (client, token, _) = await ContributorAsync();

        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/uploads", token,
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OtherUsersSession_404()
    {
        var (client, token, target) = await ContributorAsync();
        var data = Bytes(10);
        var session = await BeginOkAsync(client, token, target, "a.bin", data.Length);
        var other = await _factory.CreateClientAs("customer-full");
        var otherToken = await TokenAsync(other);

        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(Request(HttpMethod.Get, $"/api/uploads/{session}", otherToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(Chunk(otherToken, session, 0, data))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(Request(HttpMethod.Delete, $"/api/uploads/{session}", otherToken))).StatusCode);
        Assert.DoesNotContain(_factory.Storage.Calls, c => c.StartsWith("WriteChunk") || c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task Abort_204_AuditsCancelled()
    {
        var (client, token, target) = await ContributorAsync();
        var session = await BeginOkAsync(client, token, target, "a.bin", 10);

        var response = await client.SendAsync(Request(HttpMethod.Delete, $"/api/uploads/{session}", token));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Cancelled by user", Assert.Single(await UploadAuditAsync()).Error);
    }

    [Fact]
    public async Task PutChunk_BadMd5_400_NoStorageCall()
    {
        var (client, token, target) = await ContributorAsync();
        var data = Bytes(10);
        var session = await BeginOkAsync(client, token, target, "a.bin", data.Length);

        var response = await client.SendAsync(Chunk(token, session, 0, data, Convert.ToBase64String(new byte[16])));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(UploadSessionService.ChunkCorruptedMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.DoesNotContain(_factory.Storage.Calls, c => c.StartsWith("WriteChunk"));
    }

    // The fake enforces the Azure SDK contract (seekable stream, exact length), so this fails if the raw body reaches storage.
    [Fact]
    public async Task PutChunk_FullSizePiece_ReachesStorageAsSeekableStream()
    {
        var (client, token, target) = await ContributorAsync();
        var file = Bytes(ChunkMath.ChunkSize);
        var session = await BeginOkAsync(client, token, target, "a.bin", file.Length);

        var response = await client.SendAsync(Chunk(token, session, 0, file));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ChunkMath.ChunkSize, _factory.Storage.BytesWritten);
    }

    [Fact]
    public async Task Complete_StagedDataLost_409()
    {
        var (client, token, target) = await ContributorAsync();
        var session = await BeginOkAsync(client, token, target, "a.bin", 0);
        var staging = (await _factory.Services.GetRequiredService<IUploadSessionStore>().GetAsync(session))!.StagingPath;
        await _factory.Storage.AbortUploadAsync(target, staging, default);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/uploads/{session}/complete", token));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(UploadSessionService.UploadLostMessage, (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Begin_StoreFailure_503_GenericMessage()
    {
        using var failing = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IUploadSessionStore, ThrowingSessionStore>()));
        var client = failing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await client.GetAsync("/dev/signin?as=customer-contribute");
        var target = (await failing.Services.GetRequiredService<ITargetStore>().ListAsync()).Single(t => t.DisplayName == "Demo share (Contribute)");

        var response = await client.SendAsync(Begin(await TokenAsync(client), target.Id, "", "a.bin", 1));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("The service is temporarily unavailable.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task PutChunks_DoNotSpendTransfersBucket()
    {
        using var factory = new TestAppFactory(("Transfers:RequestsPerMinute", "2"));
        var client = await factory.CreateClientAs("customer-contribute");
        var token = await TokenAsync(client);
        var target = await factory.DemoTargetAsync("Contribute");
        var file = Bytes(10);

        var begin = await client.SendAsync(Begin(token, target.Id, "", "a.bin", file.Length));
        var session = (await JsonOf(begin)).GetProperty("sessionId").GetGuid();
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Chunk(token, session, 0, file))).StatusCode);
        var complete = await client.SendAsync(Request(HttpMethod.Post, $"/api/uploads/{session}/complete", token));
        var third = await client.SendAsync(Begin(token, target.Id, "", "b.bin", 1));

        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    // ---- R29: machine-readable error codes ----

    [Fact]
    public async Task ErrorCodes_Begin()
    {
        var (client, token, target) = await ContributorAsync();
        await _factory.SeedFileAsync(target, "Uploads/a.bin", Bytes(3));
        var reader = await _factory.CreateClientAs("customer-read");
        var readTarget = await _factory.DemoTargetAsync("Read");

        await AssertError(await client.SendAsync(Begin(token, target.Id, "Uploads", "a.bin", 5)), HttpStatusCode.Conflict, UploadErrorCodes.Exists);
        await AssertError(await client.SendAsync(Begin(token, target.Id, "", "a\u202Eb.bin", 5)), HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
        await AssertError(await client.SendAsync(Begin(token, target.Id, "", "huge.bin", long.MaxValue)), HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
        await AssertError(await reader.SendAsync(Begin(await TokenAsync(reader), readTarget.Id, "", "a.bin", 1)), HttpStatusCode.Forbidden, UploadErrorCodes.Denied);
        await AssertError(await client.SendAsync(Begin(null, target.Id, "", "a.bin", 1)), HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
        var cross = Begin(token, target.Id, "", "a.bin", 1);
        cross.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertError(await client.SendAsync(cross), HttpStatusCode.Forbidden, UploadErrorCodes.Denied);
    }

    [Fact]
    public async Task ErrorCodes_Chunk()
    {
        var (client, token, target) = await ContributorAsync();
        var data = Bytes(10);
        var session = await BeginOkAsync(client, token, target, "a.bin", data.Length);

        await AssertError(await client.SendAsync(Chunk(token, session, 0, data, Convert.ToBase64String(new byte[16]))),
            HttpStatusCode.BadRequest, UploadErrorCodes.Retry);
        await AssertError(await client.SendAsync(Chunk(token, session, 0, data, "nope")), HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
        await AssertError(await client.SendAsync(Chunk(token, session, 0, Bytes(9))), HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
        await AssertError(await client.SendAsync(Chunk(token, session, 5, data)), HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
        await AssertError(await client.SendAsync(Chunk(token, Guid.NewGuid(), 0, data)), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        await AssertError(await client.SendAsync(Chunk(token, session, 0, Bytes(ChunkMath.ChunkSize + 1))),
            HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
        _factory.Storage.ThrowUnreachable = true;
        await AssertError(await client.SendAsync(Chunk(token, session, 0, data)), HttpStatusCode.ServiceUnavailable, UploadErrorCodes.Unavailable);
    }

    [Fact]
    public async Task ErrorCodes_CompleteAndStatus()
    {
        var (client, token, target) = await ContributorAsync();
        var session = await BeginOkAsync(client, token, target, "a.bin", 10);

        await AssertError(await client.SendAsync(Request(HttpMethod.Post, $"/api/uploads/{session}/complete", token)),
            HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
        await AssertError(await client.SendAsync(Request(HttpMethod.Get, $"/api/uploads/{Guid.NewGuid()}", token)),
            HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        await AssertError(await client.SendAsync(Request(HttpMethod.Delete, $"/api/uploads/{Guid.NewGuid()}", token)),
            HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
    }

    [Fact]
    public async Task ErrorCodes_CompleteLost_Restart()
    {
        var (client, token, target) = await ContributorAsync();
        var session = await BeginOkAsync(client, token, target, "a.bin", 0);
        var staging = (await _factory.Services.GetRequiredService<IUploadSessionStore>().GetAsync(session))!.StagingPath;
        await _factory.Storage.AbortUploadAsync(target, staging, default);

        await AssertError(await client.SendAsync(Request(HttpMethod.Post, $"/api/uploads/{session}/complete", token)),
            HttpStatusCode.Conflict, UploadErrorCodes.Restart);
    }

    [Fact]
    public async Task ErrorCodes_StoreFailure_Unavailable()
    {
        using var failing = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IUploadSessionStore, ThrowingSessionStore>()));
        var client = failing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await client.GetAsync("/dev/signin?as=customer-contribute");
        var target = (await failing.Services.GetRequiredService<ITargetStore>().ListAsync()).Single(t => t.DisplayName == "Demo share (Contribute)");

        await AssertError(await client.SendAsync(Begin(await TokenAsync(client), target.Id, "", "a.bin", 1)),
            HttpStatusCode.ServiceUnavailable, UploadErrorCodes.Unavailable);
    }

    [Fact]
    public async Task ErrorCodes_RateLimited_BusyJsonOnUploads_TextOnDownloads()
    {
        using var factory = new TestAppFactory(("Transfers:RequestsPerMinute", "1"));
        var client = await factory.CreateClientAs("customer-contribute");
        var token = await TokenAsync(client);
        var target = await factory.DemoTargetAsync("Contribute");
        await factory.SeedFileAsync(target, "d.bin", Bytes(3));

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Begin(token, target.Id, "", "a.bin", 1))).StatusCode);
        var busy = await client.SendAsync(Begin(token, target.Id, "", "b.bin", 1));
        var download = await client.GetAsync($"/api/files/{target.Id}/download?path=d.bin");

        await AssertError(busy, HttpStatusCode.TooManyRequests, UploadErrorCodes.Busy);
        Assert.Equal("5", busy.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.TooManyRequests, download.StatusCode);
        Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ErrorCodes_OtherUserUploadingBlob_InProgress()
    {
        var full = await _factory.CreateClientAs("customer-full");
        var targets = _factory.Services.GetRequiredService<ITargetStore>();
        var users = _factory.Services.GetRequiredService<IUserStore>();
        var grants = _factory.Services.GetRequiredService<IGrantStore>();
        var fullTarget = await _factory.DemoTargetAsync("Full");
        var blob = fullTarget with { Id = Guid.NewGuid(), DisplayName = "Shared blob", Kind = StorageKind.BlobContainer, ContainerName = "shared-blob" };
        await targets.UpsertAsync(blob);
        await grants.SetAsync(new Grant(blob.CustomerId!.Value, blob.Id, AccessLevel.Full, "test", DateTimeOffset.UtcNow));
        // A second user of the same customer.
        var fullUser = (await users.ListAsync(UserKind.Customer, blob.CustomerId)).Single();
        var colleague = fullUser with { Id = Guid.NewGuid(), Upn = "colleague@customer.example", EntraObjectId = null };
        await users.UpsertAsync(colleague);
        var sessions = _factory.Services.GetRequiredService<IUploadSessionStore>();
        var now = DateTimeOffset.UtcNow;
        await sessions.CreateAsync(new UploadSession(Guid.NewGuid(), colleague.Id, blob.Id, "a.bin", "a.bin", 1, ChunkMath.ChunkSize,
            false, now, now.AddHours(24), UploadStatus.Open));

        await AssertError(await full.SendAsync(Begin(await TokenAsync(full), blob.Id, "", "a.bin", 1)),
            HttpStatusCode.Conflict, UploadErrorCodes.InProgress);
    }

    private sealed class ThrowingSessionStore : IUploadSessionStore
    {
        private static Exception Down() => new InvalidOperationException("db down");
        public Task CreateAsync(UploadSession s, CancellationToken ct = default) => throw Down();
        public Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default) => throw Down();
        public Task<UploadSession?> FindResumableAsync(Guid userId, Guid targetId, string finalPath, long size, DateTimeOffset now,
            CancellationToken ct = default) => throw Down();
        public Task MarkChunkAsync(Guid id, int index, DateTimeOffset at, CancellationToken ct = default) => throw Down();
        public Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default) => throw Down();
        public Task SetStatusAsync(Guid id, string status, CancellationToken ct = default) => throw Down();
        public Task<bool> TrySetStatusAsync(Guid id, string expected, string next, DateTimeOffset? claimedAt = null,
            CancellationToken ct = default, bool requireClaim = false, DateTimeOffset? expectedClaim = null) => throw Down();
        public Task<IReadOnlyList<UploadSession>> ListActiveForPathAsync(Guid targetId, string finalPath, DateTimeOffset now,
            CancellationToken ct = default) => throw Down();
        public Task<IReadOnlyList<UploadSession>> ListStuckCompletingAsync(DateTimeOffset claimedBefore, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UploadSession>>(Array.Empty<UploadSession>());
        public Task SetOverwriteAsync(Guid id, bool overwrite, CancellationToken ct = default) => throw Down();
        public Task<IReadOnlyList<UploadSession>> ListExpiredOpenAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UploadSession>>(Array.Empty<UploadSession>());
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw Down();
        public Task<int> PurgeExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
