using System.Globalization;
using CloudSelfService.Services.Transfers;

namespace CloudSelfService.Services.Files;

/// <summary>One file in the upload panel, keyed by the id uploader.js gave it. Mutated only on the circuit's dispatcher.</summary>
public sealed class UploadItem(string id, string name, Guid targetId, string folder, long size)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public Guid TargetId { get; } = targetId;
    public string Folder { get; } = folder;
    public long Size { get; set; } = size;
    public long Sent { get; set; }
    public string Status { get; set; } = UploadStatusNames.Queued;
    public string? Message { get; set; }

    /// <summary>A line shown under the status that later status reports do not overwrite.</summary>
    public string? Notice { get; set; }

    /// <summary>The server session this item was started to resume, until uploader.js reports the one Begin returned.</summary>
    public Guid? ResumingSessionId { get; set; }

    public int Percent => Size <= 0
        ? (Status == UploadStatusNames.Done ? 100 : 0)
        : (int)Math.Clamp(Sent * 100 / Size, 0, 100);

    public bool IsActive => Status is UploadStatusNames.Queued or UploadStatusNames.Uploading or UploadStatusNames.Waiting
        or UploadStatusNames.Finishing;

    public bool CanResume => Status is UploadStatusNames.Paused or UploadStatusNames.Interrupted;

    public bool IsFinished => Status is UploadStatusNames.Done or UploadStatusNames.Failed;

    /// <summary>A server-side unfinished upload shown as a row waiting for the user to select its file again.</summary>
    public static UploadItem Waiting(UnfinishedUpload u) =>
        new("w" + u.SessionId.ToString("N"), u.FileName, u.TargetId, u.Folder, u.Size)
        {
            Sent = u.ReceivedBytes,
            Status = UploadStatusNames.WaitingForFile,
        };

    public bool IsFor(UnfinishedUpload u) =>
        TargetId == u.TargetId && Size == u.Size
        && string.Equals(Folder, u.Folder, StringComparison.Ordinal) && string.Equals(Name, u.FileName, StringComparison.Ordinal);

    /// <summary>
    /// The unfinished uploads still waiting for their file: not already covered by an item in the panel, and not one
    /// whose file was already selected here (the server may have replaced that session with a new one).
    /// </summary>
    public static IReadOnlyList<UnfinishedUpload> WaitingRows(IEnumerable<UnfinishedUpload> unfinished, IEnumerable<UploadItem> live,
        IReadOnlySet<Guid> claimed)
    {
        var items = live.ToList();
        return unfinished.Where(u => !claimed.Contains(u.SessionId) && !items.Any(i => i.IsFor(u))).ToList();
    }
}

/// <summary>The status strings uploader.js reports through OnStatus.</summary>
public static class UploadStatusNames
{
    public const string Queued = "queued";
    public const string Uploading = "uploading";
    public const string Waiting = "waiting";
    public const string Finishing = "finishing";
    public const string Paused = "paused";
    public const string Interrupted = "interrupted";
    public const string Conflict = "conflict";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string SignedOut = "signed-out";
    public const string WaitingForFile = "waiting-file";

    /// <summary>uploader.js's INTERRUPTED text, which it also sends while it is still retrying a dropped connection.</summary>
    public const string ConnectionLostMessage =
        "The connection dropped. Select Resume, or select the same file again, to carry on where it stopped.";

    /// <summary>uploader.js's SIGNED_OUT text: the upload is paused, and the server still holds what it received.</summary>
    public const string SignedOutMessage =
        "You were signed out. Sign in again, then select the same file to carry on where it stopped.";

    public const string DifferentFileMessage = "That is a different file; it was started as a new upload.";

    /// <summary>The warning shown while uploads run and the sign-in ends soon, in the viewer's zone (UTC, labelled, before it is known).</summary>
    public static string SessionEndingText(DateTimeOffset endsAt, TimeZoneInfo? zone)
    {
        var time = zone is null
            ? endsAt.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC"
            : TimeZoneInfo.ConvertTime(endsAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        return $"Your sign-in ends at {time}. Uploads still running then will pause; sign in again and select the same files to carry on.";
    }

    public static string Label(UploadItem item) => item.Status switch
    {
        Queued => "Waiting to start",
        Uploading => "Uploading",
        Waiting => item.Message is { Length: > 0 } m && m != ConnectionLostMessage ? $"Retrying: {m}" : "Connection lost. Retrying…",
        Finishing => "Finishing",
        Paused => "Paused",
        Interrupted => item.Message ?? "Interrupted",
        Conflict => "Already exists",
        Done => "Uploaded",
        Failed => item.Message ?? "Failed",
        SignedOut => SignedOutMessage,
        WaitingForFile => $"Waiting for the file: {item.Percent}% received",
        _ => item.Status,
    };
}

/// <summary>What csspUploader.start returns for each file it accepted.</summary>
public sealed record UploadStart(string Id, string Name, long Size, bool Existing);

/// <summary>What cssp.timeZone returns.</summary>
public sealed record BrowserTimeZone(string? Id, int Offset);
