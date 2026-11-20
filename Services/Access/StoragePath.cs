using System.Globalization;
using System.Text;

namespace CloudSelfService.Services.Access;

public static class StoragePath
{
    private const int MaxSegmentLength = 255;
    private const int MaxPathLength = 1024;
    public const string InvisibleCharactersMessage = "Names can't contain invisible or direction-changing characters.";
    private const string ReservedChars = "\"\\:|<>*?";

    /// <summary>
    /// Locates an EXISTING item. Lenient on invisible/format characters, because files created outside the portal
    /// (Word paste, Storage Explorer) may carry them and the user must still be able to open, rename or delete them.
    /// Names being created or renamed to go through <see cref="TryValidateName"/> instead.
    /// </summary>
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
        return TryValidateSegment(name, out error, strict: true);
    }

    /// <summary>Replaces format and C1 control characters with "_", matching the Content-Disposition sanitiser, for display.</summary>
    public static string DisplayName(string name) =>
        string.Concat(name.EnumerateRunes().Select(r => IsInvisible(r) ? "_" : r.ToString()));

    private static bool IsInvisible(Rune r) =>
        r.Value is >= 0x80 and <= 0x9F || Rune.GetUnicodeCategory(r) == UnicodeCategory.Format;

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

    public static bool TryValidateSegment(string segment, out string? error, bool strict = false)
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
        if (strict && segment.EnumerateRunes().Any(IsInvisible))
        {
            error = InvisibleCharactersMessage;
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
