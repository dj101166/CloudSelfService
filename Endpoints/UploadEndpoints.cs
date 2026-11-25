using System.Text.Json;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Transfers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace CloudSelfService.Endpoints;

/// <summary>
/// The upload API used by uploader.js. Every route needs a signed-in user, a same-origin fetch and the antiforgery
/// token in the RequestVerificationToken header (from GET /api/antiforgery). Bodies never pass through the circuit.
/// </summary>
public static class UploadEndpoints
{
    public const string CrossSiteMessage = "Cross-site upload requests are not allowed.";
    public const string AntiforgeryMessage = "The request is missing a valid antiforgery token. Reload the page and try again.";
    public const string BadBodyMessage = "The request body is not a valid upload request.";
    public const string TooLargeMessage = "An upload piece is larger than the 32 MiB maximum piece size.";
    public const string ControlBodyTooLargeMessage = "The request body is too large.";
    public const string JsonRequiredMessage = "Send this request as application/json.";
    /// <summary>Begin's JSON is a few hundred bytes; every non-chunk upload route gets this body cap.</summary>
    public const long ControlBodyLimit = 16 * 1024;

    public static void MapUploadEndpoints(this WebApplication app)
    {
        var uploads = app.MapGroup("/api/uploads").RequireAuthorization().AddEndpointFilter(GuardAsync);
        uploads.MapPost("", BeginAsync).RequireRateLimiting(RateLimits.Transfers);
        uploads.MapGet("/{id:guid}", StatusAsync).RequireRateLimiting(RateLimits.Transfers);
        uploads.MapPut("/{id:guid}/chunks/{index:int}", PutChunkAsync).RequireRateLimiting(RateLimits.Chunks);
        uploads.MapPost("/{id:guid}/complete", CompleteAsync).RequireRateLimiting(RateLimits.Transfers);
        uploads.MapDelete("/{id:guid}", AbortAsync).RequireRateLimiting(RateLimits.Transfers);
    }

    // Runs before the handler reads any body: same-origin fetch, body limits, content type, antiforgery, a user id, then
    // for a chunk PUT the per-user throughput bucket.
    internal static async ValueTask<object?> GuardAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var ctx = invocation.HttpContext;
        ctx.Response.Headers.CacheControl = "no-store";
        var isChunk = HttpMethods.IsPut(ctx.Request.Method);
        // Limits are set before antiforgery, which may read a form body. The chunk PUT is the group's only PUT.
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = isChunk ? ChunkMath.MaxBlobPieceSize : ControlBodyLimit;
        if (isChunk)
            ApplyMinDataRate(ctx, ctx.RequestServices.GetRequiredService<IConfiguration>());
        if (ctx.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
            return Error(StatusCodes.Status403Forbidden, CrossSiteMessage, UploadErrorCodes.Denied);
        if (ctx.Request.ContentLength > (isChunk ? ChunkMath.MaxBlobPieceSize : ControlBodyLimit))
            return isChunk
                ? Error(StatusCodes.Status413PayloadTooLarge, TooLargeMessage, UploadErrorCodes.TooLarge)
                : Error(StatusCodes.Status413PayloadTooLarge, ControlBodyTooLargeMessage, UploadErrorCodes.TooLarge);
        // Begin and Complete are the POSTs. Begin must be JSON; Complete may also have no body at all. Any other media
        // type (a form or text POST, which a plain cross-site form can send without a preflight) is refused.
        if (HttpMethods.IsPost(ctx.Request.Method) && !ctx.Request.HasJsonContentType())
        {
            var isComplete = ctx.Request.Path.Value?.EndsWith("/complete", StringComparison.OrdinalIgnoreCase) == true;
            var emptyBody = string.IsNullOrEmpty(ctx.Request.ContentType) && ctx.Request.ContentLength is null or 0;
            if (!(isComplete && emptyBody))
                return Error(StatusCodes.Status415UnsupportedMediaType, JsonRequiredMessage, UploadErrorCodes.Invalid);
        }
        // ValidateRequestAsync, not IsRequestValidAsync: the latter waves GET through, and the status route is a GET.
        try
        {
            await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            return Error(StatusCodes.Status400BadRequest, AntiforgeryMessage, UploadErrorCodes.Invalid);
        }
        if (AppClaims.UserIdOf(ctx.User) is null)
            return Results.Unauthorized();
        if (isChunk && !ctx.RequestServices.GetRequiredService<ChunkThroughputLimiter>().TryAcquire(ctx, ctx.Request.ContentLength))
            return Busy(null, UploadErrorCodes.Busy);
        return await next(invocation);
    }

    /// <summary>
    /// A client trickling a piece in would otherwise hold a buffer and a concurrency slot indefinitely. Kestrel supports a
    /// per-request minimum body data rate on HTTP/1.1 only; on HTTP/2 and HTTP/3 setting it throws NotSupportedException,
    /// and those connections rely on the piece read timeout instead.
    /// </summary>
    internal static void ApplyMinDataRate(HttpContext ctx, IConfiguration config)
    {
        if (!HttpProtocol.IsHttp11(ctx.Request.Protocol)
            || ctx.Features.Get<IHttpMinRequestBodyDataRateFeature>() is not { } rate)
            return;
        rate.MinDataRate = new MinDataRate(UploadSessionService.MinChunkBytesPerSecondFrom(config), TimeSpan.FromSeconds(5));
    }

    private static Task<IResult> BeginAsync(HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "begin", async (userId, audit, ct) =>
        {
            if (!ctx.Request.HasJsonContentType())
                return Error(StatusCodes.Status400BadRequest, BadBodyMessage, UploadErrorCodes.Invalid);
            // Read through our own cap: a chunked body has no Content-Length for the guard to check, and the server's
            // MaxRequestBodySize is not enforced by every host.
            var body = new byte[ControlBodyLimit + 1];
            var read = 0;
            int n;
            while (read < body.Length && (n = await ctx.Request.Body.ReadAsync(body.AsMemory(read), ct)) > 0)
                read += n;
            if (read > ControlBodyLimit)
                return Error(StatusCodes.Status413PayloadTooLarge, ControlBodyTooLargeMessage, UploadErrorCodes.TooLarge);
            BeginUploadRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<BeginUploadRequest>(body.AsSpan(0, read), JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, BadBodyMessage, UploadErrorCodes.Invalid);
            }
            if (request is null)
                return Error(StatusCodes.Status400BadRequest, BadBodyMessage, UploadErrorCodes.Invalid);

            var result = await uploads.BeginAsync(userId, request, audit, ct);
            return result.Outcome == AccessOutcome.Ok ? Results.Json(result.Value) : ErrorFor(result.Outcome, result.Message, result.Code);
        });

    private static Task<IResult> StatusAsync(Guid id, HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "status", async (userId, _, ct) =>
        {
            var result = await uploads.StatusAsync(userId, id, ct);
            return result.Outcome == AccessOutcome.Ok
                ? Results.Json(new { received = result.Value })
                : ErrorFor(result.Outcome, result.Message, result.Code);
        });

    private static Task<IResult> PutChunkAsync(Guid id, int index, HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "chunk", async (userId, audit, ct) =>
        {
            // Format checks (length, MD5 header) run inside the service before any database call. A declared length
            // over the largest piece size was refused by the guard; Kestrel's body limit is the backstop, and the
            // service holds each piece to its own session's size.
            var result = await uploads.PutChunkAsync(userId, id, index, ctx.Request.Body, ctx.Request.ContentLength,
                ctx.Request.Headers["Content-MD5"].ToString(), audit, ct);
            return result.Outcome == AccessOutcome.Ok ? Results.NoContent() : ErrorFor(result.Outcome, result.Message, result.Code);
        });

    private static Task<IResult> CompleteAsync(Guid id, HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "complete", async (userId, audit, ct) =>
        {
            var result = await uploads.CompleteAsync(userId, id, audit, ct);
            return result.Outcome == AccessOutcome.Ok ? Results.NoContent() : ErrorFor(result.Outcome, result.Message, result.Code);
        });

    private static Task<IResult> AbortAsync(Guid id, HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "abort", async (userId, audit, ct) =>
        {
            var result = await uploads.AbortAsync(userId, id, audit, ct);
            return result.Outcome == AccessOutcome.Ok ? Results.NoContent() : ErrorFor(result.Outcome, result.Message, result.Code);
        });

    // Store (database) failures escape the services by design; here they become a 503, never a 500.
    private static async Task<IResult> RunAsync(HttpContext ctx, ILoggerFactory loggers, string step,
        Func<Guid, Models.AuditContext, CancellationToken, Task<IResult>> action)
    {
        var userId = AppClaims.UserIdOf(ctx.User)!.Value;
        var audit = AuthEndpoints.AuditContextFor(ctx);
        try
        {
            return await action(userId, audit, ctx.RequestAborted);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            loggers.CreateLogger("CloudSelfService.Upload").LogError(ex,
                "Upload {Step} failed (correlation {CorrelationId}).", step, audit.CorrelationId);
            return Error(StatusCodes.Status503ServiceUnavailable, DownloadEndpoints.UnavailableMessage, UploadErrorCodes.Unavailable);
        }
    }

    // A service-supplied code refines the outcome (retry vs invalid, restart vs in_progress); otherwise the outcome decides.
    internal static IResult ErrorFor(AccessOutcome outcome, string? message, string? code = null) => outcome switch
    {
        AccessOutcome.Denied => Error(StatusCodes.Status403Forbidden, message, UploadErrorCodes.Denied),
        AccessOutcome.NotFound => Error(StatusCodes.Status404NotFound, message, UploadErrorCodes.NotFound),
        AccessOutcome.Invalid => Error(StatusCodes.Status400BadRequest, message, code ?? UploadErrorCodes.Invalid),
        AccessOutcome.AlreadyExists => Error(StatusCodes.Status409Conflict, message, UploadErrorCodes.Exists),
        AccessOutcome.Conflict => Error(StatusCodes.Status409Conflict, message, code ?? UploadErrorCodes.Restart),
        AccessOutcome.Busy => Busy(message, code ?? UploadErrorCodes.Busy),
        _ => Error(StatusCodes.Status503ServiceUnavailable, message ?? DownloadEndpoints.UnavailableMessage, UploadErrorCodes.Unavailable),
    };

    private static IResult Busy(string? message, string code) => new BusyResult(message ?? RateLimits.RejectedMessage, code);

    private sealed class BusyResult(string message, string code) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = "5";
            return Error(StatusCodes.Status429TooManyRequests, message, code).ExecuteAsync(httpContext);
        }
    }

    internal static IResult Error(int status, string? message, string code) =>
        Results.Json(new { error = message ?? "", code }, statusCode: status);
}
