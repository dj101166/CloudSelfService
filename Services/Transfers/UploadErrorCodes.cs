namespace CloudSelfService.Services.Transfers;

/// <summary>
/// The machine-readable <c>code</c> in every upload API error body (<c>{ "error": "…", "code": "…" }</c>). uploader.js
/// branches on these, never on the message text.
/// </summary>
public static class UploadErrorCodes
{
    /// <summary>409: the final file already exists; ask the user whether to overwrite.</summary>
    public const string Exists = "exists";

    /// <summary>409: another user is uploading the same blob right now.</summary>
    public const string InProgress = "in_progress";

    /// <summary>409: the staged data is gone; start this file again from the beginning.</summary>
    public const string Restart = "restart";

    /// <summary>400: this piece arrived damaged or incomplete; send the same piece again.</summary>
    public const string Retry = "retry";

    /// <summary>400: the request can never succeed as sent (length, MD5 header, index, name or path).</summary>
    public const string Invalid = "invalid";

    public const string Denied = "denied";
    public const string NotFound = "not_found";
    public const string TooLarge = "too_large";

    /// <summary>429: a rate or concurrency limit, or too many uploads in progress; wait for Retry-After.</summary>
    public const string Busy = "busy";

    /// <summary>503: storage or the database is unavailable; retry later.</summary>
    public const string Unavailable = "unavailable";
}
