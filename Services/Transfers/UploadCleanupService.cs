using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Storage;

namespace CloudSelfService.Services.Transfers;

/// <summary>
/// Every 30 minutes: aborts expired or idle Open uploads, settles commits that never finished, retries removing staged
/// data that an earlier abort could not delete, and deletes session rows a week after they expired once nothing is left
/// in storage. Sessions are claimed through conditional status changes, so an upload a user is completing right now is
/// never touched.
/// </summary>
public sealed class UploadCleanupService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RowRetention = TimeSpan.FromDays(7);
    /// <summary>A row whose staging could never be removed is kept this long, then dropped with an error naming the path.</summary>
    public static readonly TimeSpan AbandonedRetention = TimeSpan.FromDays(30);
    public const int DefaultCompletingTimeoutMinutes = 30;
    /// <summary>A commit of the largest file finishes well inside this, so a live Complete is never settled underneath.</summary>
    public const int MinCompletingTimeoutMinutes = 10;
    public const int DefaultIdleSessionMinutes = 120;
    public const string InterruptedCommitMessage = "The upload was interrupted while it was being finished.";
    public const string ExpiredMessage = "Upload expired";

    private readonly IUploadSessionStore _sessions;
    private readonly ITargetStore _targets;
    private readonly IUserStore _users;
    private readonly IStorageBackendResolver _backends;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadCleanupService> _logger;
    private readonly TimeSpan _completingTimeout;
    private readonly TimeSpan _idle;

    public UploadCleanupService(IUploadSessionStore sessions, ITargetStore targets, IUserStore users, IStorageBackendResolver backends,
        AuditService audit, TimeProvider time, IConfiguration config, ILogger<UploadCleanupService> logger)
    {
        _sessions = sessions;
        _targets = targets;
        _users = users;
        _backends = backends;
        _audit = audit;
        _time = time;
        _logger = logger;
        _completingTimeout = TimeSpan.FromMinutes(Math.Max(MinCompletingTimeoutMinutes,
            config.GetValue<int?>("Transfers:CompletingTimeoutMinutes") ?? DefaultCompletingTimeoutMinutes));
        _idle = IdleFrom(config);
    }

    /// <summary>Transfers:IdleSessionMinutes, at least 1: how long a session no piece has reached is kept before the sweep aborts it.</summary>
    public static TimeSpan IdleFrom(IConfiguration config) =>
        TimeSpan.FromMinutes(Math.Max(1, config.GetValue<int?>("Transfers:IdleSessionMinutes") ?? DefaultIdleSessionMinutes));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            try
            {
                await SweepAsync(_time.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload cleanup sweep failed; retrying in {Interval}.", Interval);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var session in await _sessions.ListSweepableOpenAsync(now, now - _idle, ct))
        {
            if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Open, UploadStatus.Aborted, ct: ct,
                    requireClaim: true, expectedClaim: session.ClaimedAt))
                continue;
            var target = await _targets.GetAsync(session.TargetId, ct);
            await RemoveStagingAsync(target, session, ct);
            await RecordAsync(session, target, 0, AuditResult.Failed, ExpiredMessage, ct);
        }

        foreach (var session in await _sessions.ListStuckCompletingAsync(now - _completingTimeout, ct))
            await SettleStuckAsync(session, ct);

        // Aborts above, in Complete or in Begin may have failed to delete; keep trying until storage confirms.
        foreach (var session in await _sessions.ListAbortedWithStagingAsync(ct))
            await RemoveStagingAsync(await _targets.GetAsync(session.TargetId, ct), session, ct);

        var (purged, abandoned) = await _sessions.PurgeAsync(now - RowRetention, now - AbandonedRetention, ct);
        foreach (var a in abandoned)
            _logger.LogError("Upload {SessionId} on target {TargetId} was purged after {Days} days but its staging file {StagingPath} " +
                             "could never be removed; delete it by hand.", a.SessionId, a.TargetId, AbandonedRetention.TotalDays, a.StagingPath);
        if (purged > 0)
            _logger.LogInformation("Upload cleanup deleted {Count} settled session rows.", purged);
    }

    // The process died (or the status write failed) between claiming Completing and recording the result. If the
    // commit landed (asked definitively, see IStorageBackend.HasLandedAsync) it is Completed; otherwise the staged data
    // is removed.
    private async Task SettleStuckAsync(UploadSession session, CancellationToken ct)
    {
        var target = await _targets.GetAsync(session.TargetId, ct);
        var landed = false;
        if (target is not null && target.Kind == session.TargetKind)
        {
            try
            {
                landed = await _backends.For(target).HasLandedAsync(target, session.StagingPath, session.FinalPath, session.Id,
                    session.Size, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Storage unreachable: leave it for the next sweep rather than guess.
                _logger.LogWarning(ex, "Could not check stuck upload {SessionId} on target {TargetId}; will retry.", session.Id, session.TargetId);
                return;
            }
        }

        // Requiring the listed claim time means a session re-claimed by a retried Complete since it was listed is left alone.
        if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Completing, landed ? UploadStatus.Completed : UploadStatus.Aborted,
                ct: ct, requireClaim: true, expectedClaim: session.ClaimedAt))
            return;
        if (landed)
            await _sessions.SetStagingRemovedAsync(session.Id, ct);
        else
            await RemoveStagingAsync(target, session, ct);

        await RecordAsync(session, target, landed ? session.Size : 0, landed ? AuditResult.Success : AuditResult.Failed,
            landed ? "" : InterruptedCommitMessage, ct);
        _logger.LogInformation("Settled stuck upload {SessionId} as {Status}.", session.Id, landed ? UploadStatus.Completed : UploadStatus.Aborted);
    }

    // Deletes only the session's own derived staging name, and only when the target is still the kind it was at Begin.
    // A refusal is logged once and recorded on the row, so later sweeps don't retry (or re-log) it.
    private async Task RemoveStagingAsync(StorageTarget? target, UploadSession session, CancellationToken ct)
    {
        if (session.StagingRemoved || session.CleanupRefused) return;
        if (session.TargetKind == StorageKind.BlobContainer && target is null or { Kind: StorageKind.BlobContainer })
        {
            await _sessions.SetStagingRemovedAsync(session.Id, ct);
            return;
        }
        if (target is null)
        {
            _logger.LogError("Upload {SessionId} staging {StagingPath} cannot be removed: target {TargetId} no longer exists.",
                session.Id, session.StagingPath, session.TargetId);
            await _sessions.SetCleanupRefusedAsync(session.Id, ct);
            return;
        }
        if (!UploadSessionService.StagingIsOwnedBy(session) || session.TargetKind != target.Kind)
        {
            _logger.LogError("Refusing to remove staging {StagingPath} for upload {SessionId}: it isn't this session's staging name, " +
                             "or target {TargetId} changed kind since the upload began.", session.StagingPath, session.Id, target.Id);
            await _sessions.SetCleanupRefusedAsync(session.Id, ct);
            return;
        }
        try
        {
            await _backends.For(target).AbortUploadAsync(target, session.StagingPath, ct);
            await _sessions.SetStagingRemovedAsync(session.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not remove staged data for upload {SessionId} on target {TargetId}; will retry.",
                session.Id, session.TargetId);
        }
    }

    private async Task RecordAsync(UploadSession session, StorageTarget? target, long bytes, AuditResult result, string error,
        CancellationToken ct)
    {
        var upn = (await _users.GetAsync(session.UserId, ct))?.Upn ?? "";
        await _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), session.UserId, upn, target?.CustomerId,
            session.TargetId, UploadSessionService.Operation(session), session.FinalPath, bytes, result, error,
            "", $"upload-cleanup:{session.Id:N}"), CancellationToken.None);
    }
}
