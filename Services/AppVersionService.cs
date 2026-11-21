namespace CloudSelfService.Services;

public class AppVersionService
{
    public AppVersionService(IConfiguration config) =>
        Version = Resolve(Environment.GetEnvironmentVariable("APP_VERSION"), config["APP_VERSION"], ChangelogDocument.CurrentVersion);

    public string Version { get; }

    internal static string Resolve(string? env, string? config, string? changelog)
    {
        foreach (var candidate in new[] { env, config, changelog })
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
        return "dev";
    }
}
