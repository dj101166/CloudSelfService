using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Storage;
using CloudSelfService.Services.Transfers;
using Microsoft.AspNetCore.Mvc;

namespace CloudSelfService.Endpoints;

public static class DownloadEndpoints
{
    public const string UnavailableMessage = "The service is temporarily unavailable.";
    private const int CopyBufferSize = 80 * 1024;
    private static readonly string Operation = StorageOperation.Download.ToString();

    public static void MapDownloadEndpoints(this WebApplication app) =>
        app.MapGet("/api/files/{targetId:guid}/download", DownloadAsync).RequireAuthorization();

    private static async Task<IResult> DownloadAsync(Guid targetId, [FromQuery] string? path, HttpContext ctx,
        StorageAccessService access, IStorageBackendResolver backends, AuditService audit,
        TimeProvider time, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger("CloudSelfService.Download");
        ctx.Response.Headers.CacheControl = "no-store";

        if (AppClaims.UserIdOf(ctx.User) is not { } userId)
            return Results.Unauthorized();

        var auditCtx = AuthEndpoints.AuditContextFor(ctx);
        var requested = path ?? "";
        AccessResult<(StorageTarget Target, StorageItem Item)> result;
        try
        {
            result = await access.AuthorizeDownloadAsync(userId, targetId, requested, auditCtx, ctx.RequestAborted);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Download authorisation failed on target {TargetId} (correlation {CorrelationId}).",
                targetId, auditCtx.CorrelationId);
            return Text(StatusCodes.Status503ServiceUnavailable, UnavailableMessage);
        }

        Task Record(Guid? customerId, string recordedPath, long bytes, AuditResult outcome, string error) =>
            audit.RecordAsync(new AuditEvent(Guid.NewGuid(), time.GetUtcNow(), userId, ctx.User.Identity?.Name ?? "",
                customerId, targetId, Operation, recordedPath, bytes, outcome, error, auditCtx.SourceIp,
                auditCtx.CorrelationId), CancellationToken.None);

        // The service returns no target on NotFound/Unavailable, and the audit row should still carry the customer.
        // After authorisation the service returns the target and normalised path on every outcome, so audit rows use those.
        var (target, item) = result.Value;
        var customerId = target?.CustomerId;
        var auditPath = item?.Path ?? requested;

        switch (result.Outcome)
        {
            case AccessOutcome.Ok:
                break;
            case AccessOutcome.Denied:
                return Text(StatusCodes.Status403Forbidden, result.Message);
            case AccessOutcome.Invalid:
                // A folder is a valid path that cannot be downloaded; every other Invalid is a malformed request.
                if (item is { IsFolder: true })
                    await Record(customerId, auditPath, 0, AuditResult.Failed, result.Message ?? "");
                return Text(StatusCodes.Status400BadRequest, result.Message);
            case AccessOutcome.NotFound:
                await Record(customerId, auditPath, 0, AuditResult.Failed, result.Message ?? "");
                return Text(StatusCodes.Status404NotFound, result.Message);
            default:
                await Record(customerId, auditPath, 0, AuditResult.Failed, result.Message ?? "");
                return Text(StatusCodes.Status503ServiceUnavailable, result.Message ?? UnavailableMessage);
        }

        if (item!.Size is not { } size)
        {
            log.LogWarning("Download of {Path} on target {TargetId} has no size from the backend (correlation {CorrelationId}).",
                auditPath, targetId, auditCtx.CorrelationId);
            await Record(customerId, auditPath, 0, AuditResult.Failed, StorageErrorMapper.UnreachableMessage);
            return Text(StatusCodes.Status503ServiceUnavailable, StorageErrorMapper.UnreachableMessage);
        }

        var range = RangeHeader.Parse(ctx.Request.Headers.Range.ToString(), size);

        if (range.Kind == RangeParseKind.Unsatisfiable)
        {
            ctx.Response.Headers.ContentRange = $"bytes */{size}";
            await Record(customerId, auditPath, 0, AuditResult.Failed, "Range not satisfiable.");
            return Text(StatusCodes.Status416RangeNotSatisfiable, "The requested range cannot be satisfied.");
        }

        var partial = range.Kind == RangeParseKind.Satisfiable;
        var start = partial ? range.Start : 0;
        var count = partial ? range.End - range.Start + 1 : size;

        Stream? source = null;
        try
        {
            if (count > 0)
                source = await backends.For(target!).OpenReadAsync(target!, item.Path, start, count, ctx.RequestAborted);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            await Record(customerId, auditPath, 0, AuditResult.Failed, StorageAccessService.CancelledMessage);
            return Results.Empty;
        }
        catch (Exception ex) when (ex is not StorageFailureException)
        {
            log.LogError(ex, "Download open failed unexpectedly on target {TargetId} at {Path} (correlation {CorrelationId}).",
                targetId, auditPath, auditCtx.CorrelationId);
            await Record(customerId, auditPath, 0, AuditResult.Failed, StorageErrorMapper.UnreachableMessage);
            return Text(StatusCodes.Status503ServiceUnavailable, StorageErrorMapper.UnreachableMessage);
        }
        catch (StorageFailureException failure)
        {
            log.LogWarning("Download open failed on target {TargetId} at {Path}: {Kind} (correlation {CorrelationId}).",
                targetId, item.Path, failure.Kind, auditCtx.CorrelationId);
            await Record(customerId, auditPath, 0, AuditResult.Failed, failure.Message);
            return Text(failure.Kind == StorageFailureKind.NotFound
                ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable, failure.Message);
        }

        var response = ctx.Response;
        response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        response.ContentType = "application/octet-stream";
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(StoragePath.Leaf(item.Path));
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers.AcceptRanges = "bytes";
        if (partial)
            response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{size}";
        response.ContentLength = count;

        long written = 0;
        try
        {
            if (source is not null)
            {
                await using (source)
                {
                    var buffer = new byte[CopyBufferSize];
                    int read;
                    while ((read = await source.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                    {
                        await response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                        written += read;
                    }
                }
            }

            if (written != count)
                throw new IOException($"Storage returned {written} of {count} expected bytes.");
            await Record(customerId, auditPath, written, AuditResult.Success, "");
        }
        catch (Exception ex)
        {
            var disconnected = ex is OperationCanceledException or IOException && ctx.RequestAborted.IsCancellationRequested;
            if (!disconnected)
                log.LogError(ex, "Download of {Path} on target {TargetId} failed after {Bytes} bytes (correlation {CorrelationId}).",
                    auditPath, targetId, written, auditCtx.CorrelationId);
            // Headers are already sent, so the only honest signal left is a truncated, aborted response.
            await Record(customerId, auditPath, written, AuditResult.Failed,
                disconnected ? StorageAccessService.CancelledMessage : StorageErrorMapper.UnreachableMessage);
            ctx.Abort();
        }
        return Results.Empty;
    }

    private static IResult Text(int status, string? message) =>
        Results.Text(message ?? "", "text/plain; charset=utf-8", statusCode: status);
}
