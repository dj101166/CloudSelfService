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

    public int Percent => Size <= 0
        ? (Status == UploadStatusNames.Done ? 100 : 0)
        : (int)Math.Clamp(Sent * 100 / Size, 0, 100);

    public bool IsActive => Status is UploadStatusNames.Queued or UploadStatusNames.Uploading or UploadStatusNames.Waiting
        or UploadStatusNames.Finishing;

    public bool CanResume => Status is UploadStatusNames.Paused or UploadStatusNames.Interrupted;

    public bool IsFinished => Status is UploadStatusNames.Done or UploadStatusNames.Failed;
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

    /// <summary>uploader.js's INTERRUPTED text, which it also sends while it is still retrying a dropped connection.</summary>
    public const string ConnectionLostMessage =
        "The connection dropped. Select Resume, or select the same file again, to carry on where it stopped.";

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
        _ => item.Status,
    };
}

/// <summary>What csspUploader.start returns for each file it accepted.</summary>
public sealed record UploadStart(string Id, string Name, long Size, bool Existing);

/// <summary>What cssp.timeZone returns.</summary>
public sealed record BrowserTimeZone(string? Id, int Offset);
