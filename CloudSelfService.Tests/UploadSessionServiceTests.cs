using System.Security.Cryptography;
using CloudSelfService.Tests.Infrastructure;
using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Storage;
using CloudSelfService.Services.Transfers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

    private UploadSessionService Service(long? maxBytes = null, IUploadSessionStore? sessions = null, int? maxOpen = null,
        ILogger<UploadSessionService>? logger = null)
    {
        var settings = new Dictionary<string, string?> { ["Transfers:ParallelChunks"] = "6" };
        if (maxBytes is not null) settings["Transfers:MaxUploadBytes"] = maxBytes.ToString();
        if (maxOpen is not null) settings["Transfers:MaxOpenSessionsPerUser"] = maxOpen.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var audit = new AuditService(_audit, NullLogger<AuditService>.Instance);
        var access = new StorageAccessService(_users, _customers, _targets, _grants, _fake, audit, new OwnerConfig(""),
            _time, NullLogger<StorageAccessService>.Instance);
        return new UploadSessionService(access, sessions ?? _sessions, _fake, audit, _time, config,
            logger ?? NullLogger<UploadSessionService>.Instance);
    }

    private UploadCleanupService Cleanup(int? completingTimeoutMinutes = null, int? idleMinutes = null) =>
        new(_sessions, _targets, _users, _fake, new AuditService(_audit, NullLogger<AuditService>.Instance), _time,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transfers:CompletingTimeoutMinutes"] = completingTimeoutMinutes?.ToString(),
                ["Transfers:IdleSessionMinutes"] = idleMinutes?.ToString(),
            }).Build(), NullLogger<UploadCleanupService>.Instance);

    // Every audit row except the UploadBegin rows Begin writes.
    private async Task<IReadOnlyList<AuditEvent>> NonBeginAudits() =>
        (await _audit.QueryAsync(new AuditQuery())).Where(e => e.Operation != UploadSessionService.BeginOperation).ToList();

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

    private Task SeedFile(StorageTarget t, string path, byte[] data)
    {
        _fake.SeedFile(t.Id, path, data);
        return Task.CompletedTask;
    }

    private async Task<byte[]> ReadFile(StorageTarget t, string path)
    {
        await using var s = await _fake.OpenReadAsync(t, path, 0, null, default);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    private async Task<IReadOnlyList<AuditEvent>> UploadAudits(string operation = "Upload") =>
        await _audit.QueryAsync(new AuditQuery(Operation: operation));

    // ---- Begin ----

    [Fact]
    public async Task Begin_ReadUser_Denied()
    {
        var result = await Service().BeginAsync(_reader.Id, new BeginUploadRequest(_readShare.Id, "", "a.bin", 10, false), Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Null(result.Value);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("BeginUpload"));
        Assert.Empty(await _sessions.ListSweepableOpenAsync(DateTimeOffset.MaxValue, DateTimeOffset.MaxValue));
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
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("BeginUpload"));
        Assert.Empty(await _sessions.ListSweepableOpenAsync(DateTimeOffset.MaxValue, DateTimeOffset.MaxValue));
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
        Assert.Empty(await _sessions.ListSweepableOpenAsync(DateTimeOffset.MaxValue, DateTimeOffset.MaxValue));
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

    // ---- First-piece hash (R39) ----

    private static readonly string HashA = Convert.ToBase64String(MD5.HashData(new byte[] { 1 }));
    private static readonly string HashB = Convert.ToBase64String(MD5.HashData(new byte[] { 2 }));

    private async Task<BeginUploadResponse> BeginWithHash(UploadSessionService svc, string? hash)
    {
        var result = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "same.bin", 2 * ChunkMath.ChunkSize, false, hash), Ctx);
        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        return result.Value!;
    }

    [Fact]
    public async Task Begin_SameFirstPieceHash_ResumesSession()
    {
        var svc = Service();
        var first = await BeginWithHash(svc, HashA);
        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, first.SessionId, new byte[2 * ChunkMath.ChunkSize], 0)).Outcome);

        var again = await BeginWithHash(svc, HashA);

        Assert.Equal(first.SessionId, again.SessionId);
        Assert.Equal(new[] { 0 }, again.Received);
    }

    [Fact]
    public async Task Begin_DifferentFirstPieceHash_StartsFreshAndReplacesOld()
    {
        var svc = Service();
        var first = await BeginWithHash(svc, HashA);
        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, first.SessionId, new byte[2 * ChunkMath.ChunkSize], 0)).Outcome);

        var other = await BeginWithHash(svc, HashB);

        Assert.NotEqual(first.SessionId, other.SessionId);
        Assert.Empty(other.Received);
        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(first.SessionId))!.Status);
        Assert.Equal(HashB, (await _sessions.GetAsync(other.SessionId))!.FirstPieceMd5);
        Assert.Contains(await UploadAudits(), a => a.Error == UploadSessionService.ReplacedMessage);
    }

    [Fact]
    public async Task Begin_NoHashOnEitherSide_ResumesOnNameAndSize()
    {
        var svc = Service();
        var first = await BeginWithHash(svc, null);
        var withHash = await BeginWithHash(svc, HashA);
        Assert.Equal(first.SessionId, withHash.SessionId);
    }

    [Fact]
    public async Task Begin_MalformedFirstPieceHash_Invalid()
    {
        var result = await Service().BeginAsync(_contrib.Id,
            new BeginUploadRequest(_share.Id, "", "same.bin", 10, false, "not-a-hash"), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(UploadSessionService.FirstPieceMd5InvalidMessage, result.Message);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("a", null, true)]
    [InlineData(null, "a", true)]
    [InlineData("a", "a", true)]
    [InlineData("a", "b", false)]
    public void SameFirstPiece_OnlyTwoKnownDifferentHashesRefuse(string? stored, string? declared, bool expected) =>
        Assert.Equal(expected, UploadSessionService.SameFirstPiece(stored, declared));

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

    // R28: the MD5 is checked locally before storage is called; a mismatch is a retryable Invalid (400).
    [Fact]
    public async Task PutChunk_BadMd5_InvalidAndChunkNotMarked()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(data), data.Length,
            Convert.ToBase64String(new byte[16]), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(UploadSessionService.ChunkCorruptedMessage, result.Message);
        Assert.Equal(UploadErrorCodes.Retry, result.Code);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
        Assert.Empty(await _sessions.ReceivedChunksAsync(begin.SessionId));
    }

    [Fact]
    public async Task PutChunk_StorageMd5Mismatch_UnavailableAndChunkNotMarked()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        _fake.FailOnCall = call => call.StartsWith("WriteChunk")
            ? new StorageFailureException(StorageFailureKind.Conflict, StorageErrorMapper.CorruptedChunkMessage) : null;

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new MemoryStream(data), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(StorageErrorMapper.CorruptedChunkMessage, result.Message);
        Assert.Empty(await _sessions.ReceivedChunksAsync(begin.SessionId));
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Empty(await NonBeginAudits());
    }

    [Fact]
    public async Task PutChunk_NonSeekableBody_BufferedAndWritten()
    {
        var svc = Service();
        var file = Bytes(9 * Mi + 1);
        var begin = await BeginOk(svc, _contrib, _blob, "", "a.bin", file.Length);

        foreach (var i in new[] { 1, 0, 2 })
        {
            var chunk = Chunk(file, i);
            var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, i, new NonSeekableStream(chunk), chunk.Length, Md5(chunk), Ctx);
            Assert.Equal(AccessOutcome.Ok, result.Outcome);
        }

        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.Equal(SHA256.HashData(file), SHA256.HashData(await ReadFile(_blob, "a.bin")));
    }

    [Fact]
    public async Task PutChunk_BodyShorterThanDeclared_Invalid()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new NonSeekableStream(data[..6]), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(UploadSessionService.ShortChunkMessage, result.Message);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
    }

    [Fact]
    public async Task PutChunk_BodyLongerThanDeclared_Invalid()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new NonSeekableStream(Bytes(11)), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(UploadSessionService.LongChunkMessage, result.Message);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
    }

    [Fact]
    public async Task PutChunk_BodyReadFails_InvalidNotUnavailable()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new NonSeekableStream(data, failAfter: 4), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(UploadSessionService.ChunkIncompleteMessage, result.Message);
        Assert.Equal(UploadErrorCodes.Retry, result.Code);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
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
        Assert.Empty(await NonBeginAudits());
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
        Assert.Empty(await NonBeginAudits());
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

        var row = Assert.Single(await NonBeginAudits());
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
    public async Task Begin_BlobPathAnotherUserIsUploading_Conflict()
    {
        var svc = Service();
        await BeginOk(svc, _contrib, _blob, "", "a.bin", 10);

        var other = await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_blob.Id, "", "a.bin", 10, false), Ctx);
        var otherSize = await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_blob.Id, "", "a.bin", 99, false), Ctx);

        Assert.Equal(AccessOutcome.Conflict, other.Outcome);
        Assert.Equal(UploadSessionService.OtherUserUploadingMessage, other.Message);
        Assert.Equal(UploadErrorCodes.InProgress, other.Code);
        Assert.Equal(AccessOutcome.Conflict, otherSize.Outcome);
        Assert.Equal(1, _fake.Calls.Count(c => c.StartsWith("BeginUpload")));
    }

    [Fact]
    public async Task Begin_BlobPathOtherUsersSessionStale_DoesNotBlock()
    {
        var svc = Service();
        await BeginOk(svc, _contrib, _blob, "", "a.bin", 10);
        _time.Now = Start.AddMinutes(16);

        var other = await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_blob.Id, "", "a.bin", 10, false), Ctx);

        Assert.Equal(AccessOutcome.Ok, other.Outcome);
    }

    [Fact]
    public async Task Begin_BlobPathOtherUsersSessionRecentChunk_StillBlocks()
    {
        var svc = Service();
        var file = Bytes(5 * Mi);
        var first = await BeginOk(svc, _contrib, _blob, "", "a.bin", file.Length);
        _time.Now = Start.AddMinutes(20);
        await Put(svc, _contrib.Id, first.SessionId, file, 0);
        _time.Now = Start.AddMinutes(30);

        var blocked = await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_blob.Id, "", "a.bin", file.Length, false), Ctx);
        _time.Now = Start.AddMinutes(36);
        var allowed = await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_blob.Id, "", "a.bin", file.Length, false), Ctx);

        Assert.Equal(AccessOutcome.Conflict, blocked.Outcome);
        Assert.Equal(AccessOutcome.Ok, allowed.Outcome);
        Assert.Equal(Start.AddMinutes(20), (await _sessions.GetAsync(first.SessionId))!.LastActivityAt);
    }

    [Fact]
    public async Task Begin_BlobLockMinutesFromConfig()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Transfers:BlobLockMinutes"] = "60" }).Build();
        var audit = new AuditService(_audit, NullLogger<AuditService>.Instance);
        var access = new StorageAccessService(_users, _customers, _targets, _grants, _fake, audit, new OwnerConfig(""), _time,
            NullLogger<StorageAccessService>.Instance);
        var svc = new UploadSessionService(access, _sessions, _fake, audit, _time, config, NullLogger<UploadSessionService>.Instance);
        await BeginOk(svc, _contrib, _blob, "", "a.bin", 10);
        _time.Now = Start.AddMinutes(30);

        Assert.Equal(AccessOutcome.Conflict, (await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_blob.Id, "", "a.bin", 10, false), Ctx)).Outcome);
    }

    [Fact]
    public async Task Complete_StaleSessionOverwrittenByOtherUser_Restart()
    {
        var svc = Service();
        var mine = Bytes(10, seed: 1);
        var theirs = Bytes(12, seed: 2);
        var first = await BeginOk(svc, _contrib, _blob, "", "a.bin", mine.Length);
        await Put(svc, _contrib.Id, first.SessionId, mine, 0);
        _time.Now = Start.AddMinutes(16);
        var second = await BeginOk(svc, _colleague, _blob, "", "a.bin", theirs.Length);
        await Put(svc, _colleague.Id, second.SessionId, theirs, 0);
        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_colleague.Id, second.SessionId, Ctx)).Outcome);

        var result = await svc.CompleteAsync(_contrib.Id, first.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadErrorCodes.Restart, result.Code);
        Assert.Equal(theirs, await ReadFile(_blob, "a.bin"));
    }

    [Fact]
    public async Task Begin_BlobPathOtherUsersSessionExpired_Ok()
    {
        var svc = Service();
        await BeginOk(svc, _contrib, _blob, "", "a.bin", 10);
        _time.Now = Start.AddHours(24);

        Assert.Equal(AccessOutcome.Ok, (await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_blob.Id, "", "a.bin", 10, false), Ctx)).Outcome);
    }

    [Fact]
    public async Task Begin_FileSharePathAnotherUserIsUploading_OwnSessionOk()
    {
        var svc = Service();
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", 10);

        var second = await BeginOk(svc, _colleague, _share, "", "a.bin", 10);

        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    // R32 d: the same file at a different size replaces the user's older session for it.
    [Theory]
    [InlineData(StorageKind.FileShare)]
    [InlineData(StorageKind.BlobContainer)]
    public async Task Begin_SameFileDifferentSize_ReplacesOlderSession(StorageKind kind)
    {
        var target = kind == StorageKind.FileShare ? _share : _blob;
        var svc = Service();
        var a = Bytes(10, seed: 1);
        var b = Bytes(12, seed: 2);
        var first = await BeginOk(svc, _contrib, target, "", "a.bin", a.Length);
        await Put(svc, _contrib.Id, first.SessionId, a, 0);
        var firstStaging = (await _sessions.GetAsync(first.SessionId))!.StagingPath;

        var second = await BeginOk(svc, _contrib, target, "", "a.bin", b.Length);

        var old = (await _sessions.GetAsync(first.SessionId))!;
        Assert.Equal(UploadStatus.Aborted, old.Status);
        Assert.True(old.StagingRemoved);
        if (kind == StorageKind.FileShare)
        {
            Assert.Contains($"AbortUpload:{firstStaging}", _fake.Calls);
            Assert.Null(_fake.StagingLength(_share.Id, firstStaging));
        }
        var row = Assert.Single(await UploadAudits());
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal(UploadSessionService.ReplacedMessage, row.Error);
        Assert.Equal(AccessOutcome.NotFound, (await Put(svc, _contrib.Id, first.SessionId, a, 0)).Outcome);
        await Put(svc, _contrib.Id, second.SessionId, b, 0);
        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, second.SessionId, Ctx)).Outcome);
        Assert.Equal(b, await ReadFile(target, "a.bin"));
    }

    [Fact]
    public async Task Begin_SameFileOtherUserDifferentSize_DoesNotReplace()
    {
        var svc = Service();
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", 10);

        await BeginOk(svc, _colleague, _share, "", "a.bin", 12);

        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(first.SessionId))!.Status);
    }

    [Fact]
    public async Task Complete_InvalidBlockList_ConflictWithRestartMessage()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _blob, "", "a.bin", 0);
        _fake.FailOnCall = call => call.StartsWith("CompleteUpload")
            ? StorageErrorMapper.Map(new Azure.RequestFailedException(400, "bad list", "InvalidBlockList", null)) : null;

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(StorageErrorMapper.UploadInterruptedMessage, result.Message);
        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Aborted, session.Status);
        // Uncommitted blob blocks need no cleanup, so nothing is deleted and nothing is left for the sweep.
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
        Assert.True(session.StagingRemoved);
    }

    [Fact]
    public async Task Complete_FilesStagingRemovedExternally_ConflictAndAborted()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        await _fake.AbortUploadAsync(_share, (await _sessions.GetAsync(begin.SessionId))!.StagingPath, default);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadSessionService.UploadLostMessage, result.Message);
        Assert.Equal(UploadErrorCodes.Restart, result.Code);
        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
        var row = Assert.Single(await UploadAudits());
        Assert.Equal(AuditResult.Failed, row.Result);
    }

    // R30: an earlier commit that landed but whose reply was lost is recognised, not written off.
    [Theory]
    [InlineData(StorageKind.FileShare, 10)]
    [InlineData(StorageKind.BlobContainer, 10)]
    [InlineData(StorageKind.BlobContainer, 0)]
    public async Task Complete_CommitAlreadyLanded_TreatedAsSuccess(StorageKind kind, int size)
    {
        var target = kind == StorageKind.FileShare ? _share : _blob;
        var svc = Service();
        var data = Bytes(size);
        var begin = await BeginOk(svc, _contrib, target, "", "a.bin", size);
        if (size > 0) await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        var session = (await _sessions.GetAsync(begin.SessionId))!;
        var blocks = size > 0 ? new[] { ChunkMath.BlockId(session.Id, 0) } : Array.Empty<string>();
        await _fake.CompleteUploadAsync(target, session.StagingPath, session.FinalPath, blocks, false, default, session.Id);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal(UploadStatus.Completed, (await _sessions.GetAsync(begin.SessionId))!.Status);
        var row = Assert.Single(await UploadAudits());
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Equal(data, await ReadFile(target, "a.bin"));
    }

    [Fact]
    public async Task Complete_ZeroByteBlobFromOtherSession_NotTreatedAsLanded()
    {
        var svc = Service();
        var first = await BeginOk(svc, _contrib, _blob, "", "a.bin", 0);
        await _fake.CompleteUploadAsync(_blob, "a.bin", "a.bin", Array.Empty<string>(), true, default, Guid.NewGuid());

        var result = await svc.CompleteAsync(_contrib.Id, first.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadErrorCodes.Restart, result.Code);
    }

    [Fact]
    public async Task Complete_BlobCommitStampsSessionMetadata()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _blob, "", "a.bin", 0);

        await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.True(await _fake.HasLandedAsync(_blob, "a.bin", "a.bin", begin.SessionId, 0, default));
        Assert.False(await _fake.HasLandedAsync(_blob, "a.bin", "a.bin", Guid.NewGuid(), 0, default));
    }

    [Fact]
    public async Task Complete_LandedCheckUnreachable_UnavailableAndStaysOpen()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        _fake.FailOnCall = call => call.StartsWith("CompleteUpload")
            ? new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage)
            : call.StartsWith("HasLanded")
                ? new StorageFailureException(StorageFailureKind.Unreachable, StorageErrorMapper.UnreachableMessage) : null;

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task Complete_LostButSweepAlreadySettled_NoSecondFailedRow()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        _fake.FailOnCall = call =>
        {
            if (!call.StartsWith("CompleteUpload")) return null;
            // The sweep settles the session while the commit is failing.
            _sessions.SetStatusAsync(begin.SessionId, UploadStatus.Aborted).GetAwaiter().GetResult();
            return new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);
        };

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Empty(await UploadAudits());
    }

    [Fact]
    public async Task Complete_SucceededButSweepAlreadySettled_NoSecondSuccessRow()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        _fake.FailOnCall = call =>
        {
            if (call.StartsWith("CompleteUpload"))
                _sessions.SetStatusAsync(begin.SessionId, UploadStatus.Completed).GetAwaiter().GetResult();
            return null;
        };

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Empty(await UploadAudits());
    }

    [Fact]
    public async Task Complete_RecordsClaimTime()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        _time.Now = Start.AddMinutes(5);

        await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(Start.AddMinutes(5), (await _sessions.GetAsync(begin.SessionId))!.ClaimedAt);
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
        var row = Assert.Single(await NonBeginAudits());
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

    // ---- Round 3: R32 quota, caps, idle, replacement ----

    [Fact]
    public async Task Begin_FileShare_StagingCreatedEmpty_NotAtDeclaredSize()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "huge.iso", 40L * 1024 * 1024 * 1024);

        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;
        Assert.Equal(0, _fake.StagingLength(_share.Id, staging));
        Assert.Equal(0, _fake.TotalStagingBytes);
    }

    // R34: one grow per session, to the full size, whichever piece arrives first. Order 1,2,0 is what tells this apart
    // from the old end-of-piece growth (which would stop at 8 MiB on piece 1); 2,0,1 alone would not.
    [Theory]
    [InlineData(new[] { 2, 0, 1 })]
    [InlineData(new[] { 1, 2, 0 })]
    public async Task PutChunk_FirstChunkGrowsStagingToFullSize_OnceOnly(int[] order)
    {
        var svc = Service();
        var file = Bytes(9 * Mi + 1);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;

        await Put(svc, _contrib.Id, begin.SessionId, file, order[0]);
        Assert.Equal(file.Length, _fake.StagingLength(_share.Id, staging));
        foreach (var i in order.Skip(1))
            await Put(svc, _contrib.Id, begin.SessionId, file, i);

        var grows = _fake.Calls.Where(c => c.StartsWith("GrowStaging")).ToList();
        Assert.Equal(new[] { $"GrowStaging:{staging}:{file.Length}" }, grows);
        Assert.Equal(file.Length, (await _sessions.GetAsync(begin.SessionId))!.StagingSize);
        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.Equal(file, await ReadFile(_share, "a.bin"));
    }

    // R34: a timed-out grow that storage applies late is the same full-size grow, so written pieces survive.
    [Fact]
    public async Task PutChunk_LateDuplicateGrowToSameSize_KeepsWrittenBytes()
    {
        var svc = Service();
        var file = Bytes(9 * Mi + 1);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        foreach (var i in new[] { 1, 2, 0 })
            await Put(svc, _contrib.Id, begin.SessionId, file, i);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;

        await _fake.GrowStagingAsync(_share, staging, file.Length, default);

        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.Equal(SHA256.HashData(file), SHA256.HashData(await ReadFile(_share, "a.bin")));
    }

    // The fake models Azure's truncation, so this pins why the grow target must never be smaller than the session.
    [Fact]
    public async Task Fake_ShrinkingStaging_DiscardsPiecesPastTheEnd()
    {
        var svc = Service();
        var file = Bytes(9 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 0);
        await Put(svc, _contrib.Id, begin.SessionId, file, 2);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;

        await _fake.GrowStagingAsync(_share, staging, 8L * Mi, default);
        await _fake.GrowStagingAsync(_share, staging, file.Length, default);
        await Put(svc, _contrib.Id, begin.SessionId, file, 1);

        // Piece 2's block was discarded by the shrink, so the commit can't assemble the file and nothing is published.
        Assert.NotEqual(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.Null(await _fake.GetItemAsync(_share, "a.bin", default));
    }

    [Fact]
    public async Task PutChunk_ZeroByteUpload_NeverGrows()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "empty.txt", 0);

        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("GrowStaging"));
    }

    [Fact]
    public async Task PutChunk_FileShare_ParallelPiecesNeverShrinkStaging()
    {
        var svc = Service();
        var file = Bytes(16 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);

        await Task.WhenAll(new[] { 3, 0, 2, 1 }.Select(i => Task.Run(() => Put(svc, _contrib.Id, begin.SessionId, file, i))));

        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;
        Assert.Equal(file.Length, _fake.StagingLength(_share.Id, staging));
        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.Equal(SHA256.HashData(file), SHA256.HashData(await ReadFile(_share, "a.bin")));
    }

    [Fact]
    public async Task PutChunk_Blob_NeverGrowsStaging()
    {
        var svc = Service();
        var file = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _blob, "", "a.bin", file.Length);

        await Put(svc, _contrib.Id, begin.SessionId, file, 0);

        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("GrowStaging"));
    }

    [Fact]
    public async Task Begin_TooManyOpenSessions_Busy()
    {
        var svc = Service(maxOpen: 2);
        await BeginOk(svc, _contrib, _share, "", "a.bin", 1);
        await BeginOk(svc, _contrib, _share, "", "b.bin", 1);

        var third = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "c.bin", 1, false), Ctx);
        var colleague = await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_share.Id, "", "c.bin", 1, false), Ctx);
        var resume = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 1, false), Ctx);

        Assert.Equal(AccessOutcome.Busy, third.Outcome);
        Assert.Equal(UploadSessionService.TooManyUploadsMessage, third.Message);
        Assert.Equal(UploadErrorCodes.Busy, third.Code);
        Assert.Equal(AccessOutcome.Ok, colleague.Outcome);
        Assert.Equal(AccessOutcome.Ok, resume.Outcome);
        Assert.Equal(3, _fake.Calls.Count(c => c.StartsWith("BeginUpload")));
    }

    [Fact]
    public async Task Begin_ReservedBytesOverLimit_Busy_BeforeAnyStorageCall()
    {
        var svc = Service(maxBytes: 100);
        await BeginOk(svc, _contrib, _share, "", "a.bin", 60);
        var calls = _fake.Calls.Count;

        var over = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "b.bin", 41, false), Ctx);

        Assert.Equal(AccessOutcome.Busy, over.Outcome);
        Assert.Equal(calls, _fake.Calls.Count);
        Assert.Equal(AccessOutcome.Ok, (await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "c.bin", 40, false), Ctx)).Outcome);
    }

    [Fact]
    public async Task Begin_SettledSessionsFreeTheirReservation()
    {
        var svc = Service(maxBytes: 100, maxOpen: 1);
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        await svc.CompleteAsync(_contrib.Id, first.SessionId, Ctx);
        var second = await BeginOk(svc, _contrib, _share, "", "b.bin", 100);
        await svc.AbortAsync(_contrib.Id, second.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Ok, (await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "c.bin", 100, false), Ctx)).Outcome);
    }

    [Fact]
    public async Task Begin_ReplacingOlderSessionOfSameFile_DoesNotCountAgainstCaps()
    {
        var svc = Service(maxBytes: 100, maxOpen: 1);
        await BeginOk(svc, _contrib, _share, "", "a.bin", 90);

        var replaced = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 95, false), Ctx);

        Assert.Equal(AccessOutcome.Ok, replaced.Outcome);
    }

    [Fact]
    public async Task Sweep_SessionThatNeverReceivedAChunk_AbortedWhenIdle()
    {
        var svc = Service();
        var file = Bytes(5 * Mi);
        var idle = await BeginOk(svc, _contrib, _share, "", "idle.bin", file.Length);
        var active = await BeginOk(svc, _contrib, _share, "", "active.bin", file.Length);
        await Put(svc, _contrib.Id, active.SessionId, file, 0);

        await Cleanup().SweepAsync(Start.AddMinutes(121), default);

        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(idle.SessionId))!.Status);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(active.SessionId))!.Status);
        var row = Assert.Single(await UploadAudits());
        Assert.Equal(UploadCleanupService.ExpiredMessage, row.Error);
        Assert.Equal("idle.bin", row.Path);
    }

    // R33: once a chunk has landed the session keeps its full 24 h, so an upload paused overnight resumes.
    [Fact]
    public async Task Sweep_SessionWithAChunk_KeepsFullLifetimeDespiteIdle()
    {
        var svc = Service();
        var file = Bytes(9 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 0);

        await Cleanup().SweepAsync(Start.AddHours(23), default);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);

        _time.Now = Start.AddHours(23);
        var resumed = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        Assert.Equal(begin.SessionId, resumed.SessionId);
        Assert.Equal(new[] { 0 }, resumed.Received);

        await Cleanup().SweepAsync(Start.AddHours(24), default);
        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task Sweep_IdleSessionMinutesFromConfig()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);

        await Cleanup(idleMinutes: 300).SweepAsync(Start.AddMinutes(200), default);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);

        await Cleanup(idleMinutes: 300).SweepAsync(Start.AddMinutes(301), default);
        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    // ---- Round 3: M1 staging cleanup is retried until confirmed ----

    [Fact]
    public async Task Sweep_AbortFailed_RetriedNextSweep_ThenRowPurgeable()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;
        _fake.FailOnCall = c => c.StartsWith("AbortUpload") ? new StorageFailureException(StorageFailureKind.Unreachable, "x") : null;

        await Cleanup().SweepAsync(Start.AddHours(25), default);
        var afterFirst = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Aborted, afterFirst.Status);
        Assert.False(afterFirst.StagingRemoved);

        // Past the 7-day retention but staging still unconfirmed: the row is kept.
        await Cleanup().SweepAsync(Start.AddDays(9), default);
        Assert.NotNull(await _sessions.GetAsync(begin.SessionId));

        _fake.FailOnCall = null;
        await Cleanup().SweepAsync(Start.AddDays(9).AddMinutes(30), default);
        Assert.Null(_fake.StagingLength(_share.Id, staging));
        Assert.Null(await _sessions.GetAsync(begin.SessionId));
    }

    [Fact]
    public async Task Sweep_StagingNeverRemovable_RowDroppedAfter30Days()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        _fake.FailOnCall = c => c.StartsWith("AbortUpload") ? new StorageFailureException(StorageFailureKind.Unreachable, "x") : null;
        await Cleanup().SweepAsync(Start.AddHours(25), default);

        await Cleanup().SweepAsync(Start.AddDays(30), default);
        Assert.NotNull(await _sessions.GetAsync(begin.SessionId));

        await Cleanup().SweepAsync(Start.AddDays(31).AddHours(1), default);
        Assert.Null(await _sessions.GetAsync(begin.SessionId));
    }

    [Fact]
    public async Task Begin_BeginUploadFails_StagingRemovedAndConfirmed()
    {
        var svc = Service();
        _fake.FailOnCall = call => call.StartsWith("BeginUpload")
            ? new StorageFailureException(StorageFailureKind.Unreachable, StorageErrorMapper.UnreachableMessage) : null;

        var result = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 3, false), Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Contains(_fake.Calls, c => c.StartsWith("AbortUpload"));
        Assert.Empty(await _sessions.ListAbortedWithStagingAsync());
        Assert.Empty(await _audit.QueryAsync(new AuditQuery(Operation: UploadSessionService.BeginOperation)));
    }

    [Fact]
    public async Task Begin_BeginUploadFails_AbortAlsoFails_SweepRetries()
    {
        _fake.FailOnCall = call => call.StartsWith("BeginUpload") || call.StartsWith("AbortUpload")
            ? new StorageFailureException(StorageFailureKind.Unreachable, StorageErrorMapper.UnreachableMessage) : null;

        await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 3, false), Ctx);
        var pending = Assert.Single(await _sessions.ListAbortedWithStagingAsync());

        _fake.FailOnCall = null;
        await Cleanup().SweepAsync(Start.AddMinutes(30), default);

        Assert.True((await _sessions.GetAsync(pending.Id))!.StagingRemoved);
        Assert.Empty(await _sessions.ListAbortedWithStagingAsync());
    }

    [Fact]
    public async Task Complete_Lost_AbortFails_SweepRetries()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        _fake.FailOnCall = call =>
            call.StartsWith("CompleteUpload") ? new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage)
            : call.StartsWith("AbortUpload") ? new StorageFailureException(StorageFailureKind.Unreachable, "x") : null;

        Assert.Equal(AccessOutcome.Conflict, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        Assert.False((await _sessions.GetAsync(begin.SessionId))!.StagingRemoved);

        _fake.FailOnCall = null;
        await Cleanup().SweepAsync(Start.AddMinutes(30), default);
        Assert.True((await _sessions.GetAsync(begin.SessionId))!.StagingRemoved);
    }

    // ---- Round 3: L2 audit ----

    [Fact]
    public async Task Begin_WritesUploadBeginAuditRow()
    {
        var svc = Service();
        await SeedFile(_share, "Docs/b.bin", Bytes(3));

        await BeginOk(svc, _contrib, _share, "Docs", "a.bin", 1234);
        await BeginOk(svc, _contrib, _share, "Docs", "b.bin", 7, overwrite: true);
        await BeginOk(svc, _contrib, _share, "Docs", "a.bin", 1234);

        var rows = (await _audit.QueryAsync(new AuditQuery(Operation: UploadSessionService.BeginOperation))).OrderBy(r => r.Path).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("Docs/a.bin", 1234L, ""), (rows[0].Path, rows[0].Bytes!.Value, rows[0].Error));
        Assert.Equal(("Docs/b.bin", 7L, "overwrite"), (rows[1].Path, rows[1].Bytes!.Value, rows[1].Error));
        Assert.All(rows, r => Assert.Equal(AuditResult.Success, r.Result));
        Assert.All(rows, r => Assert.Equal(_contrib.Upn, r.UserDisplay));
    }

    [Fact]
    public async Task Complete_OverwriteOfExistingFile_AuditedAsOverwrite()
    {
        var svc = Service();
        await SeedFile(_share, "a.bin", Bytes(3));
        var overwrite = await BeginOk(svc, _contrib, _share, "", "a.bin", 0, overwrite: true);
        var fresh = await BeginOk(svc, _contrib, _share, "", "b.bin", 0, overwrite: true);

        await svc.CompleteAsync(_contrib.Id, overwrite.SessionId, Ctx);
        await svc.CompleteAsync(_contrib.Id, fresh.SessionId, Ctx);

        Assert.Equal("a.bin", Assert.Single(await _audit.QueryAsync(new AuditQuery(Operation: "Overwrite"))).Path);
        Assert.Equal("b.bin", Assert.Single(await UploadAudits()).Path);
    }

    // ---- Round 3: L3 staging-name and target-kind guards ----

    [Fact]
    public async Task Sweep_TamperedStagingPath_NotDeleted()
    {
        await SeedFile(_share, "keep.txt", Bytes(4));
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        var session = (await _sessions.GetAsync(begin.SessionId))!;
        await _sessions.DeleteAsync(session.Id);
        await _sessions.CreateAsync(session with { StagingPath = "keep.txt" });

        await Cleanup().SweepAsync(Start.AddHours(25), default);

        Assert.DoesNotContain(_fake.Calls, c => c == "AbortUpload:keep.txt");
        Assert.Equal(Bytes(4), await ReadFile(_share, "keep.txt"));
        Assert.False((await _sessions.GetAsync(session.Id))!.StagingRemoved);
    }

    [Fact]
    public async Task Sweep_OtherSessionsStagingName_NotDeleted()
    {
        var svc = Service();
        var mine = await BeginOk(svc, _contrib, _share, "", "a.bin", 3);
        var other = await BeginOk(svc, _contrib, _share, "", "b.bin", 3);
        var session = (await _sessions.GetAsync(mine.SessionId))!;
        var otherStaging = (await _sessions.GetAsync(other.SessionId))!.StagingPath;
        await _sessions.DeleteAsync(session.Id);
        await _sessions.CreateAsync(session with { StagingPath = otherStaging, ExpiresAt = Start.AddMinutes(1) });

        await Cleanup().SweepAsync(Start.AddMinutes(2), default);

        Assert.Equal(0, _fake.StagingLength(_share.Id, otherStaging));
        Assert.DoesNotContain(_fake.Calls, c => c == $"AbortUpload:{otherStaging}");
    }

    [Fact]
    public async Task Sweep_TargetChangedKind_SkipsStorageCleanup()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        await _targets.UpsertAsync(_share with { Kind = StorageKind.BlobContainer });

        await Cleanup().SweepAsync(Start.AddHours(25), default);

        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Aborted, session.Status);
        Assert.False(session.StagingRemoved);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
    }

    [Fact]
    public void StagingIsOwnedBy_OnlyTheDerivedName()
    {
        var id = Guid.NewGuid();
        var s = new UploadSession(id, Guid.NewGuid(), Guid.NewGuid(), "Docs/a.bin", UploadSessionService.StagingPathFor("Docs/a.bin", id),
            1, ChunkMath.ChunkSize, false, Start, Start, UploadStatus.Open);

        Assert.True(UploadSessionService.StagingIsOwnedBy(s));
        Assert.False(UploadSessionService.StagingIsOwnedBy(s with { StagingPath = "Docs/a.bin" }));
        Assert.False(UploadSessionService.StagingIsOwnedBy(s with { StagingPath = UploadSessionService.StagingPathFor("Docs/a.bin", Guid.NewGuid()) }));
        Assert.False(UploadSessionService.StagingIsOwnedBy(s with { StagingPath = UploadSessionService.StagingPathFor("Other/a.bin", id) }));
        Assert.True(UploadSessionService.StagingIsOwnedBy(s with { TargetKind = StorageKind.BlobContainer, StagingPath = "Docs/a.bin" }));
        Assert.False(UploadSessionService.StagingIsOwnedBy(s with { TargetKind = StorageKind.BlobContainer }));
    }

    // ---- Round 3: carried minors ----

    [Fact]
    public async Task Complete_FilesStagingGoneAndSomeoneElsesFinalPresent_RestartNotSuccess()
    {
        var svc = Service();
        var data = Bytes(10);
        await SeedFile(_share, "a.bin", Bytes(4));
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length, overwrite: true);
        await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        await _fake.AbortUploadAsync(_share, (await _sessions.GetAsync(begin.SessionId))!.StagingPath, default);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadErrorCodes.Restart, result.Code);
        Assert.Equal(Bytes(4), await ReadFile(_share, "a.bin"));
    }

    [Fact]
    public async Task PutChunk_StagingGone_ConflictRestart()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await _fake.AbortUploadAsync(_share, (await _sessions.GetAsync(begin.SessionId))!.StagingPath, default);

        var result = await Put(svc, _contrib.Id, begin.SessionId, data, 0);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadErrorCodes.Restart, result.Code);
        AssertInterrupted(await _sessions.GetAsync(begin.SessionId));
        var again = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        Assert.NotEqual(begin.SessionId, again.SessionId);
        Assert.Empty(again.Received);
    }

    // ---- Round 3: M2 format checks before the database ----

    [Theory]
    [InlineData(-1, 10L, "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData(0, null, "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData(0, 4L * 1024 * 1024 + 1, "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData(0, 10L, null)]
    [InlineData(0, 10L, "short")]
    public async Task PutChunk_MalformedRequest_InvalidWithoutTouchingTheStore(int index, long? length, string? md5)
    {
        var svc = Service(sessions: new ThrowingSessionStore());

        var result = await svc.PutChunkAsync(_contrib.Id, Guid.NewGuid(), index, new MemoryStream(), length, md5, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
    }

    // ---- Round 3: L1 piece read timeout ----

    [Fact]
    public async Task PutChunk_StalledBody_TimesOutAsRetry()
    {
        var svc = Service();
        svc.PieceReadTimeout = TimeSpan.FromMilliseconds(200);
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await svc.PutChunkAsync(_contrib.Id, begin.SessionId, 0, new StallingStream(), data.Length, Md5(data), Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(UploadErrorCodes.Retry, result.Code);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- Round 4 ----

    [Fact]
    public async Task Complete_FilesStagingShorterThanSession_RestartAndAborted()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;
        await _fake.GrowStagingAsync(_share, staging, 4, default);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadErrorCodes.Restart, result.Code);
        Assert.Equal(StorageErrorMapper.StagingIncompleteMessage, result.Message);
        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Aborted, session.Status);
        Assert.True(session.StagingRemoved);
        Assert.Null(await _fake.GetItemAsync(_share, "a.bin", default));
        Assert.Equal(AuditResult.Failed, Assert.Single(await UploadAudits()).Result);
    }

    [Fact]
    public async Task Complete_FilesStagingLongerThanSession_Restart()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        await _fake.GrowStagingAsync(_share, (await _sessions.GetAsync(begin.SessionId))!.StagingPath, 4096, default);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(UploadErrorCodes.Restart, result.Code);
        Assert.Null(await _fake.GetItemAsync(_share, "a.bin", default));
    }

    [Fact]
    public async Task PutChunk_FilesStagingTruncatedExternally_ConflictRestart()
    {
        var svc = Service();
        var file = Bytes(9 * Mi);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 1);
        // Shrunk outside the portal; the recorded size still says full, so the next piece isn't grown first.
        await _fake.GrowStagingAsync(_share, (await _sessions.GetAsync(begin.SessionId))!.StagingPath, 0, default);

        var result = await Put(svc, _contrib.Id, begin.SessionId, file, 0);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadErrorCodes.Restart, result.Code);
        AssertInterrupted(await _sessions.GetAsync(begin.SessionId));

        // The next Begin for the same file starts afresh rather than resuming into the same failure.
        var again = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        Assert.NotEqual(begin.SessionId, again.SessionId);
        Assert.Empty(again.Received);
    }

    private void AssertInterrupted(UploadSession? session)
    {
        Assert.Equal(UploadStatus.Aborted, session!.Status);
        Assert.True(session.StagingRemoved);
        var row = Assert.Single(UploadAudits().GetAwaiter().GetResult(), r => r.Error == UploadSessionService.InterruptedMessage);
        Assert.Equal(AuditResult.Failed, row.Result);
    }

    [Fact]
    public async Task PutChunk_GrowTimesOut_RetryAndChunkNotWritten()
    {
        var svc = Service();
        svc.GrowTimeout = TimeSpan.FromMilliseconds(100);
        _fake.GrowStagingDelay = TimeSpan.FromSeconds(30);
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);

        var result = await Put(svc, _contrib.Id, begin.SessionId, data, 0);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal(UploadErrorCodes.Retry, result.Code);
        Assert.Equal(UploadSessionService.StorageSlowMessage, result.Message);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("WriteChunk"));
        Assert.Equal(0, (await _sessions.GetAsync(begin.SessionId))!.StagingSize);
        Assert.Empty(await _sessions.ReceivedChunksAsync(begin.SessionId));

        _fake.GrowStagingDelay = null;
        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, begin.SessionId, data, 0)).Outcome);
    }

    [Fact]
    public async Task Sweep_RefusedCleanup_LoggedOnceAndNotRetried()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        await _targets.UpsertAsync(_share with { Kind = StorageKind.BlobContainer });
        var log = new CountingLogger();
        var cleanup = new UploadCleanupService(_sessions, _targets, _users, _fake, new AuditService(_audit, NullLogger<AuditService>.Instance),
            _time, new ConfigurationBuilder().Build(), log);

        await cleanup.SweepAsync(Start.AddHours(25), default);
        await cleanup.SweepAsync(Start.AddHours(26), default);
        await cleanup.SweepAsync(Start.AddHours(27), default);

        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.True(session.CleanupRefused);
        Assert.False(session.StagingRemoved);
        Assert.Empty(await _sessions.ListAbortedWithStagingAsync());
        Assert.Equal(1, log.Errors);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task Sweep_TargetDeleted_RefusalRecorded()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        var emptyTargets = new InMemoryTargetStore();
        var cleanup = new UploadCleanupService(_sessions, emptyTargets, _users, _fake, new AuditService(_audit, NullLogger<AuditService>.Instance),
            _time, new ConfigurationBuilder().Build(), NullLogger<UploadCleanupService>.Instance);

        await cleanup.SweepAsync(Start.AddHours(25), default);

        Assert.True((await _sessions.GetAsync(begin.SessionId))!.CleanupRefused);
    }

    private sealed class CountingLogger : ILogger<UploadCleanupService>
    {
        public int Errors;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Interlocked.Increment(ref Errors);
        }
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

        await Cleanup(idleMinutes: 48 * 60).SweepAsync(Start.AddHours(24), default);

        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(expired.SessionId))!.Status);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(fresh.SessionId))!.Status);
        var row = Assert.Single(await UploadAudits());
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal(UploadCleanupService.ExpiredMessage, row.Error);
        Assert.Equal("old.bin", row.Path);
        Assert.True((await _sessions.GetAsync(expired.SessionId))!.StagingRemoved);
        Assert.Contains($"AbortUpload:{expiredStaging}", _fake.Calls);
        Assert.DoesNotContain($"AbortUpload:{freshStaging}", _fake.Calls);
    }

    [Fact]
    public async Task Sweep_SessionBeingCompleted_NotTouched()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 3);
        var sweepAt = Start.AddHours(25);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, sweepAt.AddMinutes(-29)));

        await Cleanup().SweepAsync(sweepAt, default);

        Assert.Equal(UploadStatus.Completing, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task Sweep_StuckCompletingWithFinalFile_CompletedAndAuditsSuccess()
    {
        var file = Bytes(10);
        var begin = await BeginOk(Service(), _contrib, _share, "Docs", "a.bin", file.Length);
        var session = (await _sessions.GetAsync(begin.SessionId))!;
        await _fake.GrowStagingAsync(_share, session.StagingPath, file.Length, default);
        await _fake.WriteChunkAsync(_share, session.StagingPath, session.FinalPath, "b0", 0, new MemoryStream(file), file.Length, MD5.HashData(file), default);
        await _fake.CompleteUploadAsync(_share, session.StagingPath, session.FinalPath, new[] { "b0" }, false, default, session.Id);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start.AddMinutes(1)));

        await Cleanup().SweepAsync(Start.AddMinutes(32), default);

        Assert.Equal(UploadStatus.Completed, (await _sessions.GetAsync(begin.SessionId))!.Status);
        var row = Assert.Single(await UploadAudits());
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Equal(file.Length, row.Bytes);
        Assert.Equal(_contrib.Id, row.UserId);
        Assert.Equal(_contrib.Upn, row.UserDisplay);
        Assert.Equal(_custA.Id, row.CustomerId);
        Assert.Equal("Docs/a.bin", row.Path);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task Sweep_StuckCompletingWithoutFinalFile_AbortedAndStagingRemoved()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "Docs", "a.bin", 10);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start.AddMinutes(1)));

        await Cleanup().SweepAsync(Start.AddMinutes(32), default);

        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Contains($"AbortUpload:{staging}", _fake.Calls);
        Assert.Throws<StorageFailureException>(() =>
            _fake.CompleteUploadAsync(_share, staging, "Docs/a.bin", Array.Empty<string>(), false, default).GetAwaiter().GetResult());
        var row = Assert.Single(await UploadAudits());
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal(UploadCleanupService.InterruptedCommitMessage, row.Error);
    }

    [Fact]
    public async Task Sweep_StuckCompletingFilesStagingStillPresent_AbortedEvenIfFinalExists()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 10, overwrite: true);
        await SeedFile(_share, "a.bin", Bytes(10));
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));

        await Cleanup().SweepAsync(Start.AddHours(1), default);

        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Equal(Bytes(10), await ReadFile(_share, "a.bin"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public async Task Sweep_StuckCompletingBlobLanded_Completed(int size)
    {
        var svc = Service();
        var data = Bytes(size);
        var begin = await BeginOk(svc, _contrib, _blob, "", "a.bin", size);
        if (size > 0) await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        var blocks = size > 0 ? new[] { ChunkMath.BlockId(begin.SessionId, 0) } : Array.Empty<string>();
        await _fake.CompleteUploadAsync(_blob, "a.bin", "a.bin", blocks, false, default, begin.SessionId);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));

        await Cleanup().SweepAsync(Start.AddHours(1), default);

        Assert.Equal(UploadStatus.Completed, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Equal(AuditResult.Success, Assert.Single(await UploadAudits()).Result);
    }

    [Fact]
    public async Task Sweep_StuckCompletingBlobSameSizeFromOtherSession_Aborted()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _blob, "", "a.bin", 10);
        await SeedFile(_blob, "a.bin", Bytes(10));
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));

        await Cleanup().SweepAsync(Start.AddHours(1), default);

        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Equal(Bytes(10), await ReadFile(_blob, "a.bin"));
    }

    [Fact]
    public async Task TrySetStatus_RequireClaimWithStaleClaim_RefusedAtStoreLevel()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 10);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));
        var listed = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Completing, UploadStatus.Completing, Start.AddMinutes(50)));

        Assert.False(await _sessions.TrySetStatusAsync(listed.Id, UploadStatus.Completing, UploadStatus.Aborted,
            requireClaim: true, expectedClaim: listed.ClaimedAt));
        Assert.True(await _sessions.TrySetStatusAsync(listed.Id, UploadStatus.Completing, UploadStatus.Aborted,
            requireClaim: true, expectedClaim: Start.AddMinutes(50)));
    }

    [Fact]
    public async Task Sweep_CompletingTimeoutFlooredAtTenMinutes()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 10);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));

        await Cleanup(completingTimeoutMinutes: 1).SweepAsync(Start.AddMinutes(5), default);
        Assert.Equal(UploadStatus.Completing, (await _sessions.GetAsync(begin.SessionId))!.Status);

        await Cleanup(completingTimeoutMinutes: 1).SweepAsync(Start.AddMinutes(11), default);
        Assert.Equal(UploadStatus.Aborted, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task Sweep_StuckCompletingStorageUnreachable_LeftForNextSweep()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 10);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));
        _fake.ThrowUnreachable = true;

        await Cleanup().SweepAsync(Start.AddHours(1), default);

        Assert.Equal(UploadStatus.Completing, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Empty(await UploadAudits());
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

    // ---- Task 9: guard, state-machine and cap coverage ----

    [Fact]
    public async Task Sweep_StuckCompletingReclaimedDuringLandedCheck_LeftAloneNoAudit()
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 10);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));
        _fake.FailOnCall = call =>
        {
            if (call.StartsWith("HasLanded"))
                Assert.True(_sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Completing, UploadStatus.Completing,
                    Start.AddHours(2)).GetAwaiter().GetResult());
            return null;
        };

        await Cleanup().SweepAsync(Start.AddHours(1), default);

        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Completing, session.Status);
        Assert.Equal(Start.AddHours(2), session.ClaimedAt);
        Assert.Empty(await NonBeginAudits());
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task Complete_AbortCompleteOrChunkDuringCommit_NotFoundAndNoSideEffects()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        Assert.Equal(AccessOutcome.Ok, (await Put(svc, _contrib.Id, begin.SessionId, data, 0)).Outcome);
        AccessResult? abort = null, again = null, chunk = null;
        _fake.FailOnCall = call =>
        {
            if (!call.StartsWith("CompleteUpload")) return null;
            abort = svc.AbortAsync(_contrib.Id, begin.SessionId, Ctx).GetAwaiter().GetResult();
            again = svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx).GetAwaiter().GetResult();
            chunk = Put(svc, _contrib.Id, begin.SessionId, data, 0).GetAwaiter().GetResult();
            return null;
        };

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Ok, result.Outcome);
        Assert.Equal(AccessOutcome.NotFound, abort!.Outcome);
        Assert.Equal(AccessOutcome.NotFound, again!.Outcome);
        Assert.Equal(AccessOutcome.NotFound, chunk!.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
        Assert.Equal(1, _fake.Calls.Count(c => c.StartsWith("CompleteUpload")));
        Assert.Equal(UploadStatus.Completed, (await _sessions.GetAsync(begin.SessionId))!.Status);
        Assert.Equal(AuditResult.Success, Assert.Single(await UploadAudits()).Result);
    }

    [Fact]
    public async Task Abort_TamperedStagingPath_RefusedReopenedNoStorageCall()
    {
        await SeedFile(_share, "keep.txt", Bytes(4));
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 10);
        var session = (await _sessions.GetAsync(begin.SessionId))!;
        await _sessions.DeleteAsync(session.Id);
        await _sessions.CreateAsync(session with { StagingPath = "keep.txt" });

        var result = await svc.AbortAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(session.Id))!.Status);
        Assert.DoesNotContain(_fake.Calls, c => c == "AbortUpload:keep.txt");
        Assert.Equal(Bytes(4), await ReadFile(_share, "keep.txt"));
        Assert.Empty(await NonBeginAudits());
    }

    [Fact]
    public async Task Abort_TargetChangedKindSinceBegin_RefusedSessionStaysOpen()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        var staging = (await _sessions.GetAsync(begin.SessionId))!.StagingPath;
        await _targets.UpsertAsync(_share with { Kind = StorageKind.BlobContainer });

        var result = await svc.AbortAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.NotEqual(AccessOutcome.Ok, result.Outcome);
        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Open, session.Status);
        Assert.False(session.StagingRemoved);
        Assert.NotNull(_fake.StagingLength(_share.Id, staging));
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task Begin_ReplacingOlderSessionWithTamperedStaging_CleanupRefusedNotDeleted()
    {
        await SeedFile(_share, "keep.txt", Bytes(4));
        var svc = Service();
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", 10);
        var older = (await _sessions.GetAsync(first.SessionId))!;
        await _sessions.DeleteAsync(older.Id);
        await _sessions.CreateAsync(older with { StagingPath = "keep.txt" });

        var second = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 12, false), Ctx);

        Assert.Equal(AccessOutcome.Ok, second.Outcome);
        var replaced = (await _sessions.GetAsync(older.Id))!;
        Assert.Equal(UploadStatus.Aborted, replaced.Status);
        Assert.True(replaced.CleanupRefused);
        Assert.False(replaced.StagingRemoved);
        Assert.DoesNotContain(_fake.Calls, c => c == "AbortUpload:keep.txt");
        Assert.Equal(Bytes(4), await ReadFile(_share, "keep.txt"));
        Assert.Equal(UploadSessionService.ReplacedMessage, Assert.Single(await UploadAudits()).Error);
    }

    [Fact]
    public async Task Status_GrantRevokedAfterBegin_Denied()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        await _grants.RemoveAsync(_custA.Id, _share.Id);

        var result = await svc.StatusAsync(_contrib.Id, begin.SessionId);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Abort_GrantRevokedAfterBegin_DeniedNoStorageCall()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        await Put(svc, _contrib.Id, begin.SessionId, data, 0);
        await _grants.RemoveAsync(_custA.Id, _share.Id);

        var result = await svc.AbortAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Denied, result.Outcome);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task Begin_ExpiredUnsweptOlderSessionOfSameFile_NotSubtractedFromCaps_Busy()
    {
        var svc = Service(maxOpen: 1);
        await BeginOk(svc, _contrib, _share, "", "a.bin", 10);
        _time.Now = Start.AddHours(25);
        await BeginOk(svc, _contrib, _share, "", "b.bin", 1);

        var again = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 12, false), Ctx);

        Assert.Equal(AccessOutcome.Busy, again.Outcome);
        Assert.Equal(UploadErrorCodes.Busy, again.Code);
    }

    [Fact]
    public async Task Begin_ExpiredUnsweptOlderSessionOfSameFile_NotSubtractedFromByteCap_Busy()
    {
        var svc = Service(maxBytes: 100);
        await BeginOk(svc, _contrib, _share, "", "a.bin", 90);
        _time.Now = Start.AddHours(25);
        await BeginOk(svc, _contrib, _share, "", "b.bin", 50);

        var again = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", 60, false), Ctx);

        Assert.Equal(AccessOutcome.Busy, again.Outcome);
        Assert.Equal(UploadErrorCodes.Busy, again.Code);
    }

    [Fact]
    public async Task Begin_CompletingSessionStillCountsAgainstOpenCap_Busy()
    {
        var svc = Service(maxOpen: 1);
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", 10);
        Assert.True(await _sessions.TrySetStatusAsync(first.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));

        var second = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "b.bin", 1, false), Ctx);

        Assert.Equal(AccessOutcome.Busy, second.Outcome);
    }

    [Fact]
    public async Task Begin_CompletingSessionStillCountsAgainstByteCap_Busy()
    {
        var svc = Service(maxBytes: 100);
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", 60);
        Assert.True(await _sessions.TrySetStatusAsync(first.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));

        var over = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "b.bin", 41, false), Ctx);
        var fits = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "c.bin", 40, false), Ctx);

        Assert.Equal(AccessOutcome.Busy, over.Outcome);
        Assert.Equal(AccessOutcome.Ok, fits.Outcome);
    }

    [Fact]
    public async Task Complete_ReceivedIncludesOutOfRangeIndex_StillMissingPiece_Invalid()
    {
        var svc = Service();
        var file = Bytes(2 * ChunkMath.ChunkSize + 1);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", file.Length);
        await Put(svc, _contrib.Id, begin.SessionId, file, 0);
        await Put(svc, _contrib.Id, begin.SessionId, file, 1);
        await _sessions.MarkChunkAsync(begin.SessionId, 7, Start);
        await _sessions.MarkChunkAsync(begin.SessionId, -1, Start);

        var result = await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx);

        Assert.Equal(AccessOutcome.Invalid, result.Outcome);
        Assert.Equal("Upload is missing 1 piece.", result.Message);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("CompleteUpload"));
        Assert.Equal(UploadStatus.Open, (await _sessions.GetAsync(begin.SessionId))!.Status);
    }

    [Fact]
    public async Task Begin_BlobOtherUsersStaleButCompletingSession_ConflictInProgress()
    {
        var svc = Service();
        var first = await BeginOk(svc, _contrib, _blob, "", "a.bin", 10);
        Assert.True(await _sessions.TrySetStatusAsync(first.SessionId, UploadStatus.Open, UploadStatus.Completing, Start.AddMinutes(1)));
        _time.Now = Start.AddMinutes(40);

        var result = await svc.BeginAsync(_colleague.Id, new BeginUploadRequest(_blob.Id, "", "a.bin", 10, false), Ctx);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadErrorCodes.InProgress, result.Code);
    }

    [Fact]
    public async Task PutChunk_StagingLostButSessionAlreadyEnded_NoSecondAuditRowNoAbort()
    {
        var svc = Service();
        var data = Bytes(10);
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        _fake.FailOnCall = call =>
        {
            if (!call.StartsWith("WriteChunk")) return null;
            _sessions.SetStatusAsync(begin.SessionId, UploadStatus.Aborted).GetAwaiter().GetResult();
            return new StorageFailureException(StorageFailureKind.NotFound, StorageErrorMapper.NotFoundMessage);
        };

        var result = await Put(svc, _contrib.Id, begin.SessionId, data, 0);

        Assert.Equal(AccessOutcome.Conflict, result.Outcome);
        Assert.Equal(UploadErrorCodes.Restart, result.Code);
        Assert.DoesNotContain(await NonBeginAudits(), e => e.Error == UploadSessionService.InterruptedMessage);
        Assert.Empty(await NonBeginAudits());
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
    }

    [Fact]
    public async Task Sweep_ExpiredSessionReopenedAfterFailedComplete_Aborted()
    {
        var svc = Service();
        var begin = await BeginOk(svc, _contrib, _share, "", "a.bin", 0);
        _fake.FailOnCall = call => call.StartsWith("CompleteUpload")
            ? new StorageFailureException(StorageFailureKind.Unreachable, StorageErrorMapper.UnreachableMessage) : null;
        Assert.Equal(AccessOutcome.Unavailable, (await svc.CompleteAsync(_contrib.Id, begin.SessionId, Ctx)).Outcome);
        _fake.FailOnCall = null;
        var reopened = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Open, reopened.Status);
        Assert.Equal(Start, reopened.ClaimedAt);

        await Cleanup().SweepAsync(Start.AddHours(25), default);

        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Aborted, session.Status);
        Assert.True(session.StagingRemoved);
        var rows = await UploadAudits();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Error == UploadCleanupService.ExpiredMessage);
        Assert.Single(rows, r => r.Result == AuditResult.Failed && r.Error == StorageErrorMapper.UnreachableMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_StuckCompletingTargetChangedKindOrDeleted_AbortedWithoutLandedCheck_RefusalRecorded(bool targetDeleted)
    {
        var begin = await BeginOk(Service(), _contrib, _share, "", "a.bin", 10);
        Assert.True(await _sessions.TrySetStatusAsync(begin.SessionId, UploadStatus.Open, UploadStatus.Completing, Start));
        ITargetStore targets = _targets;
        if (targetDeleted)
            targets = new InMemoryTargetStore();
        else
            await _targets.UpsertAsync(_share with { Kind = StorageKind.BlobContainer });
        var cleanup = new UploadCleanupService(_sessions, targets, _users, _fake, new AuditService(_audit, NullLogger<AuditService>.Instance),
            _time, new ConfigurationBuilder().Build(), NullLogger<UploadCleanupService>.Instance);

        await cleanup.SweepAsync(Start.AddHours(1), default);

        var session = (await _sessions.GetAsync(begin.SessionId))!;
        Assert.Equal(UploadStatus.Aborted, session.Status);
        Assert.True(session.CleanupRefused);
        Assert.False(session.StagingRemoved);
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("HasLanded"));
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("AbortUpload"));
        var row = Assert.Single(await NonBeginAudits());
        Assert.Equal(AuditResult.Failed, row.Result);
        Assert.Equal(UploadCleanupService.InterruptedCommitMessage, row.Error);
        Assert.Equal(targetDeleted ? null : _custA.Id, row.CustomerId);
    }

    [Fact]
    public async Task Begin_ResumeAfterFileAppeared_UpdatesExistedAtBegin_AuditsOverwrite()
    {
        var svc = Service();
        var data = Bytes(10);
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length);
        Assert.False((await _sessions.GetAsync(first.SessionId))!.ExistedAtBegin);
        await SeedFile(_share, "a.bin", Bytes(4));

        var resumed = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length, overwrite: true);

        Assert.Equal(first.SessionId, resumed.SessionId);
        var session = (await _sessions.GetAsync(first.SessionId))!;
        Assert.True(session.Overwrite);
        Assert.True(session.ExistedAtBegin);
        await Put(svc, _contrib.Id, first.SessionId, data, 0);
        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, first.SessionId, Ctx)).Outcome);
        Assert.Single(await UploadAudits(nameof(StorageOperation.Overwrite)));
        Assert.Empty(await UploadAudits());
    }

    [Fact]
    public async Task Begin_ResumeAfterFileVanished_UpdatesExistedAtBegin_AuditsUpload()
    {
        var svc = Service();
        var data = Bytes(10);
        await SeedFile(_share, "a.bin", Bytes(4));
        var first = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length, overwrite: true);
        Assert.True((await _sessions.GetAsync(first.SessionId))!.ExistedAtBegin);
        await _fake.DeleteAsync(_share, "a.bin", false, default);

        var resumed = await BeginOk(svc, _contrib, _share, "", "a.bin", data.Length, overwrite: true);

        Assert.Equal(first.SessionId, resumed.SessionId);
        Assert.False((await _sessions.GetAsync(first.SessionId))!.ExistedAtBegin);
        await Put(svc, _contrib.Id, first.SessionId, data, 0);
        Assert.Equal(AccessOutcome.Ok, (await svc.CompleteAsync(_contrib.Id, first.SessionId, Ctx)).Outcome);
        Assert.Single(await UploadAudits());
        Assert.Empty(await UploadAudits(nameof(StorageOperation.Overwrite)));
    }

    // StagingFits allows a leaf of 255 - (1 dot + 11 suffix + 32 session id) = 211 characters on Files; Blob has no staging name.
    [Fact]
    public async Task Begin_FilesStagingNameLengthBoundary()
    {
        var atLimit = new string('n', 207) + ".bin";
        var overLimit = new string('n', 208) + ".bin";
        Assert.Equal(211, atLimit.Length);

        var files211 = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", atLimit, 3, false), Ctx);
        var files212 = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", overLimit, 3, false), Ctx);
        var blob212 = await Service().BeginAsync(_contrib.Id, new BeginUploadRequest(_blob.Id, "", overLimit, 3, false), Ctx);

        Assert.Equal(AccessOutcome.Ok, files211.Outcome);
        Assert.Equal(AccessOutcome.Invalid, files212.Outcome);
        Assert.Equal(UploadSessionService.NameTooLongMessage, files212.Message);
        Assert.Equal(AccessOutcome.Ok, blob212.Outcome);
    }

    [Fact]
    public async Task Begin_ConfiguredMaxAboveBlobBlockLimit_ClampedAndWarned()
    {
        var log = new WarningLogger();
        var svc = Service(maxBytes: 1L << 40, logger: log);

        var atCeiling = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "a.bin", ChunkMath.MaxUploadBytes, false), Ctx);
        var overCeiling = await svc.BeginAsync(_contrib.Id, new BeginUploadRequest(_share.Id, "", "b.bin", ChunkMath.MaxUploadBytes + 1, false), Ctx);

        Assert.Equal(1, log.Warnings);
        Assert.Equal(AccessOutcome.Ok, atCeiling.Outcome);
        Assert.Equal(AccessOutcome.Invalid, overCeiling.Outcome);
        Assert.Equal(UploadSessionService.TooLargeMessage(ChunkMath.MaxUploadBytes), overCeiling.Message);
    }

    [Fact]
    public void Service_ConfiguredMaxWithinBlobBlockLimit_NotWarned()
    {
        var log = new WarningLogger();

        Service(maxBytes: 1L << 30, logger: log);

        Assert.Equal(0, log.Warnings);
    }

    private sealed class WarningLogger : ILogger<UploadSessionService>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref Warnings);
        }
    }

    /// <summary>Like Kestrel's request body: no seeking, no length; optionally fails part-way like a dropped connection.</summary>
    private sealed class NonSeekableStream(byte[] data, int? failAfter = null) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAfter is { } f && _pos >= f) throw new IOException("The client disconnected.");
            var limit = failAfter is { } g ? Math.Min(data.Length, g) : data.Length;
            var n = Math.Min(Math.Min(count, limit - _pos), 3);
            if (n <= 0 && failAfter is not null) throw new IOException("The client disconnected.");
            Array.Copy(data, _pos, buffer, offset, Math.Max(0, n));
            _pos += Math.Max(0, n);
            return Math.Max(0, n);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
