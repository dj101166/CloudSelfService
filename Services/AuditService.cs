using CloudSelfService.Models;
using CloudSelfService.Services.Data;

namespace CloudSelfService.Services;

public sealed class AuditService
{
    private readonly IAuditStore _store;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IAuditStore store, ILogger<AuditService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task RecordAsync(AuditEvent e, CancellationToken ct = default)
    {
        try
        {
            await _store.AppendAsync(e, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit append failed for {Operation} {Result} (event {Id}).", e.Operation, e.Result, e.Id);
        }
    }
}
