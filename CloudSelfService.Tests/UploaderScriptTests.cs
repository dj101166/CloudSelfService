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

    // A piece refused for the memory budget is a wait, never a failure: it must not use up the retry or busy allowance.
    [Fact]
    public void Uploader_HandlesWaitCode()
    {
        var js = Script();
        Assert.Contains("'wait'", js);
        Assert.Contains("e.kind === 'wait'", js);
        // Busy answers met during a long wait must not add up to a failure.
        Assert.Matches(new Regex(@"e\.kind === 'wait'\) \{\s*busyWaits = 0;"), js);
    }

    // Many uploads told to wait at once must not all come back at once: repeated waits back off to 30 s, with jitter.
    [Fact]
    public void Uploader_WaitBacksOffWithJitter_AndBusyIsJittered()
    {
        var js = Script();
        Assert.Contains("MAX_WAIT_BACKOFF_SECONDS = 30", js);
        Assert.Contains("JITTER = 0.2", js);
        Assert.Matches(new Regex(@"e\.kind === 'wait'\) \{[^}]*base \* Math\.pow\(2,[^}]*waits\+\+;[^}]*sleep\(jittered\(backoff \* 1000\), signal\)"), js);
        Assert.Matches(new Regex(@"e\.kind === 'busy'\) \{[\s\S]*?sleep\(jittered\(Math\.max\(1, e\.retryAfter\) \* 1000\), signal\)"), js);
        // The wait count lives in withRetry, which each piece calls afresh, so a piece that succeeds resets it.
        Assert.Matches(new Regex(@"function withRetry\(u, work, signal\) \{\s*var attempt = 0;\s*var busyWaits = 0;\s*var waits = 0;"), js);
        // A wait never counts as an attempt.
        Assert.DoesNotMatch(new Regex(@"e\.kind === 'wait'\) \{[^}]*attempt"), js);
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
    public void SessionGuard_Keepalive_TreatsRedirectOr401AsSessionEnded()
    {
        var guard = File.ReadAllText(Path.Combine(Root(), "wwwroot", "js", "session-guard.js"));
        Assert.Contains("fetch(\"/keepalive\", { method: \"GET\", credentials: \"same-origin\", redirect: \"manual\" })", guard);
        Assert.Contains("r.type === \"opaqueredirect\" || r.status === 401", guard);
        Assert.Contains("function sessionEnded() { window.location.href = \"/logout\"; }", guard);
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

    // A sign-in that lapses mid-upload must leave the server session intact so the same file resumes after signing in
    // again: finishing the upload as failed (or cancelling it) would throw that away.
    [Fact]
    public void Uploader_ExpiryPausesInsteadOfFailing()
    {
        var js = Script();
        Assert.Contains("'signed-out'", js);
        var body = FunctionBody(js, "expireAll");
        Assert.DoesNotContain("finish(", body);
        Assert.DoesNotContain("'DELETE'", body);
        Assert.DoesNotContain("cancel(", body);
        Assert.Contains("'signed-out'", body);
    }

    [Fact]
    public void Uploader_FingerprintIsFirstFourMiB()
    {
        var js = Script();
        Assert.Contains("FINGERPRINT_BYTES = 4194304", js);
        Assert.DoesNotContain("CHUNK_SIZE", js);
    }

    [Fact]
    public void Uploader_ReadsSessionEnd()
    {
        var js = Script();
        Assert.True(js.Contains("data-session-ends", StringComparison.Ordinal) || js.Contains("dataset.sessionEnds", StringComparison.Ordinal),
            "uploader.js must read the sign-in end time App.razor writes on <body>");
        Assert.Contains("'OnSessionEnding'", js);
    }

    [Fact]
    public void Uploader_ReportsTheSessionIdBeginReturned() => Assert.Contains("'OnSession'", Script());

    /// <summary>The text between a named function's opening brace and its matching closing brace.</summary>
    private static string FunctionBody(string js, string name)
    {
        var start = js.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"uploader.js has no function {name}");
        var open = js.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < js.Length; i++)
        {
            if (js[i] == '{') depth++;
            else if (js[i] == '}' && --depth == 0) return js.Substring(open + 1, i - open - 1);
        }
        Assert.Fail($"function {name} is not closed");
        return "";
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
