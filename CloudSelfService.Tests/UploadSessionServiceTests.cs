using System.Security.Cryptography;
using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Storage;
using CloudSelfService.Services.Transfers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

public class UploadSessionServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly AuditContext Ctx = new("10.0.0.9", "corr-up");
    private const int Mi = 1024 * 1024;

    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryCustomerStore _customers = new();
    private readonly InMemoryTargetStore _targets = new();
    private readonly InMemoryGrantStore _grants;
    private readonly InMemoryAuditStore _audit = new();
    private readonly InMemoryUploadSessionStore _sessions = new();
    private readonly FakeStorageBackend _fake = new();
    private readonly MutableTime _time = new(Start);

    private readonly Customer _custA = new(Guid.NewGuid(), "Alpha", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
    private readonly Customer _custR = new(Guid.NewGuid(), "Reader Co", true, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);
    private readonly StorageTarget _share;
    private readonly StorageTarget _blob;
    private readonly StorageTarget _readShare;
    private readonly AppUser _contrib;
    private readonly AppUser _colleague;
    private readonly AppUser _reader;

    public UploadSessionServiceTests()
    {
        _grants = new InMemoryGrantStore(_targets);
        _share = new(Guid.NewGuid(), _custA.Id, "Share", "sub", "rg", "acct", StorageKind.FileShare, "share", "core.windows.net", true);
        _blob = new(Guid.NewGuid(), _custA.Id, "Blob", "sub", "rg", "acct", StorageKind.BlobContainer, "cont", "core.windows.net", true);
        _readShare = new(Guid.NewGuid(), _custR.Id, "Read share", "sub", "rg", "acct", StorageKind.FileShare, "rshare", "core.windows.net", true);
        _contrib = User(_custA.Id, "c@alpha.example");
        _colleague = User(_custA.Id, "d@alpha.example");
        _reader = User(_custR.Id, "r@reader.example");

        _customers.UpsertAsync(_custA).Wait();
        _customers.UpsertAsync(_custR).Wait();
        foreach (var t in new[] { _share, _blob, _readShare }) _targets.UpsertAsync(t).Wait();
        foreach (var u in new[] { _contrib, _colleague, _reader }) _users.UpsertAsync(u).Wait();
        _grants.SetAsync(new Grant(_custA.Id, _share.Id, AccessLevel.Contribute, "admin", Start)).Wait();
        _grants.SetAsync(new Grant(_custA.Id, _blob.Id, AccessLevel.Contribute, "admin", Start)).Wait();
        _grants.SetAsync(new Grant(_custR.Id, _readShare.Id, AccessLevel.Read, "admin", Start)).Wait();
    }

    private static AppUser User(Guid customerId, string upn) =>
        new(Guid.NewGuid(), UserKind.Customer, customerId, upn, upn, "oid-" + upn, StaffRole.None, true, Start, null);

    internal sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private UploadSessionService Service(long? maxBytes = null, IUploadSessionStore? sessions = null)
    {
        var settings = new Dictionary<string, string?> { ["Transfers:ParallelChunks"] = "6" };
        if (maxBytes is not null) settings["Transfers:MaxUploadBytes"] = maxBytes.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var audit = new AuditService(_audit, NullLogger<AuditService>.Instance);
        var access = new StorageAccessService(_users, _customers, _targets, _grants, _fake, audit, new OwnerConfig(""),
            _time, NullLogger<StorageAccessService>.Instance);
        return new UploadSessionService(access, sessions ?? _sessions, _fake, audit, _time, config,
            NullLogger<UploadSessionService>.Instance);
    }

    private UploadCleanupService Cleanup() =>
        new(_sessions, _targets, _fake, _time, NullLogger<UploadCleanupService>.Instance);

    private static byte[] Bytes(int n, int seed = 7) =>
        Enumerable.Range(0, n).Select(i => (byte)((i * 31 + seed) % 251)).ToArray();

    private static string Md5(byte[] data) => Convert.ToBase64String(MD5.HashData(data));

    private static byte[] Chunk(byte[] file, int index)
    {
        var start = index * ChunkMath.ChunkSize;
        return file[start..Math.Min(file.Length, start + ChunkMath.ChunkSize)];
    }

    private Task<AccessResult> Put(UploadSessionService svc, Guid user, Guid session, byte[] file, int index)
    {
        var chunk = Chunk(file, index);
        return svc.PutChunkAsync(user, session, index, new MemoryStream(chunk), chunk.Length, Md5(chunk), Ctx);
    }

    private async Task<BeginUploadResponse> BeginOk(UploadSessionService svc, AppUser user, StorageTarget target, string folder,
        string name, long size, bool overwrite = false)
    {
        var result = await svc.BeginAsync(user.Id, new BeginUploadRequest(target.Id, folder, name, size, overwrite), Ctx);
        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        return result.Value!;
    }

    private async Task SeedFile(StorageTarget t, string path, byte[] data)
    {
        var staging = path + ".seed";
        await _fake.BeginUploadAsync(t, staging, data.Length, default);
        await _fake.WriteChunkAsync(t, staging, path, "s0", 0, new MemoryStream(data), data.Length, MD5.HashData(data), default);
        await _fake.CompleteUploadAsync(t, staging, path, new[] { "s0" }, true, default);
    }

    private async Task<byte[]> ReadFile(StorageTarget t, string path)
    {
        await using var s = await _fake.OpenReadAsync(t, path, 0, null, default);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    private async Task<IReadOnlyList<AuditEvent>> UploadAudits() => await _audit.QueryAsync(new AuditQuery(Operation: "Upload"));

    // ---- Begin ----

    [Fact]
    public async Task Begin_ReadUser_Denied()
    {
        var result = await Service().BeginAsync(_reader.Id, new BeginUploadRequest(_readShare.Id, "", "a.bin", 10, false), Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Null(result.Value);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("BeginUpload"));
        Assert.Empty(await _sessions.ListExpiredOpenAsync(DateTimeOffset.MaxValue));
        Assert.Equal(AuditResult.Denied, Assert.Single(await UploadAudits()).Result);
    }

    [Fact]
    public async Task Begin_TooLarge_Invalid()
    {
        var svc = Service(maxBytes: 1L << 30);

        var result = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "big.iso", (1L << 30) + 1, false), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal("File is larger than the 1 GB limit.", result.Message);
        Assert.Empty(_fake.Calls);
        Assert.Equal(AccessOutcome.Ok, (await svc.BeginAsync(_contrib.Id,
            new BeginUploadRequest(_share.Id, "", "big.iso", 1L << 30, false), Ctx)).Outcome);
    }

    [Fact]
    public async Task Begin_DefaultLimit_Is50Gb()
    {
        var result = await Service().BeginAsync(_contrib.Id,
            new BeginUploadRequest(_share.Id, "", "big.iso", 53_687_091_201, false), Ctx);

        Assert.Equal("File is larger than the 50 GB limit.", result.Message);
    }

    [Fact]
    public async Task Begin_NegativeSize_Invalid()
    {
        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", -1, false), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public async Task Begin_ExistingFileNoOverwrite_AlreadyExists()
    {
        await SeedFile(_share, "Docs/a.bin", Bytes(5));

        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "Docs", "a.bin", 10, false), Ctx);

        Assert.Equal(AccessOutcome.AlreadyExists, result.Outcome);
        Assert.Equal(StorageErrorMapper.AlreadyExistsMessage, result.Message);
        Assert.Equal(1, _fake.Calls.Count(c => c.StartsWith("BeginUpload")));
        Assert.Empty(await _sessions.ListExpiredOpenAsync(DateTimeOffset.MaxValue));
    }

    [Fact]
    public async Task Begin_ExistingFolderName_AlreadyExistsEvenWithOverwrite()
    {
        await _fake.CreateFolderAsync(_share, "Docs", default);

        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "Docs", 10, true), Ctx);

        Assert.Equal(AccessOutcome.AlreadyExists, result.Outcome);
    }

    [Fact]
    public async Task Begin_ExistingFileOverwrite_RequiresContribute_Ok()
    {
        await SeedFile(_share, "a.bin", Bytes(5));
        await SeedFile(_readShare, "a.bin", Bytes(5));
        var svc = Service();

        var contributor = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 10, true), Ctx);
        var reader = await svc.BeginAsync(_reader.Id, new BeginUploadRequest(_readShare.Id, "", "a.bin", 10, true), Ctx);

        Assert.Equal(AccessOutcome.Ok, contributor.Outcome);
        Assert.Equal(AccessOutcome.Denied, reader.Outcome);
        Assert.True((await _sessions.GetAsync(contributor.Value!.SessionId))!.Overwrite);
    }

    [Fact]
    public async Task Begin_FileShare_StagingIsHiddenSiblingName_ParallelFromConfig()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "Docs/Q1", "report.pdf", 9 * Mi + 1);

        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal("Docs/Q1/report.pdf", session.FinalPath);
        Assert.Equal($"Docs/Q1/.report.pdf.cssupload-{begin.SessionId:N}", session.StagingPath);
        Assert.True(StoragePaths.IsStagingName(StoragePath.Leaf(session.StagingPath)));
        Assert.Equal(ChunkMath.ChunkSize, begin.ChunkSize);
        Assert.Equal(3, begin.ChunkCount);
        Assert.Equal(6, begin.ParallelChunks);
        Assert.Empty(begin.Received);
        Assert.Equal(UploadStatus.Open, session.Status);
        Assert.Equal(Start.AddHours(24), session.ExpiresAt);
        Assert.Equal(_contrib.Id, session.UserId);
        Assert.Contains($"BeginUpload:{session.StagingPath}", _fake.Calls);
        Assert.Empty(await UploadAudits());
    }

    [Fact]
    public async Task Begin_Blob_StagingEqualsFinalPath()
    {
        var begin = await BeginOk(Service(), _contrib, _blob, "x", "y.bin", 3);

        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal("x/y.bin", session.StagingPath);
        Assert.Equal("x/y.bin", session.FinalPath);
    }

    [Theory]
    [InlineData("a‮exe.pdf")]
    [InlineData("a/b.pdf")]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".folder")]
    [InlineData(".x.bin.cssupload-0123456789abcdef0123456789abcdef")]
    public async Task Begin_BadOrReservedFileName_Invalid(string name)
    {
        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", name, 3, false), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public async Task Begin_NameTooLongForFilesStaging_Invalid()
    {
        var name = new string('n', 230) + ".bin";

        var files = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", name, 3, false), Ctx);
        var blob = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_blob.Id, "", name, 3, false), Ctx);

        Assert.Equal(AccessOutcome.Invalid, files.Outcome);
        Assert.Equal(AccessOutcome.Ok, blob.Outcome);
    }

    [Fact]
    public async Task Begin_IntoNewFolderWithInvisibleCharacter_Invalid()
    {
        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "New‮folder", "a.bin", 3, false), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(StoragePath.InvisibleCharactersMessage, result.Message);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("BeginUpload"));
    }

    [Fact]
    public async Task Begin_IntoExistingFolderWithInvisibleCharacter_Ok()
    {
        await _fake.CreateFolderAsync(_share, "Q1­reports", default);

        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "Q1­reports", "a.bin", 3, false), Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task Begin_SameFileAgain_ReturnsSameSessionWithReceived()
    {
        var svc = Service();
        var file = Bytes(9 * Mi + 1);
        var first = await BeginOk(svc, _contrib, _share, "Docs", "big.bin", file.Length);
        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, first.SessionId, file, 1)).Outcome);
        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, first.SessionId, file, 2)).Outcome);

        var second = await BeginOk(Service(), _contrib, _share, "Docs", "big.bin", file.Length);

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(new[] { 1, 2 }, second.Received.OrderBy(i => i));
        Assert.Equal(1, _fake.Calls.Count(c => c.StartsWith("BeginUpload")));
    }

    [Fact]
    public async Task Begin_SameFileOtherUserOrSize_NewSession()
    {
        var svc = Service();
        var first = await BeginOk(svc, _contrib, _share, "", "big.bin", 100);

        var otherUser = await BeginOk(svc, _colleague, _share, "", "big.bin", 100);
        var otherSize = await BeginOk(svc, _contrib, _share, "", "big.bin", 101);

        Assert.NotEqual(first.SessionId, otherUser.SessionId);
        Assert.NotEqual(first.SessionId, otherSize.SessionId);
    }

    [Fact]
    public async Task Begin_SameFileAfterExpiry_NewSession()
    {
        var svc = Service();
        var first = await BeginOk(svc, _contrib, _share, "", "big.bin", 100);
        _time.Now = Start.AddHours(24);

        var second = await BeginOk(svc, _contrib, _share, "", "big.bin", 100);

        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task Begin_ResumeWithOverwrite_UpdatesSessionOverwrite()
    {
        var svc = Service();
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", 3);
        await SeedFile(_share, "a.bin", Bytes(2));

        var conflict = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 3, false), Ctx);
        var resumed = await BeginOk(svc, _contrib, _share, "", "a.bin", 3, overwrite: true);

        Assert.Equal(AccessOutcome.AlreadyExists, conflict.Outcome);
        Assert.Equal(first.SessionId, resumed.SessionId);
        Assert.True((await _sessions.GetAsync(first.SessionId))!.Overwrite);
    }

    [Fact]
    public async Task Begin_BackendUnreachable_Unavailable()
    {
        _fake.ThrowUnreachable = true;

        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 3, false), Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(StorageErrorMapper.UnreachableMessage, result.Message);
    }

    [Fact]
    public async Task Begin_BeginUploadFails_UnavailableAndSessionAborted()
    {
        _fake.FailOnCall = call => call.StartsWith("BeginUpload")
            ? new StorageFailureException(StorageFailureKind.Unreachable, StorageErrorMapper.UnreachableMessage) : null;

        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 3, false), Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Null(await _sessions.FindResumableAsync(_contrib.Id, _share.Id, "a.bin", 3, Start));
        Assert.Empty(await _sessions.ListExpiredOpenAsync(DateTimeOffset.MaxValue));
        _fake.FailOnCall = null;
        Assert.Equal(AccessOutcome.Ok, (await Service().BeginAsync(_contrib.Id,
            new BeginUploadRequest(_share.Id, "", "a.bin", 3, false), Ctx)).Outcome);
    }

    [Fact]
    public async Task Begin_BeginUploadThrowsUnexpected_Unavailable()
    {
        _fake.FailOnCall = call => call.StartsWith("BeginUpload") ? new InvalidOperationException("sdk bug") : null;

        var result = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 3, false), Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(StorageErrorMapper.UnreachableMessage, result.Message);
    }

    [Fact]
    public async Task Begin_StoreFailure_Propagates()
    {
        var svc = Service(sessions: new ThrowingSessionStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 3, false), Ctx));
    }

    // ---- PutChunk ----

    [Fact]
    public async Task PutChunk_WrongLength_Invalid()
    {
        var svc = Service();
        var file = Bytes(5 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        var shortChunk = file[..Mi];

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(shortChunk), shortChunk.Length, Md5(shortChunk), Ctx);
        var noLength = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(shortChunk), null, Md5(shortChunk), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(AccessOutcome.Invalid, noLength.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
        Assert.Empty(await _sessions.ReceivedChunksAsync(begin.SessionId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task PutChunk_IndexOutOfRange_Invalid(int index)
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 5 * Mi);
        var data = Bytes(10);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, index, new MemoryStream(data), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task PutChunk_MissingMd5_Invalid(string? md5)
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(data), data.Length, md5, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
    }

    [Fact]
    public async Task PutChunk_BadMd5_UnavailableAndChunkNotMarked()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(data), data.Length,
            Convert.ToBase64String(new byte[16]), Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(StorageErrorMapper.CorruptedChunkMessage, result.Message);
        Assert.Empty(await _sessions.ReceivedChunksAsync(begin.SessionId));
    }

    [Fact]
    public async Task PutChunk_OtherUsersSession_NotFound()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await svc.PutChunkAsync(_colleague.Id, begin.SessionId, 0, new MemoryStream(data), data.Length, Md5(data), Ctx);
        var unknown = await svc.PutChunkAsync(_contrib.Id, Guid.NewGuid(), 0, new MemoryStream(data), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        Assert.Equal(AccessOutcome.NotFound, unknown.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
        Assert.Empty(await _audit.QueryAsync(new AuditQuery()));
    }

    [Fact]
    public async Task PutChunk_GrantRevokedAfterBegin_Denied()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await _grants.RemoveAsync(_custA.Id, _share.Id);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(data), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
        Assert.Equal(0, _fake.BytesWritten);
        Assert.Empty(await _sessions.ReceivedChunksAsync(begin.SessionId));
        Assert.Equal(AuditResult.Denied, Assert.Single(await UploadAudits()).Result);
    }

    [Fact]
    public async Task PutChunk_UserDeactivatedAfterBegin_Denied()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await _users.UpsertAsync(_contrib with { Active = false });

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(data), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
    }

    [Fact]
    public async Task PutChunk_ExpiredSession_NotFound()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        _time.Now = Start.AddHours(24);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(data), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.NotFound, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
    }

    [Fact]
    public async Task PutChunk_SameIndexTwice_Idempotent()
    {
        var svc = Service();
        var file = Bytes(5 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);

        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, begin.SessionId, file, 1)).Outcome);
        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, begin.SessionId, file, 1)).Outcome);
        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, begin.SessionId, file, 0)).Outcome);

        Assert.Equal(new[] { 0, 1 }, (await _sessions.ReceivedChunksAsync(begin.SessionId)).OrderBy(i => i));
        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.Equal(file, await ReadFile(_share, "a.bin"));
    }

    [Fact]
    public async Task PutChunk_WritesAtChunkOffsetWithSessionBlockId()
    {
        var svc = Service();
        var file = Bytes(5 * Mi);
        var begin = await BeginOk(svc, _contrib, _blob, "", "a.bin", file.Length);

        await Put(svc, _contrib.Id, begin.SessionId, file, 1);

        Assert.Contains($"WriteChunk:a.bin:{ChunkMath.BlockId(begin.SessionId, 1)}", _fake.Calls);
        Assert.Empty(await _audit.QueryAsync(new AuditQuery()));
    }

    // ---- Status ----

    [Fact]
    public async Task Status_OwnSession_ReturnsReceived_OtherUserNotFound()
    {
        var svc = Service();
        var file = Bytes(5 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 1);

        var own = await svc.StatusAsync(_contrib.Id, begin.SessionId);
        var other = await svc.StatusAsync(_colleague.Id, begin.SessionId);

        Assert.Equal(new[] { 1 }, own.Value);
        Assert.Equal(AccessOutcome.NotFound, other.Outcome);
    }

    // ---- Complete ----

    [Fact]
    public async Task Complete_MissingChunk_Invalid()
    {
        var svc = Service();
        var file = Bytes(9 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 0);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal("Upload is missing 2 pieces.", result.Message);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("CompleteUpload"));
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Empty(await UploadAudits());
    }

    [Theory]
    [InlineData(StorageKind.FileShare)]
    [InlineData(StorageKind.BlobContainer)]
    public async Task Complete_AllChunks_FileExistsWithExactBytes(StorageKind kind)
    {
        var target = kind == StorageKind.FileShare ? _share : _blob;
        var svc = Service();
        var file = Bytes(9 * Mi + 1);
        var begin = await BeginOk(svc, _contrib, target, "Docs", "big.bin", file.Length);
        Assert.Equal(3, begin.ChunkCount);

        foreach (var i in new[] { 2, 0, 1 })
            Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, begin.SessionId, file, i)).Outcome);
        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal(SHA256.HashData(file), SHA256.HashData(await ReadFile(target, "Docs/big.bin")));
        Assert.Equal(UploadStatus.Completed, (await _sessions.GetAsync(begin.SessionId))!.Status);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;
        if (kind == StorageKind.FileShare)
            Assert.DoesNotContain((await _fake.ListAsync(target, "Docs", null, 50, default)).Items, i => i.Path == staging);
    }

    [Theory]
    [InlineData(StorageKind.FileShare)]
    [InlineData(StorageKind.BlobContainer)]
    public async Task Complete_ZeroByteFile_CreatesEmptyFile(StorageKind kind)
    {
        var target = kind == StorageKind.FileShare ? _share : _blob;
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, target, "", "empty.txt", 0);
        Assert.Equal(0, begin.ChunkCount);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        var item = await _fake.GetItemAsync(target, "empty.txt", default);
        Assert.NotNull(item);
        Assert.Equal(0, item!.Size);
        Assert.Contains("CompleteUpload:empty.txt", _fake.Calls);
        var row = Assert.Single(await UploadAudits());
        Assert.Equal(0, row.Bytes);
    }

    [Fact]
    public async Task Complete_AuditsSingleUploadRow()
    {
        var svc = Service();
        var file = Bytes(5 * Mi + 3);
        var begin = await BeginOk(svc, _contrib, _share, "Docs", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 0);
        await Put(svc, _contrib.Id, begin.SessionId, file, 1);

        await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        var row = Assert.Single(await _audit.QueryAsync(new AuditQuery()));
        Assert.Equal("Upload", row.Operation);
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Equal(file.Length, row.Bytes);
        Assert.Equal("Docs/a.bin", row.Path);
        Assert.Equal(_contrib.Id, row.UserId);
        Assert.Equal(_contrib.Upn, row.UserDisplay);
        Assert.Equal(_custA.Id, row.CustomerId);
        Assert.Equal(_share.Id, row.TargetId);
        Assert.Equal(Ctx.SourceIp, row.SourceIp);
        Assert.Equal(Ctx.CorrelationId, row.CorrelationId);
    }

    [Fact]
    public async Task Complete_Twice_SecondNotFound()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);

        await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);
        var again = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.NotFound, again.Outcome);
        Assert.Single(await UploadAudits());
    }

    [Fact]
    public async Task Complete_GrantRevoked_DeniedAndNothingCommitted()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        await _grants.RemoveAsync(_custA.Id, _share.Id);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("CompleteUpload"));
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task Complete_FileAppearedMeanwhile_AlreadyExists_SessionStaysOpenAndAuditsFailed()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        await SeedFile(_share, "a.bin", Bytes(4));

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.AlreadyExists, result.Outcome);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Equal(AuditResult.Failed, Assert.Single(await UploadAudits()).Result);
        Assert.Equal(Bytes(4), await ReadFile(_share, "a.bin"));
    }

    [Fact]
    public async Task Complete_BackendFails_UnavailableReopensAndAuditsFailed()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        _fake.FailOnCall = call => call.StartsWith("CompleteUpload")
            ? new StorageFailureException(StorageFailureKind.Unreachable, StorageErrorMapper.UnreachableMessage) : null;

        var failed = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);
        _fake.FailOnCall = null;
        var retried = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, failed.Outcome);
        Assert.Equal(AccessOutcome.Ok, retried.Outcome);
        var rows = await UploadAudits();
        Assert.Equal(new[] { AuditResult.Failed, AuditResult.Success }, rows.OrderBy(r => r.Result == AuditResult.Success).Select(r => r.Result));
    }

    [Fact]
    public async Task Complete_BackendThrowsUnexpected_UnavailableNot500()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        _fake.FailOnCall = call => call.StartsWith("CompleteUpload") ? new InvalidOperationException("sdk bug") : null;

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task PutChunk_BackendThrowsUnexpected_UnavailableAndNotMarked()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        _fake.FailOnCall = call => call.StartsWith("WriteChunk") ? new InvalidOperationException("sdk bug") : null;

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(data), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Empty(await _sessions.ReceivedChunksAsync(begin.SessionId));
    }

    [Fact]
    public async Task Complete_OtherUser_NotFound()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);

        Assert.Equal(AccessOutcome.NotFound, (await svc.CompleteAsync(_colleague.Id, begin.SessionId, Ctx)).Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("CompleteUpload"));
    }

    // ---- Abort ----

    [Fact]
    public async Task Abort_RemovesStaging_AuditsFailed()
    {
        var svc = Service();
        var file = Bytes(5 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "Docs", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 0);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;

        var result = await svc.AbortAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Contains($"AbortUpload:{staging}", _fake.Calls);
        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
        var row = Assert.Single(await _audit.QueryAsync(new AuditQuery()));
        Assert.Equal("Upload", row.Operation);
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal("Cancelled by user", row.Error);
        Assert.Equal("Docs/a.bin", row.Path);
        Assert.Equal(AccessOutcome.NotFound, (await Put(svc, _contrib.Id, begin.SessionId, file, 1)).Outcome);
        Assert.Equal(AccessOutcome.NotFound, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
    }

    [Fact]
    public async Task Abort_BlobOverwriteSession_ExistingBlobUntouched()
    {
        var original = Bytes(7);
        await SeedFile(_blob, "a.bin", original);
        var svc = Service();
        var file = Bytes(10, seed: 3);
        var begin = await BeginOk(svc, _contrib, _blob, "", "a.bin", file.Length, overwrite: true);
        await Put(svc, _contrib.Id, begin.SessionId, file, 0);

        Assert.Equal(AccessOutcome.Ok, (await svc.AbortAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.Equal(original, await ReadFile(_blob, "a.bin"));
    }

    [Fact]
    public async Task Sweep_ExpiredBlobOverwriteSession_ExistingBlobUntouched()
    {
        var original = Bytes(7);
        await SeedFile(_blob, "a.bin", original);
        await BeginOk(Service(), _contrib, _blob, "", "a.bin", 10, overwrite: true);

        await Cleanup().SweepAsync(Start.AddHours(25), default);

        Assert.Equal(original, await ReadFile(_blob, "a.bin"));
    }

    [Fact]
    public async Task Abort_OtherUser_NotFound()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 3);

        Assert.Equal(AccessOutcome.NotFound, (await svc.AbortAsync(_colleague.Id, begin.SessionId, Ctx)).Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task Abort_BackendFails_SessionStaysOpen()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 3);
        _fake.ThrowUnreachable = true;

        var result = await svc.AbortAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Empty(await UploadAudits());
    }

    // ---- Cleanup ----

    [Fact]
    public async Task Sweep_ExpiredOpenSession_Aborted()
    {
        var svc = Service();
        var expired = await BeginOk(svc, _contrib, _share, "", "old.bin", 3);
        _time.Now = Start.AddHours(1);
        var fresh = await BeginOk(svc, _contrib, _share, "", "new.bin", 3);
        var expiredStaging = (await _sessions.GetAsync(expired.SessionId))!.StagingPath;
        var freshStaging = (await _sessions.GetAsync(fresh.SessionId))!.StagingPath;

        await Cleanup().SweepAsync(Start.AddHours(24), default);

        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(expired.SessionId))!.Status);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(fresh.SessionId))!.Status);
        Assert.Contains($"AbortUpload:{expiredStaging}", _fake.Calls);
        Assert.DoesNotContain($"AbortUpload:{freshStaging}", _fake.Calls);
    }

    [Fact]
    public async Task Sweep_SessionBeingCompleted_NotTouched()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing));

        await Cleanup().SweepAsync(Start.AddHours(25), default);

        Assert.Equal(UploadStatus.Completing, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task Sweep_BackendFails_StillMarksAborted()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        _fake.ThrowUnreachable = true;

        await Cleanup().SweepAsync(Start.AddHours(25), default);

        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task Sweep_DeletesRowsSevenDaysAfterExpiry()
    {
        var svc = Service();
        var file = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 0);
        await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);
        var expiry = Start.AddHours(24);

        await Cleanup().SweepAsync(expiry.AddDays(7).AddMinutes(-1), default);
        Assert.NotNull(await _sessions.GetAsync(begin.SessionId));

        await Cleanup().SweepAsync(expiry.AddDays(7).AddMinutes(1), default);
        Assert.Null(await _sessions.GetAsync(begin.SessionId));
        Assert.Empty(await _sessions.ReceivedChunksAsync(begin.SessionId));
    }

    private sealed class ThrowingSessionStore : IUploadSessionStore
    {
        private static Exception Down() => new InvalidOperationException("db down");
        public Task CreateAsync(UploadSession s, CancellationToken ct = default) => throw Down();
        public Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default) => throw Down();
        public Task<UploadSession?> FindResumableAsync(Guid userId, Guid targetId, string finalPath, long size, DateTimeOffset now,
            CancellationToken ct = default) => throw Down();
        public Task MarkChunkAsync(Guid id, int index, CancellationToken ct = default) => throw Down();
        public Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default) => throw Down();
        public Task SetStatusAsync(Guid id, string status, CancellationToken ct = default) => throw Down();
        public Task<bool> TrySetStatusAsync(Guid id, string expected, string next, CancellationToken ct = default) => throw Down();
        public Task SetOverwriteAsync(Guid id, bool overwrite, CancellationToken ct = default) => throw Down();
        public Task<IReadOnlyList<UploadSession>> ListExpiredOpenAsync(DateTimeOffset now, CancellationToken ct = default) => throw Down();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw Down();
        public Task<int> PurgeExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => throw Down();
    }
}
