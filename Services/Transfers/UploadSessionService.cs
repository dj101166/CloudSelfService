using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using CloudSelfService.Models;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Storage;

namespace CloudSelfService.Services.Transfers;

/// <summary>FirstPieceMd5 (optional): base64 MD5 of the file's first 4 MiB (ChunkMath.FingerprintBytes) whatever the piece size, so a different file with the same name and size starts afresh.</summary>
public sealed record BeginUploadRequest(Guid TargetId, string Folder, string FileName, long Size, bool Overwrite,
    string? FirstPieceMd5 = null);

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
    public const int DefaultMaxOpenSessionsPerUser = 8;
    public const int DefaultMinChunkBytesPerSecond = 64 * 1024;
    public static readonly TimeSpan DefaultPieceReadTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan DefaultGrowTimeout = TimeSpan.FromSeconds(30);
    public const string BeginOperation = "UploadBegin";
    public const string CancelledByUserMessage = "Cancelled by user";
    public const string ReplacedMessage = "Replaced by a new upload of the same file";
    public const string NegativeSizeMessage = "File size can't be negative.";
    public const string Md5RequiredMessage = "Each piece needs a Content-MD5 header holding its base64 MD5 hash.";
    public const string NameTooLongMessage = "That file name is too long.";
    public const string ShortChunkMessage = "Upload piece was shorter than expected.";
    public const string LongChunkMessage = "Upload piece was longer than expected.";
    public const string ChunkCorruptedMessage = "Upload piece was corrupted in transit; it will be retried.";
    public const string ChunkIncompleteMessage = "Upload piece did not arrive completely; it will be retried.";
    public const string OtherUserUploadingMessage = "Someone else is uploading this file right now.";
    public const string UploadLostMessage = "This upload's data is no longer in storage. Please start it again.";
    public const string TooManyUploadsMessage = "You have too many uploads in progress. Finish or cancel one, then try again.";
    public const string StorageSlowMessage = "Storage was slow to respond; the piece will be retried.";
    public const string InterruptedMessage = "Upload was interrupted; please start again.";
    public const string FirstPieceMd5InvalidMessage = "firstPieceMd5 must be the base64 MD5 of the first piece.";
    public const string WaitMessage = "The server is busy with other large uploads. This piece will be sent again shortly.";
    private const string StagingSuffix = ".cssupload-";
    private const int MaxSegmentLength = 255;
    // Four 4 MiB pieces in flight is what today's uploads run at; larger pieces send fewer at once to stay near it.
    private const int InFlightTargetBytes = 16 * 1024 * 1024;

    private readonly StorageAccessService _access;
    private readonly IUploadSessionStore _sessions;
    private readonly IStorageBackendResolver _backends;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly PieceMemoryBudget _budget;
    private readonly ILogger<UploadSessionService> _logger;
    private readonly long _maxBytes;
    private readonly int _parallel;
    private readonly TimeSpan _lifetime;
    private readonly TimeSpan _blobLock;
    private readonly int _maxOpenSessions;
    private readonly int _minChunkBytesPerSecond;

    /// <summary>How long one piece may take to arrive before the read is abandoned. Settable for tests.</summary>
    internal TimeSpan PieceReadTimeout { get; set; } = DefaultPieceReadTimeout;

    /// <summary>A piece of <paramref name="length"/> bytes may take as long as it would at the minimum data rate, never less than <see cref="PieceReadTimeout"/>.</summary>
    internal TimeSpan PieceReadTimeoutFor(long length)
    {
        var atMinRate = TimeSpan.FromSeconds((double)length / _minChunkBytesPerSecond);
        return atMinRate > PieceReadTimeout ? atMinRate : PieceReadTimeout;
    }

    /// <summary>
    /// How long growing the staging file may take. The grow runs inside a database row lock, so an SDK retrying for minutes
    /// would otherwise hold a pooled connection that long. Settable for tests.
    /// </summary>
    internal TimeSpan GrowTimeout { get; set; } = DefaultGrowTimeout;

    public UploadSessionService(StorageAccessService access, IUploadSessionStore sessions, IStorageBackendResolver backends,
        AuditService audit, TimeProvider time, IConfiguration config, PieceMemoryBudget budget, ILogger<UploadSessionService> logger)
    {
        _access = access;
        _sessions = sessions;
        _backends = backends;
        _audit = audit;
        _time = time;
        _budget = budget;
        _logger = logger;
        _maxBytes = MaxUploadBytesFrom(config).Effective;
        _parallel = Math.Clamp(config.GetValue<int?>("Transfers:ParallelChunks") ?? DefaultParallelChunks, 1, 16);
        _lifetime = TimeSpan.FromHours(Math.Max(1, config.GetValue<int?>("Transfers:SessionHours") ?? DefaultSessionHours));
        _blobLock = TimeSpan.FromMinutes(Math.Max(1, config.GetValue<int?>("Transfers:BlobLockMinutes") ?? DefaultBlobLockMinutes));
        _maxOpenSessions = Math.Max(1, config.GetValue<int?>("Transfers:MaxOpenSessionsPerUser") ?? DefaultMaxOpenSessionsPerUser);
        _minChunkBytesPerSecond = MinChunkBytesPerSecondFrom(config);
    }

    /// <summary>Transfers:MinChunkBytesPerSecond, at least 1: the floor both Kestrel's body data rate and the piece read timeout use.</summary>
    public static int MinChunkBytesPerSecondFrom(IConfiguration config) =>
        Math.Max(1, config.GetValue<int?>("Transfers:MinChunkBytesPerSecond") ?? DefaultMinChunkBytesPerSecond);

    /// <summary>The configured upload limit and the one in force, clamped to the largest blob the portal can stage. Program warns once at startup when they differ.</summary>
    public static (long Effective, long Configured) MaxUploadBytesFrom(IConfiguration config)
    {
        var configured = Math.Max(0, config.GetValue<long?>("Transfers:MaxUploadBytes") ?? DefaultMaxUploadBytes);
        return (Math.Min(configured, ChunkMath.MaxBlobBytes), configured);
    }

    public static string TooLargeMessage(long maxBytes) =>
        $"File is larger than the {(maxBytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture)} GB limit.";

    public static string MissingPiecesMessage(int missing) =>
        $"Upload is missing {missing} {(missing == 1 ? "piece" : "pieces")}.";

    public static string StagingPathFor(string finalPath, Guid sessionId) =>
        StoragePath.Combine(StoragePath.Parent(finalPath), $".{StoragePath.Leaf(finalPath)}{StagingSuffix}{sessionId:N}");

    /// <summary>
    /// Whether <paramref name="session"/>'s staging path is safe to delete: for Files it must be exactly the name this
    /// service derives from the final path and session id (and so a hidden staging name); Blob never deletes anything.
    /// </summary>
    public static bool StagingIsOwnedBy(UploadSession session) =>
        session.TargetKind == StorageKind.BlobContainer
            ? session.StagingPath == session.FinalPath
            : session.StagingPath == StagingPathFor(session.FinalPath, session.Id)
              && StoragePaths.IsStagingName(StoragePath.Leaf(session.StagingPath));

    /// <summary>Cheap format checks a chunk PUT can fail before any database or storage call.</summary>
    public static AccessResult? CheckChunkFormat(int index, long? contentLength, string? contentMd5Base64)
    {
        if (index < 0)
            return new(AccessOutcome.Invalid, $"Piece {index} is out of range.");
        if (contentLength is not { } length || length < 0 || length > ChunkMath.MaxBlobPieceSize)
            return new(AccessOutcome.Invalid, "Each piece needs a Content-Length of at most one piece size.");
        if (!TryParseMd5(contentMd5Base64, out _))
            return new(AccessOutcome.Invalid, Md5RequiredMessage);
        return null;
    }

    public async Task<AccessResult<BeginUploadResponse>> BeginAsync(Guid userId, BeginUploadRequest req, AuditContext ctx,
        CancellationToken ct = default)
    {
        if (req.Size < 0)
            return new(AccessOutcome.Invalid, null, NegativeSizeMessage);
        if (req.Size > _maxBytes)
            return new(AccessOutcome.Invalid, null, TooLargeMessage(_maxBytes));
        if (req.FirstPieceMd5 is not null && !TryParseMd5(req.FirstPieceMd5, out _))
            return new(AccessOutcome.Invalid, null, FirstPieceMd5InvalidMessage);
        var firstPieceMd5 = req.FirstPieceMd5?.Trim();
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

        var now = _time.GetUtcNow();
        var resumable = await _sessions.FindResumableAsync(userId, target.Id, finalPath, req.Size, now, ct);
        // Same name and size is not enough to resume: a different file's first piece means a different file, so the
        // open session is replaced (below) rather than finished with a mix of two files' pieces.
        if (resumable is not null && !SameFirstPiece(resumable.FirstPieceMd5, firstPieceMd5))
            resumable = null;

        // Per-user caps, before any storage call: a session reserves its declared size until it settles, so one user
        // can never stage more than MaxUploadBytes (or hold more than MaxOpenSessionsPerUser uploads) at a time.
        // Resuming adds nothing, and an older session for the same file is about to be replaced, so neither counts.
        if (resumable is null)
        {
            var replaced = (await _sessions.ListOpenForUserPathAsync(userId, target.Id, finalPath, ct))
                .Where(o => o.ExpiresAt > now).ToList();
            var open = await _sessions.CountOpenForUserAsync(userId, now, ct) - replaced.Count;
            var reserved = await _sessions.SumOpenSizeForUserAsync(userId, now, ct) - replaced.Sum(o => o.Size);
            if (open >= _maxOpenSessions || reserved + req.Size > _maxBytes)
                return new(AccessOutcome.Busy, null, TooManyUploadsMessage, Code: UploadErrorCodes.Busy);
        }

        var backend = _backends.For(target);
        StorageItem? existing;
        try
        {
            // Storage creates missing folders implicitly, so a folder that doesn't exist yet is a new name.
            if (StoragePath.NonStrictFolderPrefix(folder) is { } newFolders
                && await backend.GetItemAsync(target, newFolders, ct) is not { IsFolder: true })
                return new(AccessOutcome.Invalid, null, StoragePath.InvisibleCharactersMessage);

            existing = await backend.GetItemAsync(target, finalPath, ct);
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
        var existed = existing is not null;

        // Blob blocks are staged on the final name, so two users' sessions there share one uncommitted block list and
        // whichever commits first discards the other's blocks. Only a recently active session holds the name: a stalled
        // one would otherwise lock the file for a day, and its owner gets a clean "restart" if they come back.
        if (target.Kind == StorageKind.BlobContainer
            && (await _sessions.ListActiveForPathAsync(target.Id, finalPath, now, ct))
                .Any(o => o.UserId != userId && HoldsBlobName(o, now)))
            return new(AccessOutcome.Conflict, null, OtherUserUploadingMessage, Code: UploadErrorCodes.InProgress);

        if (resumable is not null)
        {
            if (resumable.Overwrite != req.Overwrite || resumable.ExistedAtBegin != existed)
                await _sessions.SetOverwriteAsync(resumable.Id, req.Overwrite, existed, ct);
            var received = await _sessions.ReceivedChunksAsync(resumable.Id, ct);
            return new(AccessOutcome.Ok, Response(resumable.Id, req.Size, resumable.ChunkSize, received), null, auth.UserUpn);
        }

        // The same file at a different size, or with a different first piece, is a new upload of it: the older session
        // can never complete usefully.
        foreach (var older in await _sessions.ListOpenForUserPathAsync(userId, target.Id, finalPath, ct))
        {
            if (!await _sessions.TrySetStatusAsync(older.Id, UploadStatus.Open, UploadStatus.Aborted, ct: ct))
                continue;
            if (await RemoveStagingAsync(target, older))
                await _sessions.SetStagingRemovedAsync(older.Id, CancellationToken.None);
            await RecordAsync(userId, auth.UserUpn, target, Operation(older), older.FinalPath, 0, AuditResult.Failed, ReplacedMessage, ctx);
        }

        var id = Guid.NewGuid();
        var staging = target.Kind == StorageKind.FileShare ? StagingPathFor(finalPath, id) : finalPath;
        var pieceSize = ChunkMath.PieceSizeFor(target.Kind, req.Size);
        // The row is written first so the expiry sweep can always find (and remove) whatever storage was prepared.
        var session = new UploadSession(id, userId, target.Id, finalPath, staging, req.Size, pieceSize,
            req.Overwrite, now, now + _lifetime, UploadStatus.Open, TargetKind: target.Kind, ExistedAtBegin: existed,
            StagingRemoved: target.Kind == StorageKind.BlobContainer, FirstPieceMd5: firstPieceMd5);
        await _sessions.CreateAsync(session, ct);
        try
        {
            await backend.BeginUploadAsync(target, staging, req.Size, ct, id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The create may have landed server-side even though the call failed, so try to remove it; the cleanup
            // sweep retries for as long as StagingRemoved stays false.
            await _sessions.TrySetStatusAsync(id, UploadStatus.Open, UploadStatus.Aborted, ct: CancellationToken.None);
            if (await RemoveStagingAsync(target, session))
                await _sessions.SetStagingRemovedAsync(id, CancellationToken.None);
            if (ex is StorageFailureException failure)
            {
                LogStorageFailure(failure, "begin", target.Id, finalPath, ctx);
                return new(OutcomeOf(failure), null, failure.Message);
            }
            LogUnexpected(ex, "begin", target.Id, finalPath, ctx);
            return new(AccessOutcome.Unavailable, null, StorageErrorMapper.UnreachableMessage);
        }

        await _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), userId, auth.UserUpn ?? "", target.CustomerId,
            target.Id, BeginOperation, finalPath, req.Size, AuditResult.Success, req.Overwrite ? "overwrite" : "", ctx.SourceIp,
            ctx.CorrelationId), CancellationToken.None);
        return new(AccessOutcome.Ok, Response(id, req.Size, pieceSize, Array.Empty<int>()), null, auth.UserUpn);
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
        if (CheckChunkFormat(index, contentLength, contentMd5Base64) is { } malformed)
            return malformed;
        TryParseMd5(contentMd5Base64, out var md5);

        var session = await LoadAsync(userId, sessionId, ct);
        if (session is null)
            return new(AccessOutcome.NotFound, StorageErrorMapper.NotFoundMessage);

        var count = ChunkMath.ChunkCount(session.Size, session.ChunkSize);
        if (index >= count)
            return new(AccessOutcome.Invalid, $"Piece {index} is out of range; this upload has {count}.");
        var expected = ChunkMath.ExpectedLength(session.Size, session.ChunkSize, index);
        if (contentLength != expected)
            return new(AccessOutcome.Invalid, $"Piece {index} must be exactly {expected} bytes.");

        var auth = await _access.AuthorizeAsync(userId, session.TargetId, OperationOf(session), session.FinalPath, ctx, ct);
        if (auth.Outcome != AccessOutcome.Ok)
            return new(auth.Outcome, auth.Message);
        var target = auth.Value!;

        // The Azure SDKs need a seekable stream of known length and Kestrel's request body is neither, so the piece is
        // buffered (the session's piece size, at most MaxBlobPieceSize) and checked here before storage sees it. Storage
        // still verifies the same MD5 end to end. The byte budget is taken before the buffer exists and held until the
        // piece is written or has failed.
        if (!_budget.TryReserve(userId, expected, out var budgetLease))
            return new(AccessOutcome.Busy, WaitMessage, UploadErrorCodes.Wait);
        var length = (int)expected;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            var read = await ReadPieceAsync(body, buffer, length, target.Id, session.FinalPath, ctx, ct);
            if (read.Outcome != AccessOutcome.Ok)
                return read;
            if (!MD5.HashData(buffer.AsSpan(0, length)).AsSpan().SequenceEqual(md5))
                return new(AccessOutcome.Invalid, ChunkCorruptedMessage, UploadErrorCodes.Retry);

            var backend = _backends.For(target);
            var offset = ChunkMath.Offset(index, session.ChunkSize);
            // Files quota is charged on logical size, so the full size is reserved only when the first piece arrives
            // (bounded by the per-user byte cap). It is always grown to exactly session.Size, never offset + length: a
            // timed-out grow that Azure applies late then sets the same size again instead of truncating pieces another
            // chunk has written since (R34).
            if (target.Kind == StorageKind.FileShare && session.StagingSize < session.Size)
            {
                using var growTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                growTimeout.CancelAfter(GrowTimeout);
                try
                {
                    await _sessions.EnsureStagingSizeAsync(session.Id, session.Size,
                        token => backend.GrowStagingAsync(target, session.StagingPath, session.Size, token), growTimeout.Token);
                }
                catch (Exception ex) when (growTimeout.IsCancellationRequested && !ct.IsCancellationRequested
                                           && ex is OperationCanceledException or StorageFailureException)
                {
                    _logger.LogWarning("Growing staging for upload {SessionId} timed out after {Timeout} (correlation {CorrelationId}).",
                        session.Id, GrowTimeout, ctx.CorrelationId);
                    return new(AccessOutcome.Invalid, StorageSlowMessage, UploadErrorCodes.Retry);
                }
            }

            using var piece = new MemoryStream(buffer, 0, length, writable: false);
            await backend.WriteChunkAsync(target, session.StagingPath, session.FinalPath,
                ChunkMath.BlockId(session.Id, index), offset, piece, expected, md5, ct);
        }
        catch (StorageFailureException failure)
        {
            LogStorageFailure(failure, "chunk", target.Id, session.FinalPath, ctx);
            // The staging file (or the session's blocks) is gone or truncated; no retry of this piece can succeed. The
            // session is ended so the client's next Begin starts afresh instead of resuming into the same failure.
            if (failure.Kind == StorageFailureKind.NotFound
                || (failure.Kind == StorageFailureKind.Conflict && failure.Message == StorageErrorMapper.StagingIncompleteMessage))
            {
                await EndInterruptedAsync(userId, auth.UserUpn, target, session, ctx);
                return new(AccessOutcome.Conflict, failure.Kind == StorageFailureKind.NotFound ? UploadLostMessage : failure.Message,
                    UploadErrorCodes.Restart);
            }
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
            budgetLease.Dispose();
        }

        await _sessions.MarkChunkAsync(session.Id, index, _time.GetUtcNow(), ct);
        return new(AccessOutcome.Ok);
    }

    /// <summary>Reads exactly <paramref name="length"/> bytes within <see cref="PieceReadTimeoutFor"/> and confirms nothing follows.</summary>
    private async Task<AccessResult> ReadPieceAsync(Stream body, byte[] buffer, int length, Guid targetId, string path,
        AuditContext ctx, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PieceReadTimeoutFor(length));
        try
        {
            var total = 0;
            while (total < length)
            {
                var n = await body.ReadAsync(buffer.AsMemory(total, length - total), timeout.Token);
                if (n == 0)
                    return new(AccessOutcome.Invalid, ShortChunkMessage);
                total += n;
            }
            var probe = new byte[1];
            if (await body.ReadAsync(probe, timeout.Token) > 0)
                return new(AccessOutcome.Invalid, LongChunkMessage);
            return new(AccessOutcome.Ok);
        }
        catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // A dropped or stalled connection, or a malformed body (BadHttpRequestException is an IOException), is the
            // client's problem, not storage's: no error-level noise, and the client simply retries the piece.
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

        var count = ChunkMath.ChunkCount(session.Size, session.ChunkSize);
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
                session.Overwrite, CancellationToken.None, session.Id, session.Size);
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

        var operation = Operation(session);
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
                if (await RemoveStagingAsync(target, session))
                    await _sessions.SetStagingRemovedAsync(session.Id, CancellationToken.None);
                await RecordAsync(userId, auth.UserUpn, target, operation, session.FinalPath, 0, AuditResult.Failed, failureMessage, ctx);
                return new(AccessOutcome.Conflict, failureMessage, UploadErrorCodes.Restart);
            }
            // Back to Open so the user can retry, or resume with overwrite after a name clash.
            await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Completing, UploadStatus.Open, ct: CancellationToken.None);
            await RecordAsync(userId, auth.UserUpn, target, operation, session.FinalPath, 0, AuditResult.Failed, failureMessage, ctx);
            return new(outcome, failureMessage);
        }

        // Conditional so that if the cleanup sweep already settled a stuck commit, the Success row isn't written twice.
        if (await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Completing, UploadStatus.Completed, ct: CancellationToken.None))
        {
            await _sessions.SetStagingRemovedAsync(session.Id, CancellationToken.None);
            await RecordAsync(userId, auth.UserUpn, target, operation, session.FinalPath, session.Size, AuditResult.Success, "", ctx);
        }
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
            if (!StagingIsOwnedBy(session) || session.TargetKind != target.Kind)
                throw new StorageFailureException(StorageFailureKind.Other, StorageErrorMapper.OtherMessage);
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

        await _sessions.SetStagingRemovedAsync(session.Id, CancellationToken.None);
        await RecordAsync(userId, auth.UserUpn, target, Operation(session), session.FinalPath, 0, AuditResult.Failed,
            CancelledByUserMessage, ctx);
        return new(AccessOutcome.Ok);
    }

    /// <summary>Aborts a session whose staged data can no longer be completed; a session another call already ended is left alone.</summary>
    private async Task EndInterruptedAsync(Guid userId, string? upn, StorageTarget target, UploadSession session, AuditContext ctx)
    {
        if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Open, UploadStatus.Aborted, ct: CancellationToken.None))
            return;
        if (await RemoveStagingAsync(target, session))
            await _sessions.SetStagingRemovedAsync(session.Id, CancellationToken.None);
        await RecordAsync(userId, upn, target, Operation(session), session.FinalPath, 0, AuditResult.Failed, InterruptedMessage, ctx);
    }

    /// <summary>
    /// Best-effort removal of a session's staged data; true once nothing is left. Refuses (and logs an error) when the
    /// staging path isn't this session's own derived name, or the target has changed kind since Begin.
    /// </summary>
    private async Task<bool> RemoveStagingAsync(StorageTarget target, UploadSession session)
    {
        if (session.TargetKind == StorageKind.BlobContainer && target.Kind == StorageKind.BlobContainer)
            return true;
        if (!StagingIsOwnedBy(session) || session.TargetKind != target.Kind)
        {
            _logger.LogError("Refusing to remove staging {StagingPath} for upload {SessionId}: it isn't this session's staging name, " +
                             "or target {TargetId} changed kind since the upload began.", session.StagingPath, session.Id, target.Id);
            await _sessions.SetCleanupRefusedAsync(session.Id, CancellationToken.None);
            return false;
        }
        try
        {
            await _backends.For(target).AbortUploadAsync(target, session.StagingPath, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove staged data for upload {SessionId} on target {TargetId}.", session.Id, target.Id);
            return false;
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

    private BeginUploadResponse Response(Guid id, long size, int pieceSize, IReadOnlyList<int> received) =>
        new(id, pieceSize, ChunkMath.ChunkCount(size, pieceSize), ParallelFor(pieceSize), received);

    // Never fewer than two. Not Math.Clamp: it throws when ParallelChunks is configured as 1, which must stay the ceiling.
    private int ParallelFor(int pieceSize) =>
        pieceSize == ChunkMath.MinPieceSize ? _parallel : Math.Min(_parallel, Math.Max(2, InFlightTargetBytes / pieceSize));

    private bool HoldsBlobName(UploadSession s, DateTimeOffset now) =>
        s.Status == UploadStatus.Completing || now - s.CreatedAt < _blobLock
        || (s.LastActivityAt is { } active && now - active < _blobLock);

    private static StorageOperation OperationOf(UploadSession s) => s.Overwrite ? StorageOperation.Overwrite : StorageOperation.Upload;

    /// <summary>The audit operation: Overwrite when the upload replaces a file that existed at Begin, otherwise Upload.</summary>
    public static string Operation(UploadSession s) =>
        (s.Overwrite && s.ExistedAtBegin ? StorageOperation.Overwrite : StorageOperation.Upload).ToString();

    private static bool StagingFits(string finalPath)
    {
        var leafLength = 1 + StoragePath.Leaf(finalPath).Length + StagingSuffix.Length + 32;
        return leafLength <= MaxSegmentLength
               && StoragePath.TryNormalizePath(StagingPathFor(finalPath, Guid.Empty), out _, out _);
    }

    /// <summary>
    /// Whether a stored and a declared first-piece hash allow a resume. Only two known, different hashes refuse it: a
    /// session or client without one (an older client, or an empty file) resumes on name and size as before.
    /// </summary>
    internal static bool SameFirstPiece(string? stored, string? declared) =>
        stored is null || declared is null || string.Equals(stored, declared, StringComparison.Ordinal);

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
    private Task RecordAsync(Guid userId, string? upn, StorageTarget target, string operation, string path, long bytes,
        AuditResult result, string error, AuditContext ctx) =>
        _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), userId, upn ?? "", target.CustomerId, target.Id,
            operation, path, bytes, result, error, ctx.SourceIp, ctx.CorrelationId), CancellationToken.None);
}
