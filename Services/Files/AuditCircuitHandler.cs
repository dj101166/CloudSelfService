using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;

namespace CloudSelfService.Services.Files;

/// <summary>
/// Records the client address of the circuit's /_blazor connection into the circuit's <see cref="PageAuditContext"/>.
/// Scoped, so it shares the circuit's scope with the PageAuditContext the pages use. It runs on every connection-up,
/// including reconnects, so a client that changes address is audited with its current one.
/// </summary>
/// <remarks>
/// IHttpContextAccessor is null here (measured: under the app's LongPolling transport the hub loop outlives the request
/// that started it), so the address comes from <see cref="ConnectionIpHubFilter"/>, which reads it from the SignalR
/// connection itself and hands it down the same async flow that starts or reconnects the circuit.
/// </remarks>
public sealed class AuditCircuitHandler(PageAuditContext audit, IHttpContextAccessor accessor) : CircuitHandler
{
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        audit.CaptureConnection(ConnectionIpHubFilter.Current ?? accessor.HttpContext);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Makes the hub connection's HttpContext (as seen after UseForwardedHeaders, on the request that opened the connection)
/// available to the hub method's async flow, where <see cref="AuditCircuitHandler"/> reads it.
/// </summary>
public sealed class ConnectionIpHubFilter : IHubFilter
{
    private static readonly AsyncLocal<HttpContext?> Connection = new();

    /// <summary>The current hub invocation's connection HttpContext, or null outside one.</summary>
    public static HttpContext? Current => Connection.Value;

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var previous = Connection.Value;
        Connection.Value = invocationContext.Context.GetHttpContext();
        try
        {
            return await next(invocationContext);
        }
        finally
        {
            Connection.Value = previous;
        }
    }
}
