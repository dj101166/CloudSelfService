using System.Text.RegularExpressions;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// Static checks on wwwroot/js/uploader.js. The CSP forbids eval, and the upload API refuses a request without the
/// antiforgery header or a piece without its MD5, so each of these is a whole upload failing in the browser only.
/// </summary>
public class UploaderScriptTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.razor")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Script() => File.ReadAllText(Path.Combine(Root(), "wwwroot", "js", "uploader.js"));

    [Fact]
    public void Uploader_SendsAntiforgeryHeader() => Assert.Contains("RequestVerificationToken", Script());

    [Fact]
    public void Uploader_SendsContentMd5() => Assert.Contains("Content-MD5", Script());

    [Fact]
    public void Uploader_HasNoEvalOrInnerHtml()
    {
        var js = Script();
        Assert.DoesNotContain("eval(", js);
        Assert.DoesNotContain("innerHTML =", js);
        Assert.DoesNotContain("innerHTML=", js);
        Assert.DoesNotContain("new Function", js);
    }

    [Fact]
    public void Uploader_RetryDelaysAreTheAgreedBackoff() => Assert.Contains("[1000, 2000, 4000, 8000, 16000]", Script());

    [Fact]
    public void Uploader_TalksOnlyToSameOriginApiPaths()
    {
        var js = Script();
        Assert.DoesNotMatch(new Regex(@"https?://"), js);
        Assert.Contains("'/api/uploads'", js);
        Assert.Contains("'/api/antiforgery'", js);
    }

    [Fact]
    public void Uploader_BranchesOnEveryUploadErrorCode()
    {
        var js = Script();
        foreach (var code in new[] { "exists", "in_progress", "restart", "retry", "invalid", "denied", "not_found",
                     "too_large", "busy", "unavailable" })
            Assert.Contains($"'{code}'", js);
    }

    [Fact]
    public void SparkMd5_IsVendoredWithItsLicence()
    {
        var lib = Path.Combine(Root(), "wwwroot", "lib", "spark-md5");
        Assert.Contains("SparkMD5", File.ReadAllText(Path.Combine(lib, "spark-md5.min.js")));
        Assert.True(File.Exists(Path.Combine(lib, "LICENSE")));
    }

    [Fact]
    public void AppShell_LoadsSparkMd5BeforeTheUploader()
    {
        var app = File.ReadAllText(Path.Combine(Root(), "App.razor"));
        var spark = app.IndexOf("lib/spark-md5/spark-md5.min.js", StringComparison.Ordinal);
        var uploader = app.IndexOf("js/uploader.js", StringComparison.Ordinal);
        Assert.True(spark >= 0 && uploader > spark, "App.razor must load spark-md5 before uploader.js");
    }
}
