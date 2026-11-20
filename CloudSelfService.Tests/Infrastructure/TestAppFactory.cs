using System.Net;
using System.Security.Cryptography;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudSelfService.Tests.Infrastructure;

/// <summary>
/// The real app in Development with dev sign-in, in-memory stores and the fake storage backend. Every
/// setting that changes behaviour is set explicitly so a developer's appsettings.Development.json
/// (connection string, auth provider, storage mode) cannot leak into a test run.
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly (string Key, string Value)[] _settings;

    public TestAppFactory(params (string Key, string Value)[] settings) => _settings = settings;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:Provider", "Dev");
        builder.UseSetting("Storage:UseFake", "true");
        builder.UseSetting("ConnectionStrings:Default", "");
        builder.UseSetting("Authorization:Owners", "");
        builder.UseSetting("Entra:StaffEmailDomain", "advancedutility.com");
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
    }

    public FakeStorageBackend Storage => Services.GetRequiredService<FakeStorageBackend>();

    public HttpClient CreateAnonymousClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Signs in through /dev/signin?as=... and keeps the cookie on the returned client.</summary>
    public async Task<HttpClient> CreateClientAs(string devPrincipal)
    {
        var client = CreateAnonymousClient();
        var response = await client.GetAsync($"/dev/signin?as={devPrincipal}");
        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Dev sign-in as '{devPrincipal}' returned {(int)response.StatusCode}.");
        return client;
    }

    /// <summary>The target /dev/signin seeds for a customer persona, e.g. "Read" for customer-read.</summary>
    public async Task<StorageTarget> DemoTargetAsync(string level)
    {
        var targets = Services.GetRequiredService<ITargetStore>();
        return (await targets.ListAsync()).Single(t => t.DisplayName == $"Demo share ({level})");
    }

    public async Task SeedFileAsync(StorageTarget target, string path, byte[] data)
    {
        var staging = $"{path}.upload";
        await Storage.BeginUploadAsync(target, staging, data.Length, default);
        await Storage.WriteChunkAsync(target, staging, path, "b0", 0, new MemoryStream(data), data.Length, MD5.HashData(data), default);
        await Storage.CompleteUploadAsync(target, staging, path, new[] { "b0" }, true, default);
    }
}
