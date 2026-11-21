using CloudSelfService.Services.Files;
using Xunit;

namespace CloudSelfService.Tests;

public class UploadItemTests
{
    private static UploadItem Item(long size, long sent, string status) =>
        new("u1", "a.bin", Guid.NewGuid(), "", size) { Sent = sent, Status = status };

    [Fact]
    public void Percent_ZeroByteFile_Done_Is100() => Assert.Equal(100, Item(0, 0, UploadStatusNames.Done).Percent);

    [Fact]
    public void Percent_ZeroByteFile_Uploading_Is0() => Assert.Equal(0, Item(0, 0, UploadStatusNames.Uploading).Percent);

    [Fact]
    public void Percent_Partial_RoundsDown() => Assert.Equal(33, Item(3, 1, UploadStatusNames.Uploading).Percent);

    [Fact]
    public void Percent_ClampedTo100() => Assert.Equal(100, Item(10, 20, UploadStatusNames.Uploading).Percent);

    [Theory]
    [InlineData("queued", true, false, false)]
    [InlineData("uploading", true, false, false)]
    [InlineData("waiting", true, false, false)]
    [InlineData("finishing", true, false, false)]
    [InlineData("paused", false, true, false)]
    [InlineData("interrupted", false, true, false)]
    [InlineData("conflict", false, false, false)]
    [InlineData("done", false, false, true)]
    [InlineData("failed", false, false, true)]
    public void StatusFlags_MatchStatus(string status, bool active, bool resumable, bool finished)
    {
        var item = Item(10, 0, status);
        Assert.Equal(active, item.IsActive);
        Assert.Equal(resumable, item.CanResume);
        Assert.Equal(finished, item.IsFinished);
    }

    [Fact]
    public void Label_WaitingOnLostConnection_SaysRetrying() =>
        Assert.Equal("Connection lost. Retrying…", UploadStatusNames.Label(new UploadItem("u", "a", Guid.Empty, "", 1)
            { Status = "waiting", Message = UploadStatusNames.ConnectionLostMessage }));

    [Fact]
    public void ConnectionLostMessage_MatchesUploaderScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.razor"))) dir = dir.Parent;
        var js = File.ReadAllText(Path.Combine(dir!.FullName, "wwwroot", "js", "uploader.js"));
        Assert.Contains($"var INTERRUPTED = '{UploadStatusNames.ConnectionLostMessage}';", js);
    }

    [Fact]
    public void Label_Failed_ShowsServerMessage() =>
        Assert.Equal("Too big", UploadStatusNames.Label(new UploadItem("u", "a", Guid.Empty, "", 1) { Status = "failed", Message = "Too big" }));
}
