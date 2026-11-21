using System.Globalization;
using CloudSelfService.Authorization;
using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CloudSelfService.Endpoints;

public static class AdminEndpoints
{
    public const string CrossSiteMessage = "Cross-site requests are not allowed.";
    public const string BadFilterMessage = "A filter value is not valid.";
    public const string UnavailableMessage = "The service is temporarily unavailable.";

    public static void MapAdminEndpoints(this WebApplication app) =>
        app.MapGet("/api/admin/audit.csv", AuditCsvAsync).RequireAuthorization(Policies.StaffAdmin);

    public static string FileNameFor(DateTimeOffset now) =>
        $"audit-{now.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.csv";

    private static async Task<IResult> AuditCsvAsync(HttpContext ctx, IAuditStore audit, ICustomerStore customers,
        ITargetStore targets, TimeProvider time, ILoggerFactory loggers,
        [FromQuery] Guid? user, [FromQuery] Guid? customer, [FromQuery] Guid? target, [FromQuery] string? operation,
        [FromQuery] string? result, [FromQuery] string? from, [FromQuery] string? to)
    {
        ctx.Response.Headers.CacheControl = "no-store";

        // An export is only ever started by the user's own click; a cross-site page embedding this URL is not that.
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site is "cross-site" or "same-site")
            return Results.Text(CrossSiteMessage, "text/plain; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);

        var query = AuditFilters.ToQuery(user, customer, target, operation, result, from, to, AuditFilters.ExportLimit);
        if (query is null)
            return Results.Text(BadFilterMessage, "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);

        string csv;
        try
        {
            var events = await audit.QueryAsync(query, ctx.RequestAborted);
            var customerNames = (await customers.ListAsync(ctx.RequestAborted)).ToDictionary(c => c.Id, c => c.Name);
            var targetNames = (await targets.ListAsync(null, ctx.RequestAborted)).ToDictionary(t => t.Id, t => t.DisplayName);
            csv = CsvCell.AuditCsv(events, customerNames, targetNames);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            loggers.CreateLogger("CloudSelfService.AdminExport").LogError(ex, "Audit CSV export failed.");
            return Results.Text(UnavailableMessage, "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{FileNameFor(time.GetUtcNow())}\"";
        return Results.Text(csv, "text/csv; charset=utf-8");
    }
}
