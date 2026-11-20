using CloudSelfService.Services;
using Xunit;

namespace CloudSelfService.Tests;

public class SmokeTests
{
    [Fact]
    public void AppVersionService_NoEnvVar_ReturnsFallback()
    {
        var svc = new AppVersionService();

        Assert.False(string.IsNullOrWhiteSpace(svc.Version));
    }
}
