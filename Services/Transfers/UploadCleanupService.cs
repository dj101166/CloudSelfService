using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Storage;

namespace CloudSelfService.Services.Transfers;

/// <summary>
/// Every 30 minutes: aborts expired Open uploads (removing their staged data), settles commits that never finished,
/// and deletes session rows a week after they expired. Sessions are claimed through conditional status changes, so
/// an upload a user is completing right now is never touched.
/// </summary>
public sealed class UploadCleanupService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RowRetention = TimeSpan.FromDays(7);
    public const int DefaultCompletingTimeoutMinutes = 30;
    public const string InterruptedCommitMessage = "The upload was interrupted while it was being finished.";
    private static readonly string Operation = StorageOperation.Upload.ToString();

    private readonly IUploadSessionStore _sessions;
    private readonly ITargetStore _targets;
    private readonly IUserStore _users;
    private readonly IStorageBackendResolver _backends;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadCleanupService> _logger;
    private readonly TimeSpan _completingTimeout;

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
        _completingTimeout = TimeSpan.FromMinutes(Math.Max(1,
            config.GetValue<int?>("Transfers:CompletingTimeoutMinutes") ?? DefaultCompletingTimeoutMinutes));
    }

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
        foreach (var session in await _sessions.ListExpiredOpenAsync(now, ct))
        {
            if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Open, UploadStatus.Aborted, ct: ct))
                continue;
            if (await _targets.GetAsync(session.TargetId, ct) is { } target)
                await AbortQuietlyAsync(target, session, ct);
        }

        foreach (var session in await _sessions.ListStuckCompletingAsync(now - _completingTimeout, ct))
            await SettleStuckAsync(session, ct);

        var purged = await _sessions.PurgeExpiredBeforeAsync(now - RowRetention, ct);
        if (purged > 0)
            _logger.LogInformation("Upload cleanup deleted {Count} session rows expired over {Days} days ago.", purged, RowRetention.TotalDays);
    }

    // The process died (or the status write failed) between claiming Completing and recording the result. If the final
    // file is there with the right size the commit landed; otherwise it did not, and the staged data is removed.
    private async Task SettleStuckAsync(UploadSession session, CancellationToken ct)
    {
        var target = await _targets.GetAsync(session.TargetId, ct);
        StorageItem? item = null;
        if (target is not null)
        {
            try
            {
                item = await _backends.For(target).GetItemAsync(target, session.FinalPath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Storage unreachable: leave it for the next sweep rather than guess.
                _logger.LogWarning(ex, "Could not check stuck upload {SessionId} on target {TargetId}; will retry.", session.Id, session.TargetId);
                return;
            }
        }

        var landed = item is { IsFolder: false } && item.Size == session.Size;
        if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Completing, landed ? UploadStatus.Completed : UploadStatus.Aborted, ct: ct))
            return;
        if (!landed && target is not null)
            await AbortQuietlyAsync(target, session, ct);

        var upn = (await _users.GetAsync(session.UserId, ct))?.Upn ?? "";
        await _audit.RecordAsync(new AuditEvent(Guid.NewGuid(), _time.GetUtcNow(), session.UserId, upn, target?.CustomerId,
            session.TargetId, Operation, session.FinalPath, landed ? session.Size : 0,
            landed ? AuditResult.Success : AuditResult.Failed, landed ? "" : InterruptedCommitMessage,
            "", $"upload-cleanup:{session.Id:N}"), CancellationToken.None);
        _logger.LogInformation("Settled stuck upload {SessionId} as {Status}.", session.Id, landed ? UploadStatus.Completed : UploadStatus.Aborted);
    }

    private async Task AbortQuietlyAsync(StorageTarget target, UploadSession session, CancellationToken ct)
    {
        try
        {
            await _backends.For(target).AbortUploadAsync(target, session.StagingPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not remove staged data for upload {SessionId} on target {TargetId}.",
                session.Id, session.TargetId);
        }
    }
}
