using CloudSelfService.Services.Files;
using CloudSelfService.Services.Transfers;
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

    [Fact]
    public void SignedOut_IsNotFinishedAndNotResumable()
    {
        var item = Item(10, 4, UploadStatusNames.SignedOut);
        Assert.Equal("signed-out", UploadStatusNames.SignedOut);
        Assert.False(item.IsFinished);
        Assert.False(item.CanResume);
        Assert.False(item.IsActive);
    }

    [Fact]
    public void WaitingForFile_IsNotActive()
    {
        var item = Item(10, 4, UploadStatusNames.WaitingForFile);
        Assert.Equal("waiting-file", UploadStatusNames.WaitingForFile);
        Assert.False(item.IsActive);
        Assert.False(item.CanResume);
        Assert.False(item.IsFinished);
    }

    [Fact]
    public void Label_SignedOut_SaysSignInAgain() =>
        Assert.Equal(UploadStatusNames.SignedOutMessage, UploadStatusNames.Label(Item(10, 4, UploadStatusNames.SignedOut)));

    [Fact]
    public void SignedOutMessage_MatchesUploaderScript()
    {
        Assert.Equal("You were signed out. Sign in again, then select the same file to carry on where it stopped.",
            UploadStatusNames.SignedOutMessage);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.razor"))) dir = dir.Parent;
        var js = File.ReadAllText(Path.Combine(dir!.FullName, "wwwroot", "js", "uploader.js"));
        Assert.Contains($"var SIGNED_OUT = '{UploadStatusNames.SignedOutMessage}';", js);
    }

    private static UnfinishedUpload Unfinished(Guid target, string folder, string name, long size, long received) =>
        new(Guid.NewGuid(), target, "Share", folder, name, size, 4194304, received, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1), false, DateTimeOffset.UnixEpoch.AddDays(1));

    [Fact]
    public void Waiting_ShowsPercentReceived()
    {
        var u = Unfinished(Guid.NewGuid(), "Docs", "a.bin", 1000, 333);

        var item = UploadItem.Waiting(u);

        Assert.Equal(UploadStatusNames.WaitingForFile, item.Status);
        Assert.Equal(333, item.Sent);
        Assert.Equal("Waiting for the file: 33% received", UploadStatusNames.Label(item));
    }

    [Fact]
    public void Waiting_EmptyFile_ShowsZeroPercent() =>
        Assert.Equal("Waiting for the file: 0% received",
            UploadStatusNames.Label(UploadItem.Waiting(Unfinished(Guid.NewGuid(), "", "e.bin", 0, 0))));

    [Fact]
    public void IsFor_SameTargetFolderNameAndSize_Matches()
    {
        var target = Guid.NewGuid();
        var u = Unfinished(target, "Docs", "a.bin", 10, 4);

        Assert.True(new UploadItem("u1", "a.bin", target, "Docs", 10).IsFor(u));
        Assert.False(new UploadItem("u1", "a.bin", target, "Docs", 11).IsFor(u));
        Assert.False(new UploadItem("u1", "b.bin", target, "Docs", 10).IsFor(u));
        Assert.False(new UploadItem("u1", "a.bin", target, "", 10).IsFor(u));
        Assert.False(new UploadItem("u1", "a.bin", Guid.NewGuid(), "Docs", 10).IsFor(u));
    }

    [Fact]
    public void WaitingRows_OmitsThoseMatchedByALiveItemOrDismissed()
    {
        var target = Guid.NewGuid();
        var matched = Unfinished(target, "", "a.bin", 10, 4);
        var dismissed = Unfinished(target, "", "b.bin", 10, 4);
        var waiting = Unfinished(target, "", "c.bin", 10, 4);
        var live = new[] { new UploadItem("u1", "a.bin", target, "", 10) };

        var rows = UploadItem.WaitingRows(new[] { matched, dismissed, waiting }, live, new HashSet<Guid> { dismissed.SessionId });

        Assert.Equal(new[] { waiting.SessionId }, rows.Select(r => r.SessionId));
    }

    [Fact]
    public void SessionEndingText_InViewerZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+02:00", TimeSpan.FromHours(2), null, null);

        Assert.Equal("Your sign-in ends at 16:45. Uploads still running then will pause; sign in again and select the same files to carry on.",
            UploadStatusNames.SessionEndingText(new DateTimeOffset(2026, 11, 25, 14, 45, 0, TimeSpan.Zero), zone));
    }

    [Fact]
    public void SessionEndingText_NoZone_ShowsUtcLabelled() =>
        Assert.StartsWith("Your sign-in ends at 14:45 UTC.",
            UploadStatusNames.SessionEndingText(new DateTimeOffset(2026, 11, 25, 14, 45, 0, TimeSpan.Zero), null));

    [Fact]
    public void DifferentFileMessage_IsTheAgreedText() =>
        Assert.Equal("That is a different file; it was started as a new upload.", UploadStatusNames.DifferentFileMessage);
}
