using CloudSelfService.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CloudSelfService.Tests;

public class DevSignInGuardTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "CloudSelfService";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(string provider) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Provider"] = provider,
        }).Build();

    [Fact]
    public void AssertSafe_DevProviderInProduction_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DevSignIn.AssertSafe(new Env(Environments.Production), Config("Dev")));

        Assert.Equal("Dev sign-in cannot run outside Development", ex.Message);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("DEV")]
    public void AssertSafe_DevProviderAnyCaseInStaging_Throws(string provider)
    {
        Assert.Throws<InvalidOperationException>(
            () => DevSignIn.AssertSafe(new Env(Environments.Staging), Config(provider)));
    }

    [Fact]
    public void AssertSafe_EntraInProduction_DoesNotThrow()
    {
        DevSignIn.AssertSafe(new Env(Environments.Production), Config("Entra"));
    }

    [Fact]
    public void AssertSafe_DevProviderInDevelopment_DoesNotThrow()
    {
        DevSignIn.AssertSafe(new Env(Environments.Development), Config("Dev"));
    }

    [Fact]
    public void IsEnabled_DevProviderOutsideDevelopment_False()
    {
        Assert.False(DevSignIn.IsEnabled(new Env(Environments.Production), Config("Dev")));
        Assert.True(DevSignIn.IsEnabled(new Env(Environments.Development), Config("Dev")));
        Assert.False(DevSignIn.IsEnabled(new Env(Environments.Development), Config("Entra")));
    }
}
