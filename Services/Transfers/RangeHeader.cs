using System.Globalization;

namespace CloudSelfService.Services.Transfers;

public static class RangeHeader
{
    public static bool TryParse(string? header, long length, out long start, out long end)
    {
        start = end = 0;
        const string prefix = "bytes=";
        if (length <= 0 || header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var spec = header[prefix.Length..].Trim();
        if (spec.Contains(','))
            return false;

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();

        if (first.Length == 0)
        {
            if (!TryNumber(last, out var suffix) || suffix <= 0)
                return false;
            start = Math.Max(0, length - suffix);
            end = length - 1;
            return true;
        }

        if (!TryNumber(first, out start) || start >= length)
            return false;
        if (last.Length == 0)
        {
            end = length - 1;
            return true;
        }
        if (!TryNumber(last, out end) || end < start)
            return false;
        end = Math.Min(end, length - 1);
        return true;
    }

    private static bool TryNumber(string s, out long value) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
