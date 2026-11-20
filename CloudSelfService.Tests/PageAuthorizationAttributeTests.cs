using System.Text.RegularExpressions;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// No FallbackPolicy is registered (it would loop /login onto itself), so a routable page is
/// public unless it says otherwise. This makes "forgot the attribute" a test failure.
/// </summary>
public class PageAuthorizationAttributeTests
{
    // Error.razor is the static [ExcludeFromInteractiveRouting] page the exception handler
    // re-executes; it shows no data and must render for anyone.
    private static readonly HashSet<string> AllowList = new(StringComparer.OrdinalIgnoreCase) { "Error.razor" };

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void EveryPage_HasAuthorizeOrAllowAnonymous()
    {
        var pages = Directory.GetFiles(Path.Combine(Root(), "Pages"), "*.razor", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"^@page\s", RegexOptions.Multiline))
            .ToList();
        Assert.NotEmpty(pages);

        var missing = pages
            .Where(f => !AllowList.Contains(Path.GetFileName(f)))
            .Where(f => !Regex.IsMatch(File.ReadAllText(f), @"^@attribute\s+\[(Authorize|AllowAnonymous)\b", RegexOptions.Multiline))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(missing.Count == 0,
            "Every @page must carry @attribute [Authorize...] or [AllowAnonymous]: " + string.Join(", ", missing));
    }

    [Fact]
    public void AllowListedPages_StayStaticErrorPages()
    {
        foreach (var name in AllowList)
        {
            var text = File.ReadAllText(Path.Combine(Root(), "Pages", name));
            Assert.Contains("[ExcludeFromInteractiveRouting]", text);
        }
    }
}
