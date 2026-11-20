using System.Globalization;

namespace CloudSelfService.Services.Transfers;

public enum RangeParseKind { None, Satisfiable, Unsatisfiable }

public readonly record struct RangeParseResult(RangeParseKind Kind, long Start, long End);

public static class RangeHeader
{
    private static readonly RangeParseResult NoRange = new(RangeParseKind.None, 0, 0);
    private static readonly RangeParseResult Unsatisfiable = new(RangeParseKind.Unsatisfiable, 0, 0);

    /// <summary>
    /// RFC 9110: an absent, malformed or multi-range header is ignored (None, serve the whole file);
    /// a syntactically valid single range that selects nothing is Unsatisfiable (416).
    /// </summary>
    public static RangeParseResult Parse(string? header, long length)
    {
        const string prefix = "bytes=";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return NoRange;

        var spec = header[prefix.Length..].Trim();
        if (spec.Contains(','))
            return NoRange;

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return NoRange;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();

        if (first.Length == 0)
        {
            if (!TryNumber(last, out var suffix))
                return NoRange;
            if (suffix <= 0 || length <= 0)
                return Unsatisfiable;
            return new(RangeParseKind.Satisfiable, Math.Max(0, length - suffix), length - 1);
        }

        if (!TryNumber(first, out var start))
            return NoRange;
        long end = 0;
        var openEnded = last.Length == 0;
        if (!openEnded && (!TryNumber(last, out end) || end < start))
            return NoRange;

        if (start >= length)
            return Unsatisfiable;
        return new(RangeParseKind.Satisfiable, start, openEnded ? length - 1 : Math.Min(end, length - 1));
    }

    private static bool TryNumber(string s, out long value) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
