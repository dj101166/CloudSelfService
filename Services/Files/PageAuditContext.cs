using System.Diagnostics;
using CloudSelfService.Models;

namespace CloudSelfService.Services.Files;

/// <summary>
/// The <see cref="AuditContext"/> for actions taken inside a Blazor circuit, which has no request of its own. The
/// source IP is read from whichever HTTP context the circuit's scope first sees (the prerender request, or the
/// SignalR connection) and kept for the circuit; each action gets its own correlation id.
/// </summary>
public sealed class PageAuditContext(IHttpContextAccessor accessor)
{
    private string? _sourceIp;

    public AuditContext Next()
    {
        _sourceIp ??= accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
        return new AuditContext(_sourceIp ?? "", Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"));
    }
}
