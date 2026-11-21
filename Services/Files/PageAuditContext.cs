using CloudSelfService.Models;
using Microsoft.AspNetCore.Components;

namespace CloudSelfService.Services.Files;

/// <summary>
/// The <see cref="AuditContext"/> for actions taken inside a Blazor circuit, which has no request of its own.
/// </summary>
/// <remarks>
/// Two sources, in order of trust:
/// <list type="number">
/// <item><description><see cref="ConnectionIp"/>, set by <see cref="AuditCircuitHandler"/> from the circuit's own
/// /_blazor connection request every time the connection comes up (so a reconnect from a new address updates it). That
/// is a real request seen after UseForwardedHeaders, so it is as good as any endpoint's address.</description></item>
/// <item><description><see cref="SourceIp"/>, captured by App.razor during the prerender and carried into the circuit
/// as persisted component state. Blazor data-protects that state, so a client cannot forge a value, but it can drop
/// the state (giving "unknown") or replay an older page's state captured from another address. It is only a fallback
/// for the moment before the connection handler runs.</description></item>
/// </list>
/// With neither, the audit row says "unknown".
/// </remarks>
public sealed class PageAuditContext
{
    public const string UnknownSourceIp = "unknown";

    /// <summary>Prerender-time address; fallback only (see remarks).</summary>
    [PersistentState]
    public string? SourceIp { get; set; }

    /// <summary>The live circuit connection's address; authoritative when present.</summary>
    public string? ConnectionIp { get; private set; }

    /// <summary>Called by App.razor during the page request.</summary>
    public void Capture(HttpContext? http)
    {
        if (http?.Connection.RemoteIpAddress is { } ip)
            this.SourceIp = ip.ToString();
    }

    /// <summary>Called by <see cref="AuditCircuitHandler"/> whenever the circuit's connection comes up.</summary>
    public void CaptureConnection(HttpContext? http)
    {
        if (http?.Connection.RemoteIpAddress is { } ip)
            this.ConnectionIp = ip.ToString();
    }

    /// <summary>The address to audit: the connection's, else the prerender's, else "unknown".</summary>
    public string EffectiveSourceIp =>
        !string.IsNullOrWhiteSpace(this.ConnectionIp) ? this.ConnectionIp
        : !string.IsNullOrWhiteSpace(this.SourceIp) ? this.SourceIp
        : UnknownSourceIp;

    /// <summary>A context for one action: the circuit's client address and a fresh correlation id.</summary>
    public AuditContext Next() => new(this.EffectiveSourceIp, Guid.NewGuid().ToString("N"));
}
