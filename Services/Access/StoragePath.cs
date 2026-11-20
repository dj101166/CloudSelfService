namespace CloudSelfService.Services.Access;

public static class StoragePath
{
    private const int MaxSegmentLength = 255;
    private const int MaxPathLength = 1024;
    private const string ReservedChars = "\"\\:|<>*?";

    public static bool TryNormalizePath(string? raw, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (!TryValidateSegment(segment, out error))
                return false;
        }

        var joined = string.Join('/', segments);
        if (joined.Length > MaxPathLength)
        {
            error = $"Path is longer than {MaxPathLength} characters.";
            return false;
        }

        normalized = joined;
        return true;
    }

    public static bool TryValidateName(string? name, out string? error)
    {
        if (string.IsNullOrEmpty(name))
        {
            error = "Name is required.";
            return false;
        }
        if (name.Contains('/'))
        {
            error = "Name cannot contain '/'.";
            return false;
        }
        return TryValidateSegment(name, out error);
    }

    public static string Combine(string folder, string name) =>
        folder.Length == 0 ? name : folder + "/" + name;

    public static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    public static string Leaf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    private static bool TryValidateSegment(string segment, out string? error)
    {
        error = null;
        if (segment is "." or "..")
        {
            error = "Path cannot contain '.' or '..' segments.";
            return false;
        }
        if (segment.Length > MaxSegmentLength)
        {
            error = $"A name is longer than {MaxSegmentLength} characters.";
            return false;
        }
        if (segment[^1] is '.' or ' ')
        {
            error = "A name cannot end with a '.' or a space.";
            return false;
        }
        foreach (var c in segment)
        {
            if (c < 0x20 || c == 0x7F)
            {
                error = "Names cannot contain control characters.";
                return false;
            }
            if (ReservedChars.Contains(c))
            {
                error = $"Names cannot contain '{c}'.";
                return false;
            }
        }
        return true;
    }
}
