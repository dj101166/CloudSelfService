using System.Security.Cryptography;
using CloudSelfService.Models;
using CloudSelfService.Services.Storage;
using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

public class LiveStorageTests
{
    [Fact(Skip = "live: needs VPN + dev test account")]
    public async Task Live_FilesAndBlob_RoundTrip()
    {
        var factory = new StorageClientFactory();
        await RoundTrip(new FileShareBackend(factory), Target(StorageKind.FileShare,
            Env("CSSP_LIVE_FILES_ACCOUNT"), Env("CSSP_LIVE_SHARE")));
        await RoundTrip(new BlobBackend(factory), Target(StorageKind.BlobContainer,
            Env("CSSP_LIVE_BLOB_ACCOUNT"), Env("CSSP_LIVE_CONTAINER")));
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");

    private static StorageTarget Target(StorageKind kind, string account, string container) =>
        new(Guid.NewGuid(), null, "live", "", "", account, kind, container, "core.windows.net", true);

    private static async Task RoundTrip(IStorageBackend b, StorageTarget t)
    {
        var ct = CancellationToken.None;
        var run = "cssp-live-" + Guid.NewGuid().ToString("N")[..8];
        var content = new byte[ChunkMath.ChunkSize * 2 + 1];
        Random.Shared.NextBytes(content);
        var sessionId = Guid.NewGuid();
        var staging = $"{run}/.big.bin.cssupload-{sessionId:N}";
        var file = $"{run}/big.bin";

        await b.ProbeAsync(t, ct);
        await b.ListAsync(t, "", null, 50, ct);
        await b.CreateFolderAsync(t, run, ct);

        await b.BeginUploadAsync(t, staging, content.Length, ct);
        var ids = new List<string>();
        for (var i = 0; i < ChunkMath.ChunkCount(content.Length); i++)
        {
            var offset = (long)i * ChunkMath.ChunkSize;
            var len = (int)ChunkMath.ExpectedLength(content.Length, i);
            var slice = content.AsMemory((int)offset, len).ToArray();
            var id = ChunkMath.BlockId(sessionId, i);
            ids.Add(id);
            await b.WriteChunkAsync(t, staging, file, id, offset, new MemoryStream(slice), len, MD5.HashData(slice), ct);
        }
        await b.CompleteUploadAsync(t, staging, file, ids, overwrite: false, ct);

        using (var s = await b.OpenReadAsync(t, file, 0, null, ct))
        {
            var hash = await SHA256.HashDataAsync(s, ct);
            Assert.Equal(SHA256.HashData(content), hash);
        }

        await b.MoveAsync(t, file, $"{run}/moved.bin", false, ct);
        Assert.Null(await b.GetItemAsync(t, file, ct));
        Assert.NotNull(await b.GetItemAsync(t, $"{run}/moved.bin", ct));

        await b.DeleteAsync(t, run, isFolder: true, ct);
        Assert.Null(await b.GetItemAsync(t, run, ct));
    }
}
