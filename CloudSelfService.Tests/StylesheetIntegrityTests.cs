using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// A dropped <c>*/</c> is the one CSS mistake that leaves no trace anywhere else. Task 5's fix
/// round lost one and swallowed the entire base <c>.btn</c> rule into a 60-line comment: the app
/// compiled, every test passed, and the diff read as a clean deletion — sumi buttons kept their
/// variant colours and silently fell back to Bootstrap for all of their geometry.
/// </summary>
/// <remarks>
/// Every task from 6 through 13 edits <c>app.css</c>, so the count is asserted per file and the
/// failure names the file: a single repo-wide total says something is wrong without saying where.
/// <para>
/// The enumeration is the top level of <c>wwwroot/css/</c> only, which is exactly the set this
/// migration writes. The vendored <c>bootstrap/</c> and <c>open-iconic/</c> directories are gone, so
/// the directory holds nothing but the two authored files.
/// </para>
/// <para>
/// The counts are of literal markers, so a CSS string or a <c>content:</c> value containing
/// <c>/*</c> or <c>*/</c> would skew them. No such value exists in any of these files today; if one
/// is ever added, fix this test rather than deleting it.
/// </para>
/// </remarks>
public class StylesheetIntegrityTests
{
    private static string CssDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.razor")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "wwwroot", "css");
    }

    private static string[] CssFileNames()
    {
        return Directory
            .GetFiles(CssDirectory(), "*.css", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;
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
        var path = Path.Combine(CssDirectory(), fileName);
        var css = File.ReadAllText(path);

        var opened = Regex.Matches(css, @"/\*").Count;
        var closed = Regex.Matches(css, @"\*/").Count;

        Assert.True(
            opened == closed,
            $"wwwroot/css/{fileName}: {opened} '/*' against {closed} '*/'. An unterminated comment " +
            "swallows every rule after it until the next terminator, and nothing else reports that.");
    }

    /// <summary>
    /// Split out of the named-file check below so the anti-vacuous protection does not depend on
    /// any particular file name being present.
    /// </summary>
    /// <remarks>
    /// The theory above asserts nothing if the directory enumeration returns nothing — zero cases
    /// is a pass. It was split out when this app's `wwwroot/css` held only `site.css` and the named
    /// check below had to be skipped; a guard that cannot see an empty enumeration is exactly the
    /// "looks total, is not" shape the whole suite exists to avoid, and that stays true now that
    /// both checks are live.
    /// </remarks>
    [Fact]
    public void TheGuard_EnumeratesAtLeastOneStylesheet()
    {
        Assert.NotEmpty(CssFileNames());
    }

    [Fact]
    public void TheGuard_CoversTheStylesheetsThisMigrationEdits()
    {
        // A directory enumeration that silently returned nothing would pass the theory above by
        // running zero cases. These two are named because they outlive the migration, and they
        // are now the WHOLE of wwwroot/css: site.css was deliberately never named here, so its
        // deletion did not have to be paid for with an edit to this list.
        var names = CssFileNames();

        Assert.Contains("app.css", names);
        Assert.Contains("theme.css", names);
    }
}
