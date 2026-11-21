using CloudSelfService.Shared;
using Xunit;

namespace CloudSelfService.Tests;

public class ConfirmDialogTests
{
    [Theory]
    [InlineData(null, "", true)]
    [InlineData("Reports", "Reports", true)]
    [InlineData("Reports", "reports", false)]
    [InlineData("Reports", " Reports", false)]
    [InlineData(" padded ", " padded ", true)]
    [InlineData(" padded ", "padded", false)]
    [InlineData("trailing ", "trailing", false)]
    public void IsConfirmed_ExactUntrimmedMatch(string? required, string typed, bool expected) =>
        Assert.Equal(expected, ConfirmDialog.IsConfirmed(required, typed));
}
