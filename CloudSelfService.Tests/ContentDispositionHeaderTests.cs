using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

public class ContentDispositionHeaderTests
{
    [Fact]
    public void Attachment_NonAscii_HasFilenameStar()
    {
        var h = ContentDispositionHeader.Attachment("Ünï #1.txt");
        Assert.Contains("filename*=UTF-8''%C3%9Cn%C3%AF%20%231.txt", h);
        Assert.StartsWith("attachment; filename=\"", h);
        Assert.Contains("filename=\"_n_ #1.txt\"", h);
    }

    [Fact]
    public void Attachment_QuoteInName_Stripped()
    {
        var h = ContentDispositionHeader.Attachment("a\"b\\c.txt");
        Assert.Contains("filename=\"abc.txt\"", h);
        Assert.Contains("filename*=UTF-8''a%22b%5Cc.txt", h);
    }

    [Fact]
    public void Attachment_ControlCharacter_ReplacedInFallback()
    {
        var h = ContentDispositionHeader.Attachment("a\r\nb.txt");
        Assert.Contains("filename=\"a__b.txt\"", h);
        Assert.DoesNotContain("\r", h);
        Assert.DoesNotContain("\n", h);
    }

    [Fact]
    public void Attachment_PlainName_Unchanged()
    {
        var h = ContentDispositionHeader.Attachment("report-1.pdf");
        Assert.Equal("attachment; filename=\"report-1.pdf\"; filename*=UTF-8''report-1.pdf", h);
    }
}
