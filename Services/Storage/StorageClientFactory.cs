using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using CloudSelfService.Models;

namespace CloudSelfService.Services.Storage;

public sealed class StorageClientFactory
{
    private readonly Lazy<TokenCredential> _credential;
    private readonly ConcurrentDictionary<(string, StorageKind, string), object> _services = new();

    public StorageClientFactory() : this(null) { }

    public StorageClientFactory(TokenCredential? credential) =>
        _credential = new Lazy<TokenCredential>(() => credential ?? new DefaultAzureCredential());

    public TokenCredential Credential => _credential.Value;

    public ShareClient Share(StorageTarget t)
    {
        var svc = (ShareServiceClient)_services.GetOrAdd((t.AccountName, StorageKind.FileShare, t.EndpointSuffix),
            _ => new ShareServiceClient(ServiceUri(t with { Kind = StorageKind.FileShare }), Credential, BuildShareOptions()));
        return svc.GetShareClient(t.ContainerName);
    }

    public BlobContainerClient Container(StorageTarget t)
    {
        var svc = (BlobServiceClient)_services.GetOrAdd((t.AccountName, StorageKind.BlobContainer, t.EndpointSuffix),
            _ => new BlobServiceClient(ServiceUri(t with { Kind = StorageKind.BlobContainer }), Credential));
        return svc.GetBlobContainerClient(t.ContainerName);
    }

    public static Uri ServiceUri(StorageTarget t)
    {
        var service = t.Kind == StorageKind.FileShare ? "file" : "blob";
        return new Uri($"https://{t.AccountName}.{service}.{t.EndpointSuffix}/");
    }

    internal static ShareClientOptions BuildShareOptions() => new() { ShareTokenIntent = ShareTokenIntent.Backup };
}
