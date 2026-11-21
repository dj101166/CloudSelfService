using CloudSelfService.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudSelfService.Tests;

[Collection("AppVersionEnv")]
public class SmokeTests
{
    [Fact]
    public void Resolve_EnvWins() => Assert.Equal("env", AppVersionService.Resolve("env", "cfg", "log"));

    [Fact]
    public void Resolve_ConfigNext() => Assert.Equal("cfg", AppVersionService.Resolve(" ", "cfg", "log"));

    [Fact]
    public void Resolve_ChangelogNext() => Assert.Equal("log", AppVersionService.Resolve(null, "", "log"));

    [Fact]
    public void Resolve_AllBlank_Dev() => Assert.Equal("dev", AppVersionService.Resolve(null, " ", null));

    [Fact]
    public void Service_NoEnvOrConfig_UsesChangelogVersion()
    {
        var original = Environment.GetEnvironmentVariable("APP_VERSION");
        Environment.SetEnvironmentVariable("APP_VERSION", null);
        try
        {
            var svc = new AppVersionService(new ConfigurationBuilder().Build());

            Assert.Equal(ChangelogDocument.CurrentVersion, svc.Version);
            Assert.Equal("1.0.0", svc.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_VERSION", original);
        }
    }
}
