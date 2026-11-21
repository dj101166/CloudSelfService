namespace CloudSelfService.Services;

public static class DataProtectionPaths
{
    public static string? KeyDirectory(string? dataRoot) =>
        string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "dp-keys");
}
