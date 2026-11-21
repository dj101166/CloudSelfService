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
    }
}
