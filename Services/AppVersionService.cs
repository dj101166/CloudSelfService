namespace CloudSelfService.Services;

public class AppVersionService
{
    public string Version { get; } =
        Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";
}
