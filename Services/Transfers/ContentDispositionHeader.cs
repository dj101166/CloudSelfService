using System.Globalization;
using System.Text;

namespace CloudSelfService.Services.Transfers;

public static class ContentDispositionHeader
{
    public static string Attachment(string fileName)
    {
        // Same character classes StoragePath refuses in names; replaced again here so a name that predates the rule
        // cannot reorder or hide part of the filename in the browser's save dialog.
        fileName = string.Concat(fileName.EnumerateRunes().Select(r =>
            r.Value is >= 0x80 and <= 0x9F || Rune.GetUnicodeCategory(r) == UnicodeCategory.Format ? "_" : r.ToString()));

        var fallback = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
        {
            if (c is '"' or '\\')
                continue;
            fallback.Append(c < 0x20 || c > 0x7E ? '_' : c);
        }

        var encoded = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(fileName))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
                encoded.Append(c);
            else
                encoded.Append('%').Append(b.ToString("X2"));
        }

        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{encoded}";
    }
}
