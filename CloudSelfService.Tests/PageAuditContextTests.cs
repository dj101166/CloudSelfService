using System.Net;
using CloudSelfService.Services.Files;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CloudSelfService.Tests;

public class PageAuditContextTests
{
    [Fact]
    public void Next_PersistedIpPresent_UsesIt()
    {
        var audit = new PageAuditContext { SourceIp = "203.0.113.7" };

        Assert.Equal("203.0.113.7", audit.Next().SourceIp);
    }

    [Fact]
    public void Next_NoPersistedIp_SaysUnknown()
    {
        Assert.Equal(PageAuditContext.UnknownSourceIp, new PageAuditContext().Next().SourceIp);
        Assert.Equal(PageAuditContext.UnknownSourceIp, new PageAuditContext { SourceIp = " " }.Next().SourceIp);
    }

    [Fact]
    public void Next_EachActionHasItsOwnCorrelationId()
    {
        var audit = new PageAuditContext { SourceIp = "203.0.113.7" };

        var a = audit.Next().CorrelationId;
        var b = audit.Next().CorrelationId;

        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]{32}$", a);
    }

    [Fact]
    public void Capture_ReadsTheRequestRemoteAddress()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        var audit = new PageAuditContext();

        audit.Capture(http);

        Assert.Equal("198.51.100.4", audit.SourceIp);
    }

    [Fact]
    public void Capture_NoContextOrAddress_LeavesItUnset()
    {
        var audit = new PageAuditContext();
        audit.Capture(null);
        audit.Capture(new DefaultHttpContext());

        Assert.Null(audit.SourceIp);
    }

    private static HttpContext From(string ip)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return http;
    }

    [Fact]
    public void Next_ConnectionIpBeatsPersistedIp()
    {
        var audit = new PageAuditContext { SourceIp = "203.0.113.7" };
        audit.CaptureConnection(From("198.51.100.9"));

        Assert.Equal("198.51.100.9", audit.Next().SourceIp);
    }

    [Fact]
    public void Next_PersistedStateDropped_ConnectionIpStillUsed()
    {
        var audit = new PageAuditContext();
        audit.CaptureConnection(From("198.51.100.9"));

        Assert.Equal("198.51.100.9", audit.Next().SourceIp);
    }

    [Fact]
    public void CaptureConnection_Reconnect_UpdatesToLatestAddress()
    {
        var audit = new PageAuditContext { SourceIp = "203.0.113.7" };
        audit.CaptureConnection(From("198.51.100.9"));
        audit.CaptureConnection(From("192.0.2.33"));

        Assert.Equal("192.0.2.33", audit.Next().SourceIp);
    }

    [Fact]
    public void CaptureConnection_NoContext_KeepsPreviousAndFallsBack()
    {
        var audit = new PageAuditContext { SourceIp = "203.0.113.7" };
        audit.CaptureConnection(null);
        Assert.Equal("203.0.113.7", audit.Next().SourceIp);

        audit.CaptureConnection(From("198.51.100.9"));
        audit.CaptureConnection(new DefaultHttpContext());
        Assert.Equal("198.51.100.9", audit.Next().SourceIp);
    }

    [Fact]
    public async Task CircuitHandler_OnConnectionUp_RecordsTheHubRequestAddress()
    {
        var audit = new PageAuditContext { SourceIp = "203.0.113.7" };
        var accessor = new HttpContextAccessor { HttpContext = From("198.51.100.9") };
        var handler = new AuditCircuitHandler(audit, accessor);

        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        Assert.Equal("198.51.100.9", audit.Next().SourceIp);
    }

    [Fact]
    public async Task HubFilter_ExposesConnectionContextOnlyDuringTheInvocation()
    {
        var http = From("198.51.100.9");
        var context = new FakeCallerContext(http);
        var invocation = new Microsoft.AspNetCore.SignalR.HubInvocationContext(context, null!, null!,
            typeof(object).GetMethod(nameof(ToString))!, Array.Empty<object?>());
        HttpContext? seen = null;

        await new ConnectionIpHubFilter().InvokeMethodAsync(invocation, _ =>
        {
            seen = ConnectionIpHubFilter.Current;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.Same(http, seen);
        Assert.Null(ConnectionIpHubFilter.Current);
    }

    [Fact]
    public async Task CircuitHandler_PrefersHubConnectionOverAccessor()
    {
        var audit = new PageAuditContext();
        var handler = new AuditCircuitHandler(audit, new HttpContextAccessor { HttpContext = From("203.0.113.1") });
        var invocation = new Microsoft.AspNetCore.SignalR.HubInvocationContext(new FakeCallerContext(From("198.51.100.9")), null!, null!,
            typeof(object).GetMethod(nameof(ToString))!, Array.Empty<object?>());

        await new ConnectionIpHubFilter().InvokeMethodAsync(invocation, async _ =>
        {
            await handler.OnConnectionUpAsync(null!, CancellationToken.None);
            return null;
        });

        Assert.Equal("198.51.100.9", audit.Next().SourceIp);
    }

    private sealed class FakeCallerContext(HttpContext http) : Microsoft.AspNetCore.SignalR.HubCallerContext
    {
        private readonly Microsoft.AspNetCore.Http.Features.FeatureCollection _features = BuildFeatures(http);

        private static Microsoft.AspNetCore.Http.Features.FeatureCollection BuildFeatures(HttpContext http)
        {
            var f = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
            f.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeature { HttpContext = http });
            return f;
        }

        public override string ConnectionId => "c1";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class HttpContextFeature : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    [Fact]
    public void SourceIp_IsPersistedComponentState()
    {
        var property = typeof(PageAuditContext).GetProperty(nameof(PageAuditContext.SourceIp))!;
        Assert.Single(property.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.PersistentStateAttribute), false));
    }

    [Fact]
    public void Program_RegistersPageAuditContextForPersistence()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        var program = File.ReadAllText(Path.Combine(dir!.FullName, "Program.cs"));
        var app = File.ReadAllText(Path.Combine(dir.FullName, "App.razor"));

        Assert.Contains("RegisterPersistentService<CloudSelfService.Services.Files.PageAuditContext>", program);
        Assert.Contains("PageAudit.Capture(this.HttpContext)", app);
        Assert.Contains("CircuitHandler, CloudSelfService.Services.Files.AuditCircuitHandler>", program);
        Assert.Contains("AddHttpContextAccessor()", program);
        Assert.Contains("AddFilter<CloudSelfService.Services.Files.ConnectionIpHubFilter>()", program);
    }
}
