using CloudSelfService.Services.Files;
using Xunit;

namespace CloudSelfService.Tests;

public class EarlyUploadUpdatesTests
{
    private static UploadItem Item(string id = "u1") => new(id, "backup.bak", Guid.NewGuid(), "", 100);

    [Fact]
    public void ApplyTo_StatusReportedBeforeItemExisted_ItemShowsUploading()
    {
        var early = new EarlyUploadUpdates();
        early.Hold("u1", i => i.Status = UploadStatusNames.Queued);
        early.Hold("u1", i => i.Status = UploadStatusNames.Uploading);

        var item = Item();
        early.ApplyTo(item);

        Assert.Equal(UploadStatusNames.Uploading, item.Status);
        Assert.Equal("Uploading", UploadStatusNames.Label(item));
    }

    [Fact]
    public void ApplyTo_AppliesInArrivalOrder_ThenForgets()
    {
        var early = new EarlyUploadUpdates();
        early.Hold("u1", i => i.Sent = 10);
        early.Hold("u1", i => i.Sent = 40);
        var item = Item();

        early.ApplyTo(item);
        item.Sent = 70;
        early.ApplyTo(item);

        Assert.Equal(70, item.Sent);
        Assert.Equal(0, early.Count);
    }

    [Fact]
    public void ApplyTo_OtherFilesUpdates_NotApplied()
    {
        var early = new EarlyUploadUpdates();
        early.Hold("u2", i => i.Status = UploadStatusNames.Failed);
        var item = Item("u1");

        early.ApplyTo(item);

        Assert.Equal(UploadStatusNames.Queued, item.Status);
        Assert.Equal(1, early.Count);
    }

    [Fact]
    public void Hold_ManyUnclaimedFiles_KeepsOnlyTheNewest()
    {
        var early = new EarlyUploadUpdates();
        for (var n = 0; n < EarlyUploadUpdates.MaxHeldFiles + 10; n++)
            early.Hold("u" + n, i => i.Status = UploadStatusNames.Uploading);

        Assert.Equal(EarlyUploadUpdates.MaxHeldFiles, early.Count);
        var oldest = Item("u0");
        early.ApplyTo(oldest);
        Assert.Equal(UploadStatusNames.Queued, oldest.Status);
        var newest = Item("u" + (EarlyUploadUpdates.MaxHeldFiles + 9));
        early.ApplyTo(newest);
        Assert.Equal(UploadStatusNames.Uploading, newest.Status);
    }

    [Fact]
    public void Hold_ManyUpdatesForOneId_KeepsOnlyTheNewestAndStillApplies()
    {
        var early = new EarlyUploadUpdates();
        for (var n = 0; n < EarlyUploadUpdates.MaxChangesPerFile * 10; n++)
        {
            var sent = n;
            early.Hold("u1", i => i.Sent = sent);
        }
        var applied = 0;
        early.Hold("u1", i => applied++);
        var item = Item();

        early.ApplyTo(item);

        Assert.Equal(1, applied);
        Assert.Equal(EarlyUploadUpdates.MaxChangesPerFile * 10 - 1, item.Sent);
    }

    [Fact]
    public void Hold_AfterApplyTo_DoesNotLeakOrderEntries()
    {
        var early = new EarlyUploadUpdates();
        for (var n = 0; n < EarlyUploadUpdates.MaxHeldFiles * 3; n++)
        {
            early.Hold("u" + n, i => i.Sent = 1);
            early.ApplyTo(Item("u" + n));
        }
        early.Hold("keep", i => i.Sent = 5);

        var kept = Item("keep");
        early.ApplyTo(kept);

        Assert.Equal(5, kept.Sent);
        Assert.Equal(0, early.Count);
    }

    [Fact]
    public void UploadPanel_AppliesEarlyUpdatesWhenItCreatesItsItem()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.razor")))
            dir = dir.Parent;
        var panel = File.ReadAllText(Path.Combine(dir!.FullName, "Shared", "UploadPanel.razor"));

        Assert.Contains("this._early.ApplyTo(item);", panel);
        Assert.Contains("this._early.Hold(id, change);", panel);
    }
}
