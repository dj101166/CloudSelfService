using System.Net;
using CloudSelfService.Endpoints;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSelfService.Tests;

public sealed class AdminEndpointTests : IDisposable
{
    private readonly TestAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task SeedAsync(string user, string path)
    {
        await _factory.Services.GetRequiredService<IAuditStore>().AppendAsync(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow,
            null, user, null, null, "Download", path, 5, AuditResult.Success, "", "1.1.1.1", "c"));
    }

    [Fact]
    public async Task AuditCsv_Owner_ReturnsAttachmentWithSafeCells()
    {
        var client = await _factory.CreateClientAs("staff-owner");
        await SeedAsync("=cmd|calc", "+1,2");

        var response = await client.GetAsync("/api/admin/audit.csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.ToString());
        Assert.Matches(@"^attachment; filename=""audit-\d{8}-\d{4}\.csv""$", response.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("'=cmd|calc", body);
        Assert.Contains("\"'+1,2\"", body);
        Assert.DoesNotContain(",=cmd", body);
    }

    [Fact]
    public async Task AuditCsv_Export_IsAudited_WithFilterAndRowCount()
    {
        var client = await _factory.CreateClientAs("staff-owner");
        await SeedAsync("someone@x.example", "keep.txt");

        await client.GetAsync("/api/admin/audit.csv?operation=Download");

        var rows = await _factory.Services.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Operation: "Admin.ExportAudit"));
        var row = Assert.Single(rows);
        Assert.Equal("operation=Download", row.Path);
        Assert.Equal(1, row.Bytes);
    }

    [Fact]
    public async Task AuditCsv_FiltersApply()
    {
        var client = await _factory.CreateClientAs("staff-owner");
        await SeedAsync("someone@x.example", "keep.txt");

        var none = await (await client.GetAsync("/api/admin/audit.csv?operation=Upload")).Content.ReadAsStringAsync();
        var some = await (await client.GetAsync("/api/admin/audit.csv?operation=Download")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("keep.txt", none);
        Assert.Contains("keep.txt", some);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task AuditCsv_CrossSiteFetch_403(string site)
    {
        var client = await _factory.CreateClientAs("staff-owner");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/audit.csv");
        request.Headers.Add("Sec-Fetch-Site", site);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("result=bogus")]
    [InlineData("from=11/01/2026")]
    public async Task AuditCsv_MalformedFilter_400(string query)
    {
        var client = await _factory.CreateClientAs("staff-owner");

        var response = await client.GetAsync("/api/admin/audit.csv?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("customer-full")]
    [InlineData("staff-none")]
    public async Task AuditCsv_BelowStaffAdmin_NotServed(string persona)
    {
        var client = await _factory.CreateClientAs(persona);

        var response = await client.GetAsync("/api/admin/audit.csv");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuditCsv_Anonymous_NotServed()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/admin/audit.csv");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void FileNameFor_UsesUtcMinutePrecision() =>
        Assert.Equal("audit-20261120-0905.csv", AdminEndpoints.FileNameFor(new DateTimeOffset(2026, 11, 20, 9, 5, 59, TimeSpan.Zero)));
}
