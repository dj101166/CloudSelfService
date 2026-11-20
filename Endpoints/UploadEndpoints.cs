using System.Text.Json;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Transfers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

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
    public const string TooLargeMessage = "An upload piece is larger than the 4 MiB piece size.";

    public static void MapUploadEndpoints(this WebApplication app)
    {
        var uploads = app.MapGroup("/api/uploads").RequireAuthorization().AddEndpointFilter(GuardAsync);
        uploads.MapPost("", BeginAsync).RequireRateLimiting(RateLimits.Transfers);
        uploads.MapGet("/{id:guid}", StatusAsync).RequireRateLimiting(RateLimits.Transfers);
        uploads.MapPut("/{id:guid}/chunks/{index:int}", PutChunkAsync).RequireRateLimiting(RateLimits.Chunks);
        uploads.MapPost("/{id:guid}/complete", CompleteAsync).RequireRateLimiting(RateLimits.Transfers);
        uploads.MapDelete("/{id:guid}", AbortAsync).RequireRateLimiting(RateLimits.Transfers);
    }

    // Runs before the handler reads any body: same-origin fetch, body limit, antiforgery, then a user id.
    private static async ValueTask<object?> GuardAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var ctx = invocation.HttpContext;
        ctx.Response.Headers.CacheControl = "no-store";
        // The chunk PUT is the group's only PUT. Its limit is set before antiforgery, which may read a form body.
        if (HttpMethods.IsPut(ctx.Request.Method)
            && ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = ChunkMath.ChunkSize;
        if (ctx.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
            return Error(StatusCodes.Status403Forbidden, CrossSiteMessage);
        // ValidateRequestAsync, not IsRequestValidAsync: the latter waves GET through, and the status route is a GET.
        try
        {
            await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            return Error(StatusCodes.Status400BadRequest, AntiforgeryMessage);
        }
        if (AppClaims.UserIdOf(ctx.User) is null)
            return Results.Unauthorized();
        return await next(invocation);
    }

    private static Task<IResult> BeginAsync(HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "begin", async (userId, audit, ct) =>
        {
            if (!ctx.Request.HasJsonContentType())
                return Error(StatusCodes.Status400BadRequest, BadBodyMessage);
            BeginUploadRequest? request;
            try
            {
                request = await ctx.Request.ReadFromJsonAsync<BeginUploadRequest>(JsonSerializerOptions.Web, ct);
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, BadBodyMessage);
            }
            if (request is null)
                return Error(StatusCodes.Status400BadRequest, BadBodyMessage);

            var result = await uploads.BeginAsync(userId, request, audit, ct);
            return result.Outcome == AccessOutcome.Ok ? Results.Json(result.Value) : ErrorFor(result.Outcome, result.Message);
        });

    private static Task<IResult> StatusAsync(Guid id, HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "status", async (userId, _, ct) =>
        {
            var result = await uploads.StatusAsync(userId, id, ct);
            return result.Outcome == AccessOutcome.Ok
                ? Results.Json(new { received = result.Value })
                : ErrorFor(result.Outcome, result.Message);
        });

    private static Task<IResult> PutChunkAsync(Guid id, int index, HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "chunk", async (userId, audit, ct) =>
        {
            if (ctx.Request.ContentLength > ChunkMath.ChunkSize)
                return Error(StatusCodes.Status413PayloadTooLarge, TooLargeMessage);

            AccessResult result;
            try
            {
                result = await uploads.PutChunkAsync(userId, id, index, ctx.Request.Body, ctx.Request.ContentLength,
                    ctx.Request.Headers["Content-MD5"].ToString(), audit, ct);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, TooLargeMessage);
            }
            return result.Outcome == AccessOutcome.Ok ? Results.NoContent() : ErrorFor(result.Outcome, result.Message);
        });

    private static Task<IResult> CompleteAsync(Guid id, HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "complete", async (userId, audit, ct) =>
        {
            var result = await uploads.CompleteAsync(userId, id, audit, ct);
            return result.Outcome == AccessOutcome.Ok ? Results.NoContent() : ErrorFor(result.Outcome, result.Message);
        });

    private static Task<IResult> AbortAsync(Guid id, HttpContext ctx, UploadSessionService uploads, ILoggerFactory loggers) =>
        RunAsync(ctx, loggers, "abort", async (userId, audit, ct) =>
        {
            var result = await uploads.AbortAsync(userId, id, audit, ct);
            return result.Outcome == AccessOutcome.Ok ? Results.NoContent() : ErrorFor(result.Outcome, result.Message);
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
            return Error(StatusCodes.Status503ServiceUnavailable, DownloadEndpoints.UnavailableMessage);
        }
    }

    private static IResult ErrorFor(AccessOutcome outcome, string? message) => outcome switch
    {
        AccessOutcome.Denied => Error(StatusCodes.Status403Forbidden, message),
        AccessOutcome.NotFound => Error(StatusCodes.Status404NotFound, message),
        AccessOutcome.Invalid => Error(StatusCodes.Status400BadRequest, message),
        AccessOutcome.AlreadyExists => Error(StatusCodes.Status409Conflict, message),
        _ => Error(StatusCodes.Status503ServiceUnavailable, message ?? DownloadEndpoints.UnavailableMessage),
    };

    private static IResult Error(int status, string? message) =>
        Results.Json(new { error = message ?? "" }, statusCode: status);
}
