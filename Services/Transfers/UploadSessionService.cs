using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using CloudSelfService.Endpoints;
using CloudSelfService.Models;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Storage;

namespace CloudSelfService.Services.Transfers;

public sealed record BeginUploadRequest(Guid TargetId, string Folder, string FileName, long Size, bool Overwrite);

public sealed record BeginUploadResponse(Guid SessionId, int ChunkSize, int ChunkCount, int ParallelChunks, IReadOnlyList<int> Received);

/// <summary>
/// Chunked, resumable uploads. Every call re-reads the session and goes back through
/// <see cref="StorageAccessService.AuthorizeAsync"/>, so a revoked grant or a deactivated user stops the very next
/// chunk. A session is only ever visible to the user who began it. Store (database) failures propagate, as with the
/// access service; the HTTP boundary maps them to 503.
/// </summary>
public sealed class UploadSessionService
{
    public const long DefaultMaxUploadBytes = 53_687_091_200;
    public const int DefaultParallelChunks = 4;
    public const int DefaultSessionHours = 24;
    public const int DefaultBlobLockMinutes = 15;
    public const string CancelledByUserMessage = "Cancelled by user";
    public const string NegativeSizeMessage = "File size can't be negative.";
    public const string Md5RequiredMessage = "Each piece needs a Content-MD5 header holding its base64 MD5 hash.";
    public const string NameTooLongMessage = "That file name is too long.";
    public const string LongChunkMessage = "Upload piece was longer than expected.";
    public const string ChunkCorruptedMessage = "Upload piece was corrupted in transit; it will be retried.";
    public const string ChunkIncompleteMessage = "Upload piece did not arrive completely; it will be retried.";
    public const string OtherUserUploadingMessage = "Someone else is uploading this file right now.";
    public const string UploadLostMessage = "This upload's data is no longer in storage. Please start it again.";
    private const string StagingSuffix = ".cssupload-";
    private const int MaxSegmentLength = 255;
    private static readonly string Operation = StorageOperation.Upload.ToString();

    private readonly StorageAccessService _access;
    private readonly IUploadSessionStore _sessions;
    private readonly IStorageBackendResolver _backends;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadSessionService> _logger;
    private readonly long _maxBytes;
    private readonly int _parallel;
    private readonly TimeSpan _lifetime;
    private readonly TimeSpan _blobLock;

    public UploadSessionService(StorageAccessService access, IUploadSessionStore sessions, IStorageBackendResolver backends,
        AuditService audit, TimeProvider time, IConfiguration config, ILogger<UploadSessionService> logger)
    {
        _access = access;
        _sessions = sessions;
        _backends = backends;
        _audit = audit;
        _time = time;
        _logger = logger;
        _maxBytes = Math.Max(0, config.GetValue<long?>("Transfers:MaxUploadBytes") ?? DefaultMaxUploadBytes);
        _parallel = Math.Clamp(config.GetValue<int?>("Transfers:ParallelChunks") ?? DefaultParallelChunks, 1, 16);
        _lifetime = TimeSpan.FromHours(Math.Max(1, config.GetValue<int?>("Transfers:SessionHours") ?? DefaultSessionHours));
        _blobLock = TimeSpan.FromMinutes(Math.Max(1, config.GetValue<int?>("Transfers:BlobLockMinutes") ?? DefaultBlobLockMinutes));
    }

    public static string TooLargeMessage(long maxBytes) =>
        $"File is larger than the {(maxBytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture)} GB limit.";

    public static string MissingPiecesMessage(int missing) =>
        $"Upload is missing {missing} {(missing == 1 ? "piece" : "pieces")}.";

    public static string StagingPathFor(string finalPath, Guid sessionId) =>
        StoragePath.Combine(StoragePath.Parent(finalPath), $".{StoragePath.Leaf(finalPath)}{StagingSuffix}{sessionId:N}");

    public async Task<AccessResult<BeginUploadResponse>> BeginAsync(Guid userId, BeginUploadRequest req, AuditContext ctx,
        CancellationToken ct = default)
    {
        if (req.Size < 0)
            return new(AccessOutcome.Invalid, null, NegativeSizeMessage);
        if (req.Size > _maxBytes)
            return new(AccessOutcome.Invalid, null, TooLargeMessage(_maxBytes));
        if (!StoragePath.TryNormalizePath(req.Folder, out var folder, out var error))
            return new(AccessOutcome.Invalid, null, error);
        if (!StoragePath.TryValidateName(req.FileName, out error))
            return new(AccessOutcome.Invalid, null, error);
        if (StoragePaths.IsHiddenName(req.FileName))
            return new(AccessOutcome.Invalid, null, StorageAccessService.ReservedNameMessage);
        // Re-normalising the combined path enforces the overall length limit.
        if (!StoragePath.TryNormalizePath(StoragePath.Combine(folder, req.FileName), out var finalPath, out error))
            return new(AccessOutcome.Invalid, null, error);

        var auth = await _access.AuthorizeAsync(userId, req.TargetId, StorageOperation.Upload, finalPath, ctx, ct);
        if (auth.Outcome != AccessOutcome.Ok)
            return new(auth.Outcome, null, auth.Message);
        var target = auth.Value!;

        if (target.Kind == StorageKind.FileShare && !StagingFits(finalPath))
            return new(AccessOutcome.Invalid, null, NameTooLongMessage);

        var backend = _backends.For(target);
        try
        {
            // Storage creates missing folders implicitly, so a folder that doesn't exist yet is a new name.
            if (StoragePath.NonStrictFolderPrefix(folder) is { } newFolders
                && await backend.GetItemAsync(target, newFolders, ct) is not { IsFolder: true })
                return new(AccessOutcome.Invalid, null, StoragePath.InvisibleCharactersMessage);

            var existing = await backend.GetItemAsync(target, finalPath, ct);
            if (existing is not null && (existing.IsFolder || !req.Overwrite))
                return new(AccessOutcome.AlreadyExists, null, StorageErrorMapper.AlreadyExistsMessage);
            if (existing is not null)
            {
                auth = await _access.AuthorizeAsync(userId, req.TargetId, StorageOperation.Overwrite, finalPath, ctx, ct);
                if (auth.Outcome != AccessOutcome.Ok)
                    return new(auth.Outcome, null, auth.Message);
            }
        }
        catch (StorageFailureException failure)
        {
            LogStorageFailure(failure, "begin", target.Id, finalPath, ctx);
            return new(OutcomeOf(failure), null, failure.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUnexpected(ex, "begin", target.Id, finalPath, ctx);
            return new(AccessOutcome.Unavailable, null, StorageErrorMapper.UnreachableMessage);
        }

        var now = _time.GetUtcNow();
        // Blob blocks are staged on the final name, so two users' sessions there share one uncommitted block list and
        // whichever commits first discards the other's blocks. Only a recently active session holds the name: a stalled
        // one would otherwise lock the file for a day, and its owner gets a clean "restart" if they come back.
        if (target.Kind == StorageKind.BlobContainer
            && (await _sessions.ListActiveForPathAsync(target.Id, finalPath, now, ct))
                .Any(o => o.UserId != userId && HoldsBlobName(o, now)))
            return new(AccessOutcome.Conflict, null, OtherUserUploadingMessage, Code: UploadErrorCodes.InProgress);

        var resumable = await _sessions.FindResumableAsync(userId, target.Id, finalPath, req.Size, now, ct);
        if (resumable is not null)
        {
            if (resumable.Overwrite != req.Overwrite)
                await _sessions.SetOverwriteAsync(resumable.Id, req.Overwrite, ct);
            var received = await _sessions.ReceivedChunksAsync(resumable.Id, ct);
            return new(AccessOutcome.Ok, Response(resumable.Id, req.Size, received), null, auth.UserUpn);
        }

        var id = Guid.NewGuid();
        var staging = target.Kind == StorageKind.FileShare ? StagingPathFor(finalPath, id) : finalPath;
        // The row is written first so the expiry sweep can always find (and remove) whatever storage was prepared.
        await _sessions.CreateAsync(new UploadSession(id, userId, target.Id, finalPath, staging, req.Size, ChunkMath.ChunkSize,
            req.Overwrite, now, now + _lifetime, UploadStatus.Open), ct);
        try
        {
            await backend.BeginUploadAsync(target, staging, req.Size, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _sessions.TrySetStatusAsync(id, UploadStatus.Open, UploadStatus.Aborted, ct: CancellationToken.None);
            if (ex is StorageFailureException failure)
            {
                LogStorageFailure(failure, "begin", target.Id, finalPath, ctx);
                return new(OutcomeOf(failure), null, failure.Message);
            }
            LogUnexpected(ex, "begin", target.Id, finalPath, ctx);
            return new(AccessOutcome.Unavailable, null, StorageErrorMapper.UnreachableMessage);
        }
        return new(AccessOutcome.Ok, Response(id, req.Size, Array.Empty<int>()), null, auth.UserUpn);
    }

    public async Task<AccessResult<IReadOnlyList<int>>> StatusAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await LoadAsync(userId, sessionId, ct);
        if (session is null)
            return new(AccessOutcome.NotFound, null, StorageErrorMapper.NotFoundMessage);
        var level = await _access.GetLevelAsync(userId, session.TargetId, ct);
        if (!AccessPolicy.Allows(level, OperationOf(session)))
            return new(AccessOutcome.Denied, null, StorageAccessService.DeniedMessage);
        return new(AccessOutcome.Ok, await _sessions.ReceivedChunksAsync(sessionId, ct));
    }

    public async Task<AccessResult> PutChunkAsync(Guid userId, Guid sessionId, int index, Stream body, long? contentLength,
        string? contentMd5Base64, AuditContext ctx, CancellationToken ct = default)
    {
        var session = await LoadAsync(userId, sessionId, ct);
        if (session is null)
            return new(AccessOutcome.NotFound, StorageErrorMapper.NotFoundMessage);

        var count = ChunkMath.ChunkCount(session.Size);
        if (index < 0 || index >= count)
            return new(AccessOutcome.Invalid, $"Piece {index} is out of range; this upload has {count}.");
        var expected = ChunkMath.ExpectedLength(session.Size, index);
        if (contentLength != expected)
            return new(AccessOutcome.Invalid, $"Piece {index} must be exactly {expected} bytes.");
        if (!TryParseMd5(contentMd5Base64, out var md5))
            return new(AccessOutcome.Invalid, Md5RequiredMessage);

        var auth = await _access.AuthorizeAsync(userId, session.TargetId, OperationOf(session), session.FinalPath, ctx, ct);
        if (auth.Outcome != AccessOutcome.Ok)
            return new(auth.Outcome, auth.Message);
        var target = auth.Value!;

        // The Azure SDKs need a seekable stream of known length and Kestrel's request body is neither, so the piece is
        // buffered (at most ChunkSize; the Chunks rate limit bounds how many per user) and checked here before storage
        // sees it. Storage still verifies the same MD5 end to end.
        var length = (int)expected;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            var read = await ReadPieceAsync(body, buffer, length, target.Id, session.FinalPath, ctx, ct);
            if (read.Outcome != AccessOutcome.Ok)
                return read;
            if (!MD5.HashData(buffer.AsSpan(0, length)).AsSpan().SequenceEqual(md5))
                return new(AccessOutcome.Invalid, ChunkCorruptedMessage, UploadErrorCodes.Retry);

            using var piece = new MemoryStream(buffer, 0, length, writable: false);
            await _backends.For(target).WriteChunkAsync(target, session.StagingPath, session.FinalPath,
                ChunkMath.BlockId(session.Id, index), (long)index * ChunkMath.ChunkSize, piece, expected, md5, ct);
        }
        catch (StorageFailureException failure)
        {
            LogStorageFailure(failure, "chunk", target.Id, session.FinalPath, ctx);
            return new(OutcomeOf(failure), failure.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUnexpected(ex, "chunk", target.Id, session.FinalPath, ctx);
            return new(AccessOutcome.Unavailable, StorageErrorMapper.UnreachableMessage);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        await _sessions.MarkChunkAsync(session.Id, index, _time.GetUtcNow(), ct);
        return new(AccessOutcome.Ok);
    }

    /// <summary>Reads exactly <paramref name="length"/> bytes and confirms nothing follows.</summary>
    private async Task<AccessResult> ReadPieceAsync(Stream body, byte[] buffer, int length, Guid targetId, string path,
        AuditContext ctx, CancellationToken ct)
    {
        try
        {
            var total = 0;
            while (total < length)
            {
                var n = await body.ReadAsync(buffer.AsMemory(total, length - total), ct);
                if (n == 0)
                    return new(AccessOutcome.Invalid, StorageErrorMapper.ShortChunkMessage);
                total += n;
            }
            var probe = new byte[1];
            if (await body.ReadAsync(probe, ct) > 0)
                return new(AccessOutcome.Invalid, LongChunkMessage);
            return new(AccessOutcome.Ok);
        }
        catch (IOException ex)
        {
            // A dropped connection or malformed body (BadHttpRequestException is an IOException) is the client's
            // problem, not storage's: no error-level noise, and the client simply retries the piece.
            _logger.LogInformation("Upload piece for target {TargetId} at {Path} was not received completely: {Reason} (correlation {CorrelationId}).",
                targetId, path, ex.Message, ctx.CorrelationId);
            return new(AccessOutcome.Invalid, ChunkIncompleteMessage, UploadErrorCodes.Retry);
        }
    }

    public async Task<AccessResult> CompleteAsync(Guid userId, Guid sessionId, AuditContext ctx, CancellationToken ct = default)
    {
        var session = await LoadAsync(userId, sessionId, ct);
        if (session is null)
            return new(AccessOutcome.NotFound, StorageErrorMapper.NotFoundMessage);

        var count = ChunkMath.ChunkCount(session.Size);
        var received = (await _sessions.ReceivedChunksAsync(session.Id, ct)).Where(i => i >= 0 && i < count).Distinct().Count();
        if (received < count)
            return new(AccessOutcome.Invalid, MissingPiecesMessage(count - received));

        var auth = await _access.AuthorizeAsync(userId, session.TargetId, OperationOf(session), session.FinalPath, ctx, ct);
        if (auth.Outcome != AccessOutcome.Ok)
            return new(auth.Outcome, auth.Message);
        var target = auth.Value!;

        // Claiming Completing is what keeps the expiry sweep, a parallel Complete, or an Abort off this session.
        if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Open, UploadStatus.Completing, _time.GetUtcNow(), ct))
            return new(AccessOutcome.NotFound, StorageErrorMapper.NotFoundMessage);

        var blockIds = Enumerable.Range(0, count).Select(i => ChunkMath.BlockId(session.Id, i)).ToList();
        string? failureMessage = null;
        var outcome = AccessOutcome.Ok;
        var lost = false;
        StorageFailureKind? failedKind = null;
        try
        {
            // Not tied to the request: a client that disconnects mid-commit must not leave the session half-claimed.
            await _backends.For(target).CompleteUploadAsync(target, session.StagingPath, session.FinalPath, blockIds,
                session.Overwrite, CancellationToken.None, session.Id);
        }
        catch (StorageFailureException failure)
        {
            LogStorageFailure(failure, "complete", target.Id, session.FinalPath, ctx);
            (outcome, failureMessage, failedKind) = (OutcomeOf(failure), failure.Message, failure.Kind);
            // The staged data is gone (Files staging removed, or blob blocks discarded by another commit), so a retry
            // can never succeed: end the session and tell the client to start again.
            if (failure.Kind is StorageFailureKind.NotFound or StorageFailureKind.Conflict)
            {
                lost = true;
                (outcome, failureMessage) = (AccessOutcome.Conflict,
                    failure.Kind == StorageFailureKind.Conflict ? failure.Message : UploadLostMessage);
            }
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "complete", target.Id, session.FinalPath, ctx);
            (outcome, failureMessage) = (AccessOutcome.Unavailable, StorageErrorMapper.UnreachableMessage);
        }

        // These failures can also mean an earlier attempt's commit landed and only its reply was lost (a Files rename
        // already done, or a no-overwrite blob commit now finding its own blob). Storage is asked definitively before
        // the upload is written off or the user is shown a name clash.
        if (failedKind is StorageFailureKind.NotFound or StorageFailureKind.Conflict or StorageFailureKind.AlreadyExists)
        {
            try
            {
                if (await _backends.For(target).HasLandedAsync(target, session.StagingPath, session.FinalPath, session.Id,
                        session.Size, CancellationToken.None))
                    (lost, failureMessage, outcome) = (false, null, AccessOutcome.Ok);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not confirm whether upload {SessionId} landed on target {TargetId}; leaving it open.",
                    session.Id, target.Id);
                (lost, outcome, failureMessage) = (false, AccessOutcome.Unavailable, StorageErrorMapper.UnreachableMessage);
            }
        }

        if (failureMessage is not null)
        {
            if (lost)
            {
                // Losing this race means the cleanup sweep already settled the session (and wrote its row).
                if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Completing, UploadStatus.Aborted, ct: CancellationToken.None))
                {
                    _logger.LogWarning("Upload {SessionId} was settled by cleanup while its commit was failing.", session.Id);
                    var settled = await _sessions.GetAsync(session.Id, CancellationToken.None);
                    return settled?.Status == UploadStatus.Completed
                        ? new(AccessOutcome.Ok)
                        : new(AccessOutcome.Conflict, failureMessage, UploadErrorCodes.Restart);
                }
                await AbortQuietlyAsync(target, session);
                await RecordAsync(userId, auth.UserUpn, target, session.FinalPath, 0, AuditResult.Failed, failureMessage, ctx);
                return new(AccessOutcome.Conflict, failureMessage, UploadErrorCodes.Restart);
            }
            else
            {
                // Back to Open so the user can retry, or resume with overwrite after a name clash.
                await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Completing, UploadStatus.Open, ct: CancellationToken.None);
            }
            await RecordAsync(userId, auth.UserUpn, target, session.FinalPath, 0, AuditResult.Failed, failureMessage, ctx);
            return new(outcome, failureMessage);
        }

        // Conditional so that if the cleanup sweep already settled a stuck commit, the Success row isn't written twice.
        if (await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Completing, UploadStatus.Completed, ct: CancellationToken.None))
            await RecordAsync(userId, auth.UserUpn, target, session.FinalPath, session.Size, AuditResult.Success, "", ctx);
        else
            _logger.LogWarning("Upload {SessionId} committed but was already settled by cleanup; no second audit row written.", session.Id);
        return new(AccessOutcome.Ok);
    }

    public async Task<AccessResult> AbortAsync(Guid userId, Guid sessionId, AuditContext ctx, CancellationToken ct = default)
    {
        var session = await LoadAsync(userId, sessionId, ct);
        if (session is null)
            return new(AccessOutcome.NotFound, StorageErrorMapper.NotFoundMessage);

        var auth = await _access.AuthorizeAsync(userId, session.TargetId, OperationOf(session), session.FinalPath, ctx, ct);
        if (auth.Outcome != AccessOutcome.Ok)
            return new(auth.Outcome, auth.Message);
        var target = auth.Value!;

        if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Open, UploadStatus.Aborted, ct: ct))
            return new(AccessOutcome.NotFound, StorageErrorMapper.NotFoundMessage);

        try
        {
            await _backends.For(target).AbortUploadAsync(target, session.StagingPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Left Open so a retry, or the expiry sweep, removes the staged data.
            await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Aborted, UploadStatus.Open, ct: CancellationToken.None);
            if (ex is StorageFailureException failure)
            {
                LogStorageFailure(failure, "abort", target.Id, session.FinalPath, ctx);
                return new(OutcomeOf(failure), failure.Message);
            }
            LogUnexpected(ex, "abort", target.Id, session.FinalPath, ctx);
            return new(AccessOutcome.Unavailable, StorageErrorMapper.UnreachableMessage);
        }

        await RecordAsync(userId, auth.UserUpn, target, session.FinalPath, 0, AuditResult.Failed, CancelledByUserMessage, ctx);
        return new(AccessOutcome.Ok);
    }

    private async Task AbortQuietlyAsync(StorageTarget target, UploadSession session)
    {
        try
        {
            await _backends.For(target).AbortUploadAsync(target, session.StagingPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove staged data for upload {SessionId} on target {TargetId}.", session.Id, target.Id);
        }
    }

    /// <summary>The caller's own Open, unexpired session; null for anything else, so another user's id is indistinguishable from a missing one.</summary>
    private async Task<UploadSession?> LoadAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        return session is not null && session.UserId == userId && session.Status == UploadStatus.Open
               && session.ExpiresAt > _time.GetUtcNow()
            ? session
            : null;
    }

    private BeginUploadResponse Response(Guid id, long size, IReadOnlyList<int> received) =>
        new(id, ChunkMath.ChunkSize, ChunkMath.ChunkCount(size), _parallel, received);

    private bool HoldsBlobName(UploadSession s, DateTimeOffset now) =>
        s.Status == UploadStatus.Completing || now - s.CreatedAt < _blobLock
        || (s.LastActivityAt is { } active && now - active < _blobLock);

    private static StorageOperation OperationOf(UploadSession s) => s.Overwrite ? StorageOperation.Overwrite : StorageOperation.Upload;

    private static bool StagingFits(string finalPath)
    {
        var leafLength = 1 + StoragePath.Leaf(finalPath).Length + StagingSuffix.Length + 32;
        return leafLength <= MaxSegmentLength
               && StoragePath.TryNormalizePath(StagingPathFor(finalPath, Guid.Empty), out _, out _);
    }

    private static bool TryParseMd5(string? base64, out byte[] md5)
    {
        md5 = new byte[16];
        if (string.IsNullOrWhiteSpace(base64) || base64.Trim().Length != 24)
            return false;
        return Convert.TryFromBase64String(base64.Trim(), md5, out var written) && written == 16;
    }

    private static AccessOutcome OutcomeOf(StorageFailureException failure) => failure.Kind switch
    {
        StorageFailureKind.NotFound => AccessOutcome.NotFound,
        StorageFailureKind.AlreadyExists => AccessOutcome.AlreadyExists,
        _ => AccessOutcome.Unavailable,
    };

    private void LogStorageFailure(StorageFailureException failure, string step, Guid targetId, string path, AuditContext ctx) =>
        _logger.LogWarning(failure.InnerException ?? failure,
            "Upload {Step} failed on target {TargetId} at {Path}: {Kind} (correlation {CorrelationId}).",
            step, targetId, path, failure.Kind, ctx.CorrelationId);

    private void LogUnexpected(Exception ex, string step, Guid targetId, string path, AuditContext ctx) =>
        _logger.LogError(ex, "Upload {Step} failed unexpectedly on target {TargetId} at {Path} (correlation {CorrelationId}).",
            step, targetId, path, ctx.CorrelationId);

    // Not tied to the request token: a client that disconnects must not erase the audit row.
    private Task RecordAsync(Guid userId, string? upn, StorageTarget target, string path, long bytes, AuditResult result,
        string error, AuditContext ctx) =>
        _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), userId, upn ?? "", target.CustomerId, target.Id,
            Operation, path, bytes, result, error, ctx.SourceIp, ctx.CorrelationId), CancellationToken.None);
}
