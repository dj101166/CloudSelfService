using CloudSelfService.Services;
using Xunit;

namespace CloudSelfService.Tests;

public class SmokeTests
{
    [Fact]
    public void AppVersionService_NoEnvVar_ReturnsFallback()
    {
        var original = Environment.GetEnvironmentVariable("APP_VERSION");
        Environment.SetEnvironmentVariable("APP_VERSION", null);
        try
        {
            var svc = new AppVersionService();

            Assert.Equal("dev", svc.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_VERSION", original);
        }
    }
}
