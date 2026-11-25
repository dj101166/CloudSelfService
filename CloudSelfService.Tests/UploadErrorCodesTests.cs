using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

public class UploadErrorCodesTests
{
    [Fact]
    public void UploadErrorCodes_WireValues_Pinned()
    {
        Assert.Equal("exists", UploadErrorCodes.Exists);
        Assert.Equal("in_progress", UploadErrorCodes.InProgress);
        Assert.Equal("restart", UploadErrorCodes.Restart);
        Assert.Equal("retry", UploadErrorCodes.Retry);
        Assert.Equal("invalid", UploadErrorCodes.Invalid);
        Assert.Equal("denied", UploadErrorCodes.Denied);
        Assert.Equal("not_found", UploadErrorCodes.NotFound);
        Assert.Equal("too_large", UploadErrorCodes.TooLarge);
        Assert.Equal("busy", UploadErrorCodes.Busy);
        Assert.Equal("wait", UploadErrorCodes.Wait);
        Assert.Equal("unavailable", UploadErrorCodes.Unavailable);
    }
}
