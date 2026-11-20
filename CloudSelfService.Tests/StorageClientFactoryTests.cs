using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using CloudSelfService.Models;
using CloudSelfService.Services.Storage;
using Xunit;

namespace CloudSelfService.Tests;

public class StorageClientFactoryTests
{
    private static StorageTarget Target(StorageKind kind, string suffix = "core.windows.net") =>
        new(Guid.NewGuid(), null, "t", "", "", "acct", kind, "c", suffix, true);

    [Fact]
    public void ServiceUri_GovSuffix_UsesSuffix() =>
        Assert.Equal("https://acct.file.core.usgovcloudapi.net/",
            StorageClientFactory.ServiceUri(Target(StorageKind.FileShare, "core.usgovcloudapi.net")).ToString());

    [Fact]
    public void ServiceUri_BlobKind_UsesBlobHost() =>
        Assert.Equal("https://acct.blob.core.windows.net/",
            StorageClientFactory.ServiceUri(Target(StorageKind.BlobContainer)).ToString());

    [Fact]
    public void BuildShareOptions_SetsBackupIntent() =>
        Assert.Equal(ShareTokenIntent.Backup, StorageClientFactory.BuildShareOptions().ShareTokenIntent);

    [Fact]
    public void Clients_AreBuiltPerKindFromTheTargetsAccount()
    {
        var f = new StorageClientFactory(new StubCredential());
        Assert.Equal("acct", f.Share(Target(StorageKind.FileShare)).AccountName);
        Assert.Equal("acct", f.Container(Target(StorageKind.BlobContainer)).AccountName);
        Assert.Equal("c", f.Container(Target(StorageKind.BlobContainer)).Name);
    }
}

internal sealed class StubCredential : Azure.Core.TokenCredential
{
    public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext r, CancellationToken ct) =>
        new("t", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext r, CancellationToken ct) =>
        new(GetToken(r, ct));
}
