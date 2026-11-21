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
    public void Uploader_SignalsActivityToSessionGuard_AtMostOncePerMinute()
    {
        var js = Script();
        Assert.Contains("ACTIVITY_INTERVAL_MS = 60000", js);
        Assert.Contains("markActivity();", js);
        // The event must be one session-guard.js listens for, or the idle timer never hears it.
        var guard = File.ReadAllText(Path.Combine(Root(), "wwwroot", "js", "session-guard.js"));
        Assert.Contains("new Event('scroll')", js);
        Assert.Contains("\"scroll\"", guard);
    }

    [Fact]
    public void Uploader_RefreshesAStaleAntiforgeryTokenOnce()
    {
        var js = Script();
        Assert.Contains("e.message === antiforgeryMessage", js);
        Assert.Contains("tokenPromise = null;", js);
        var panel = File.ReadAllText(Path.Combine(Root(), "Shared", "UploadPanel.razor"));
        Assert.Contains("UploadEndpoints.AntiforgeryMessage", panel);
    }

    [Fact]
    public void SparkMd5_IsVendoredWithItsLicence()
    {
        var lib = Path.Combine(Root(), "wwwroot", "lib", "spark-md5");
        var script = File.ReadAllText(Path.Combine(lib, "spark-md5.min.js"));
        Assert.Contains("SparkMD5", script);
        Assert.StartsWith("/*! spark-md5 v3.0.2 | (c) André Cruz | MIT License */", script);
        // Only the script is served; the notices are documentation at the repo root, taken under MIT.
        Assert.Equal(new[] { "spark-md5.min.js" }, Directory.GetFiles(lib).Select(Path.GetFileName).ToArray());
        var notice = File.ReadAllText(Path.Combine(Root(), "ThirdPartyNotices", "spark-md5", "LICENSE"));
        Assert.Contains("The MIT License", notice);
        Assert.Contains("André Cruz", notice);
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
