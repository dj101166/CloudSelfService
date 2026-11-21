using CloudSelfService.Services;
using Xunit;

namespace CloudSelfService.Tests;

public class DataProtectionPathsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void KeyDirectory_BlankRoot_ReturnsNull(string? root) =>
        Assert.Null(DataProtectionPaths.KeyDirectory(root));

    [Fact]
    public void KeyDirectory_Root_AppendsDpKeys() =>
        Assert.Equal(Path.Combine("/mnt/data", "dp-keys"), DataProtectionPaths.KeyDirectory("/mnt/data"));
}
