using CloudSelfService.Models;
using Microsoft.AspNetCore.Components;

namespace CloudSelfService.Services.Files;

/// <summary>
/// The <see cref="AuditContext"/> for actions taken inside a Blazor circuit, which has no request of its own.
/// </summary>
/// <remarks>
/// The circuit's DI scope never sees the page request's HttpContext, so App.razor records the client IP here during
/// the prerender, and the value crosses into the circuit as persisted component state (registered with
/// RegisterPersistentService in Program.cs). Blazor Server data-protects that state, so a client cannot forge it.
/// A circuit that started without a prerender has no value and audits "unknown".
/// </remarks>
public sealed class PageAuditContext
{
    public const string UnknownSourceIp = "unknown";

    [PersistentState]
    public string? SourceIp { get; set; }

    /// <summary>Called by App.razor during the page request.</summary>
    public void Capture(HttpContext? http)
    {
        if (http?.Connection.RemoteIpAddress is { } ip)
            this.SourceIp = ip.ToString();
    }

    /// <summary>A context for one action: the circuit's client IP and a fresh correlation id.</summary>
    public AuditContext Next() =>
        new(string.IsNullOrWhiteSpace(this.SourceIp) ? UnknownSourceIp : this.SourceIp, Guid.NewGuid().ToString("N"));
}
