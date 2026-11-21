using System.Text;

namespace CloudSelfService.Services.Identity;

/// <summary>
/// The one definition of "the same UPN". Case-insensitive comparison in .NET and lower() in PostgreSQL disagree on
/// some characters (Kelvin sign, dotted I), which would let two different strings be one account in one place and two
/// in the other; both sides now compare this form, which the database stores in <c>upn_normalized</c>.
/// </summary>
public static class UpnNormalizer
{
    public static string Normalize(string? upn) =>
        string.IsNullOrEmpty(upn) ? "" : upn.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
}
