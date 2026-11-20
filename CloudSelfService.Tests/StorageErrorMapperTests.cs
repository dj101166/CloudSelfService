using System.Net.Sockets;
using Azure;
using Azure.Identity;
using CloudSelfService.Services.Storage;
using Xunit;

namespace CloudSelfService.Tests;

public class StorageErrorMapperTests
{
    private static StorageFailureKind KindOf(Exception ex) => StorageErrorMapper.Map(ex).Kind;

    [Fact]
    public void Map_404_NotFound() =>
        Assert.Equal(StorageFailureKind.NotFound, KindOf(new RequestFailedException(404, "gone", "BlobNotFound", null)));

    [Theory]
    [InlineData("ResourceAlreadyExists")]
    [InlineData("BlobAlreadyExists")]
    public void Map_409AlreadyExistsCodes_AlreadyExists(string code) =>
        Assert.Equal(StorageFailureKind.AlreadyExists, KindOf(new RequestFailedException(409, "dup", code, null)));

    [Fact]
    public void Map_412_AlreadyExists() =>
        Assert.Equal(StorageFailureKind.AlreadyExists, KindOf(new RequestFailedException(412, "cond", "ConditionNotMet", null)));

    [Fact]
    public void Map_409OtherCode_Other() =>
        Assert.Equal(StorageFailureKind.Other, KindOf(new RequestFailedException(409, "busy", "LeaseIdMissing", null)));

    [Fact]
    public void Map_403_NotAuthorised()
    {
        var mapped = StorageErrorMapper.Map(new RequestFailedException(403, "denied", "AuthorizationFailure", null));
        Assert.Equal(StorageFailureKind.NotAuthorised, mapped.Kind);
        Assert.Equal("The portal is not authorised on this storage account. Contact Advanced support.", mapped.Message);
    }

    [Fact]
    public void Map_AuthenticationFailed_NotAuthorised() =>
        Assert.Equal(StorageFailureKind.NotAuthorised, KindOf(new AuthenticationFailedException("no token")));

    [Fact]
    public void Map_Status0_Unreachable()
    {
        var mapped = StorageErrorMapper.Map(new RequestFailedException(0, "network", null, null));
        Assert.Equal(StorageFailureKind.Unreachable, mapped.Kind);
        Assert.Equal("Storage is temporarily unreachable. Please try again shortly.", mapped.Message);
    }

    [Fact]
    public void Map_HttpRequestException_Unreachable() =>
        Assert.Equal(StorageFailureKind.Unreachable, KindOf(new HttpRequestException("boom")));

    [Fact]
    public void Map_SocketException_Unreachable() =>
        Assert.Equal(StorageFailureKind.Unreachable, KindOf(new SocketException()));

    [Fact]
    public void Map_TimeoutTaskCancelled_Unreachable() =>
        Assert.Equal(StorageFailureKind.Unreachable,
            KindOf(new TaskCanceledException("t", new TimeoutException())));

    [Fact]
    public void Map_NonCallerOperationCanceled_Unreachable() =>
        Assert.Equal(StorageFailureKind.Unreachable, KindOf(new OperationCanceledException("timeout")));

    [Fact]
    public void Map_Md5Mismatch_Conflict()
    {
        var mapped = StorageErrorMapper.Map(new RequestFailedException(400, "bad md5", "Md5Mismatch", null));
        Assert.Equal(StorageFailureKind.Conflict, mapped.Kind);
        Assert.Equal("A piece of the upload was corrupted in transit and will be retried.", mapped.Message);
    }

    [Fact]
    public void Map_InvalidBlockList_ConflictWithRestartMessage()
    {
        var mapped = StorageErrorMapper.Map(new RequestFailedException(400, "bad list", "InvalidBlockList", null));
        Assert.Equal(StorageFailureKind.Conflict, mapped.Kind);
        Assert.Equal("This upload was interrupted by another upload of the same file. Please start it again.", mapped.Message);
    }

    [Fact]
    public void Map_Anything_Other()
    {
        var mapped = StorageErrorMapper.Map(new InvalidOperationException("secret detail"));
        Assert.Equal(StorageFailureKind.Other, mapped.Kind);
        Assert.Equal("Something went wrong talking to storage.", mapped.Message);
    }

    [Fact]
    public void Map_AlreadyMapped_ReturnedUnchanged()
    {
        var original = new StorageFailureException(StorageFailureKind.NotFound, "x");
        Assert.Same(original, StorageErrorMapper.Map(original));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(0)]
    public void Map_UserMessageNeverContainsExceptionText(int status)
    {
        const string leak = "https://acct.blob.core.windows.net/secret-container/customer.docx";
        var mapped = StorageErrorMapper.Map(new RequestFailedException(status, leak, "SomeCode", null));
        Assert.DoesNotContain("secret-container", mapped.Message);
        Assert.DoesNotContain("acct", mapped.Message);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_PassesThrough()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            StorageErrorMapper.RunAsync<int>(() => throw new OperationCanceledException(cts.Token), cts.Token));
    }

    [Fact]
    public async Task RunAsync_SdkFailure_ThrowsMapped()
    {
        var ex = await Assert.ThrowsAsync<StorageFailureException>(() =>
            StorageErrorMapper.RunAsync<int>(() => throw new RequestFailedException(404, "x", "BlobNotFound", null),
                CancellationToken.None));
        Assert.Equal(StorageFailureKind.NotFound, ex.Kind);
    }
}
