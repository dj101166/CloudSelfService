using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// A dropped <c>*/</c> is the one CSS mistake that leaves no trace anywhere else: the next rule
/// is swallowed into the comment, the app compiles, every other test passes, and the diff reads
/// as a clean deletion.
/// </summary>
/// <remarks>
/// The count is asserted per file and the failure names the file: a single total says something
/// is wrong without saying where. The set is every authored stylesheet — the top level of
/// <c>wwwroot/css/</c> AND every scoped <c>*.razor.css</c> under Pages/ and Shared/. The scoped
/// ones matter: ComponentStyleContractTests strips comments with a regex that leaves an
/// unterminated one in place, so nothing else would notice a lost terminator there.
/// <para>
/// The counts are of literal markers, so a CSS string or a <c>content:</c> value containing
/// <c>/*</c> or <c>*/</c> would skew them. No such value exists today; if one is added, fix this
/// test rather than deleting it.
/// </para>
/// </remarks>
public class StylesheetIntegrityTests
{
    private static DirectoryInfo Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.razor")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>Authored stylesheets as paths relative to the app root, forward-slashed.</summary>
    private static string[] CssFileNames()
    {
        var root = Root().FullName;
        var top = Directory.GetFiles(Path.Combine(root, "wwwroot", "css"), "*.css", SearchOption.TopDirectoryOnly);
        var scoped = new[] { "Pages", "Shared" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.razor.css", SearchOption.AllDirectories));

        return top.Concat(scoped)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public static TheoryData<string> Stylesheets()
    {
        var data = new TheoryData<string>();
        foreach (var name in CssFileNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Stylesheets))]
    public void EveryComment_IsTerminated(string fileName)
    {
        var path = Path.Combine(Root().FullName, fileName);
        var css = File.ReadAllText(path);

        var opened = Regex.Matches(css, @"/\*").Count;
        var closed = Regex.Matches(css, @"\*/").Count;

        Assert.True(
            opened == closed,
            $"{fileName}: {opened} '/*' against {closed} '*/'. An unterminated comment " +
            "swallows every rule after it until the next terminator, and nothing else reports that.");
    }

    [Fact]
    public void TheGuard_EnumeratesAtLeastOneStylesheet()
    {
        // The theory asserts nothing if the enumeration returns nothing: zero cases is a pass.
        Assert.NotEmpty(CssFileNames());
    }

    [Fact]
    public void TheGuard_CoversTopLevelAndScopedStylesheets()
    {
        var names = CssFileNames();

        Assert.Contains("wwwroot/css/app.css", names);
        Assert.Contains("wwwroot/css/theme.css", names);
        Assert.Contains("Shared/MainLayout.razor.css", names);
    }
}
