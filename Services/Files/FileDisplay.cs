using System.Globalization;
using CloudSelfService.Services.Access;

namespace CloudSelfService.Services.Files;

/// <summary>Pure formatting and link building for the Files pages.</summary>
public static class FileDisplay
{
    private static readonly string[] Units = { "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Binary units (1 KB = 1,024 bytes): "512 B", "1.5 KB", "20.0 MB", "153 MB". Null (a folder) is empty.</summary>
    public static string Size(long? bytes)
    {
        if (bytes is not { } b || b < 0) return "";
        if (b < 1024) return $"{b} B";
        double value = b;
        var unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        // Rounding can carry 1023.96 KB up to "1024.0 KB"; show it as the next unit instead.
        if (Math.Round(value, 1) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value < 100 ? "0.0" : "0";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    /// <summary>"yyyy-MM-dd HH:mm" in the viewer's zone, or in UTC (labelled) before the browser has said what its zone is.</summary>
    public static string Modified(DateTimeOffset? at, TimeZoneInfo? zone)
    {
        if (at is not { } value) return "";
        return zone is null
            ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
            : TimeZoneInfo.ConvertTime(value, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The browser's IANA zone where this host knows it, otherwise a fixed zone at the browser's current UTC offset
    /// (a container image without tzdata cannot resolve IANA names).
    /// </summary>
    public static TimeZoneInfo? ResolveZone(string? ianaId, int? offsetMinutes)
    {
        if (!string.IsNullOrWhiteSpace(ianaId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        if (offsetMinutes is not { } minutes || Math.Abs(minutes) > 14 * 60) return null;
        var offset = TimeSpan.FromMinutes(minutes);
        return TimeZoneInfo.CreateCustomTimeZone("UTC" + (minutes < 0 ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture), offset, null, null);
    }

    /// <summary>The browser page for a folder, each segment escaped so '#', '%', '+', '&amp;' and spaces survive the URL.</summary>
    public static string BrowseHref(Guid targetId, string folder) =>
        folder.Length == 0
            ? $"files/{targetId}"
            : $"files/{targetId}/" + string.Join('/', folder.Split('/').Select(Uri.EscapeDataString));

    public static string DownloadHref(Guid targetId, string path) =>
        $"/api/files/{targetId}/download?path={Uri.EscapeDataString(path)}";

    /// <summary>Each folder on the way to <paramref name="folder"/>, with the real path to link to and the sanitised name to show.</summary>
    public static IReadOnlyList<(string Label, string Path)> Breadcrumbs(string folder)
    {
        if (folder.Length == 0) return Array.Empty<(string, string)>();
        var segments = folder.Split('/');
        return segments.Select((s, i) => (StoragePath.DisplayName(s), string.Join('/', segments, 0, i + 1))).ToList();
    }

    /// <summary>A rename keeps the parent folder; anything else is a move.</summary>
    public static bool IsRename(string from, string to) => StoragePath.Parent(from) == StoragePath.Parent(to);

    /// <summary>
    /// What the user typed into the path box, as a root-relative path: backslashes accepted, empty and blank segments
    /// dropped. Names themselves are not trimmed, so an existing name with a leading space survives an edit of its folder.
    /// </summary>
    public static string CleanTypedPath(string? typed) =>
        string.Join('/', (typed ?? "").Replace('\\', '/').Split('/').Where(s => !string.IsNullOrWhiteSpace(s)));
}
