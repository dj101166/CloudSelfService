using System.Net.Sockets;
using Azure;
using Azure.Identity;

namespace CloudSelfService.Services.Storage;

public enum StorageFailureKind { NotFound, AlreadyExists, NotAuthorised, Unreachable, Conflict, Other }

public sealed class StorageFailureException : Exception
{
    public StorageFailureException(StorageFailureKind kind, string userMessage, Exception? inner = null)
        : base(userMessage, inner) => Kind = kind;

    public StorageFailureKind Kind { get; }
}

public static class StorageErrorMapper
{
    public const string NotAuthorisedMessage = "The portal is not authorised on this storage account. Contact Advanced support.";
    public const string UnreachableMessage = "Storage is temporarily unreachable. Please try again shortly.";
    public const string CorruptedChunkMessage = "A piece of the upload was corrupted in transit and will be retried.";
    public const string ChangedDuringReadMessage = "The file changed while it was being downloaded. Please try again.";
    public const string OtherMessage = "Something went wrong talking to storage.";
    public const string MoveIntoItselfMessage = "Can't move a folder into itself.";
    public const string UploadInterruptedMessage = "This upload was interrupted by another upload of the same file. Please start it again.";
    public const string NotFoundMessage = "That item no longer exists.";
    public const string AlreadyExistsMessage = "An item with that name already exists.";

    public static StorageFailureException Map(Exception ex)
    {
        switch (ex)
        {
            case StorageFailureException already:
                return already;
            case AuthenticationFailedException:
                return new(StorageFailureKind.NotAuthorised, NotAuthorisedMessage, ex);
            case RequestFailedException rfe:
                return MapRequest(rfe);
            case HttpRequestException or SocketException or TimeoutException:
                return new(StorageFailureKind.Unreachable, UnreachableMessage, ex);
            case OperationCanceledException:
                return new(StorageFailureKind.Unreachable, UnreachableMessage, ex);
            case AggregateException { InnerExceptions.Count: 1 } agg:
                return Map(agg.InnerExceptions[0]);
            default:
                return new(StorageFailureKind.Other, OtherMessage, ex);
        }
    }

    private static StorageFailureException MapRequest(RequestFailedException ex)
    {
        var code = ex.ErrorCode;
        return ex.Status switch
        {
            404 => new(StorageFailureKind.NotFound, NotFoundMessage, ex),
            409 when code is "ResourceAlreadyExists" or "BlobAlreadyExists" => new(StorageFailureKind.AlreadyExists, AlreadyExistsMessage, ex),
            412 => new(StorageFailureKind.AlreadyExists, AlreadyExistsMessage, ex),
            401 or 403 => new(StorageFailureKind.NotAuthorised, NotAuthorisedMessage, ex),
            400 when code == "Md5Mismatch" => new(StorageFailureKind.Conflict, CorruptedChunkMessage, ex),
            400 when code == "InvalidBlockList" => new(StorageFailureKind.Conflict, UploadInterruptedMessage, ex),
            0 => new(StorageFailureKind.Unreachable, UnreachableMessage, ex),
            _ => new(StorageFailureKind.Other, OtherMessage, ex),
        };
    }

    public static StorageFailureException MoveIntoItself() =>
        new(StorageFailureKind.Conflict, MoveIntoItselfMessage);

    /// <summary>Runs a storage call, converting SDK failures to <see cref="StorageFailureException"/>; caller cancellation passes through.</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        try { return await op().ConfigureAwait(false); }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw Map(ex);
        }
    }

    public static async Task RunAsync(Func<Task> op, CancellationToken ct) =>
        await RunAsync<object?>(async () => { await op().ConfigureAwait(false); return null; }, ct).ConfigureAwait(false);
}
