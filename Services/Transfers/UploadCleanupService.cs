using CloudSelfService.Services.Data;
using CloudSelfService.Services.Storage;

namespace CloudSelfService.Services.Transfers;

/// <summary>
/// Every 30 minutes: aborts expired Open uploads (removing their staged data) and deletes session rows a week
/// after they expired. Only Open sessions are claimed, through a conditional status change, so an upload a user
/// is completing right now is never touched.
/// </summary>
public sealed class UploadCleanupService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RowRetention = TimeSpan.FromDays(7);

    private readonly IUploadSessionStore _sessions;
    private readonly ITargetStore _targets;
    private readonly IStorageBackendResolver _backends;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadCleanupService> _logger;

    public UploadCleanupService(IUploadSessionStore sessions, ITargetStore targets, IStorageBackendResolver backends,
        TimeProvider time, ILogger<UploadCleanupService> logger)
    {
        _sessions = sessions;
        _targets = targets;
        _backends = backends;
        _time = time;
        _logger = logger;
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
            if (!await _sessions.TrySetStatusAsync(session.Id, UploadStatus.Open, UploadStatus.Aborted, ct))
                continue;
            try
            {
                if (await _targets.GetAsync(session.TargetId, ct) is { } target)
                    await _backends.For(target).AbortUploadAsync(target, session.StagingPath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not remove staged data for expired upload {SessionId} on target {TargetId}.",
                    session.Id, session.TargetId);
            }
        }

        var purged = await _sessions.PurgeExpiredBeforeAsync(now - RowRetention, ct);
        if (purged > 0)
            _logger.LogInformation("Upload cleanup deleted {Count} session rows expired over {Days} days ago.", purged, RowRetention.TotalDays);
    }
}
