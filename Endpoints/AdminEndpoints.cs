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
        app.MapGet("/api/admin/audit.csv", AuditCsvAsync).RequireAuthorization(Policies.StaffAdmin)
            .RequireRateLimiting(RateLimits.AuditExport);

    public static string FilterSummary(Guid? user, Guid? customer, Guid? target, string? operation, string? result, string? from, string? to)
    {
        var parts = new List<string>();
        if (user is not null) parts.Add($"user={user}");
        if (customer is not null) parts.Add($"customer={customer}");
        if (target is not null) parts.Add($"target={target}");
        if (!string.IsNullOrWhiteSpace(operation)) parts.Add($"operation={operation.Trim()}");
        if (!string.IsNullOrWhiteSpace(result)) parts.Add($"result={result.Trim()}");
        if (!string.IsNullOrWhiteSpace(from)) parts.Add($"from={from.Trim()}");
        if (!string.IsNullOrWhiteSpace(to)) parts.Add($"to={to.Trim()}");
        var text = parts.Count == 0 ? "(no filters)" : string.Join(' ', parts);
        return text.Length > 500 ? text[..500] : text;
    }

    public static string FileNameFor(DateTimeOffset now, bool truncated = false) =>
        $"audit-{now.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}{(truncated ? "-truncated" : "")}.csv";

    private static async Task<IResult> AuditCsvAsync(HttpContext ctx, IAuditStore audit, ICustomerStore customers,
        ITargetStore targets, IUserStore users, AdminService admin, TimeProvider time, ILoggerFactory loggers,
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

        if (AppClaims.UserIdOf(ctx.User) is not { } actorId)
            return Results.Unauthorized();

        string csv;
        var truncated = false;
        try
        {
            var actor = await users.GetAsync(actorId, ctx.RequestAborted);
            var events = await audit.QueryAsync(query, ctx.RequestAborted);
            var customerNames = (await customers.ListAsync(ctx.RequestAborted)).ToDictionary(c => c.Id, c => c.Name);
            var targetNames = (await targets.ListAsync(null, ctx.RequestAborted)).ToDictionary(t => t.Id, t => t.DisplayName);
            truncated = events.Count >= AuditFilters.ExportLimit;
            csv = CsvCell.AuditCsv(events, customerNames, targetNames, truncated ? AuditFilters.ExportLimit : null);
            await admin.RecordExportAsync(actorId, actor?.Upn ?? "", FilterSummary(user, customer, target, operation, result, from, to),
                events.Count, AuthEndpoints.AuditContextFor(ctx));
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
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{FileNameFor(time.GetUtcNow(), truncated)}\"";
        return Results.Text(csv, "text/csv; charset=utf-8");
    }
}
