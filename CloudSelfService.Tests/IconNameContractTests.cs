using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// Every <c>&lt;Icon Name="…" /&gt;</c> in the app must name a glyph Shared/Icon.razor's switch
/// actually has.
/// </summary>
/// <remarks>
/// Icon.razor ends its switch with <c>_ =&gt; @&lt;g&gt;&lt;/g&gt;</c> — a name it does not know
/// renders an empty group. That is the right runtime behaviour (a typo should be invisible rather
/// than decorative) and it means a misspelt or simply absent name produces no build error, no
/// console message, and no failure in <c>ComponentStyleContractTests</c>: the <c>.icon</c> class is
/// styled, the SVG element is present and correctly sized, and the only symptom is a blank 16px
/// gap where a glyph should be.
/// <para>
/// Task 12 shipped <c>&lt;Icon Name="lock" /&gt;</c> on a page it could not render — the Test Case
/// Builder is <c>RequireSuperUser</c> — and nothing in the build or the suite said the glyph did
/// not exist. It was caught by hand. This test is what would have caught it, and it matters most
/// exactly where a page cannot be looked at.
/// </para>
/// <para>
/// <b>What this test still cannot see.</b> A computed <c>Name</c> is checked by reading the string
/// literals out of the expression, not by evaluating it — so a glyph name that never appears as a
/// literal (built by concatenation, returned from a method, or held in a field or a
/// <c>switch</c>) is invisible here. None exists today: all 17 call sites are either a literal or
/// a ternary over literals, and the count assertion below fails if a site matches neither shape,
/// so a new one cannot be added silently. The same limit applies to <c>Icon.razor</c>'s own arms,
/// which are read as literals from the switch.
/// </para>
/// </remarks>
public class IconNameContractTests
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

    private static IEnumerable<string> RazorFiles() =>
        new[] { "Pages", "Shared" }
            .Select(d => Path.Combine(Root().FullName, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.razor", SearchOption.AllDirectories));

    [Fact]
    public void EveryIconNameUsed_IsACaseInTheIconSwitch()
    {
        var icon = File.ReadAllText(Path.Combine(Root().FullName, "Shared", "Icon.razor"));

        // The switch arms, as written: `"name" => @<...>`. Reading the arms rather than a
        // hand-kept list means adding a glyph needs no edit here.
        var defined = Regex.Matches(icon, @"""(?<name>[a-z][a-z0-9-]*)""\s*=>")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet();

        Assert.NotEmpty(defined);

        var missing = new List<string>();
        var sitesSeen = 0;
        var sitesChecked = 0;

        foreach (var file in RazorFiles())
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            sitesSeen += Regex.Matches(text, @"<Icon\b[^>]*?\bName=""").Count;

            // A literal name: `<Icon Name="close" />`.
            foreach (Match m in Regex.Matches(text, @"<Icon\b[^>]*?\bName=""(?<name>[^""@]*)"""))
            {
                sitesChecked++;
                if (!defined.Contains(m.Groups["name"].Value))
                {
                    missing.Add($"{name} asks for \"{m.Groups["name"].Value}\"");
                }
            }

            // A computed name: `<Icon Name="@(open ? "chevron-down" : "chevron-right")" />`.
            // The expression cannot be resolved statically in general, but the glyph names inside
            // it are string literals and a typo lands in one of THOSE — which reproduces exactly
            // the silent empty-<g> this test exists for. So every literal in the expression is
            // checked, which is strictly better than skipping the site. One such site exists
            // (Shared/ChangelogDialog.razor's disclosure chevron), a ternary over two chevrons.
            foreach (Match m in Regex.Matches(text, @"<Icon\b[^>]*?\bName=""@\((?<expr>.*?)\)"""))
            {
                sitesChecked++;
                foreach (Match lit in Regex.Matches(m.Groups["expr"].Value, @"""(?<lit>[a-z][a-z0-9-]*)"""))
                {
                    if (!defined.Contains(lit.Groups["lit"].Value))
                    {
                        missing.Add($"{name} asks for \"{lit.Groups["lit"].Value}\" (inside a computed Name)");
                    }
                }
            }
        }

        // A site reached by neither pattern is a shape this test does not understand, and silence
        // about it would be the same "looks total, is not" problem the dynamic gap already was.
        // Fail loudly instead of quietly narrowing scope.
        Assert.True(sitesSeen == sitesChecked,
            $"{sitesSeen - sitesChecked} of {sitesSeen} <Icon Name=...> sites matched neither the " +
            "literal nor the computed pattern, so they went unchecked. Widen the patterns rather " +
            "than leaving a site unexamined.");

        Assert.True(missing.Count == 0,
            "Icon.razor's switch falls through to an empty <g> for a name it does not know, so an " +
            "unknown glyph draws nothing and reports nothing — add the case, or fix the name: " +
            string.Join("; ", missing.Distinct()));
    }

    /// <summary>
    /// <c>Icon.KnownNames</c> is hand-maintained beside the switch, so this asserts the two agree.
    /// </summary>
    /// <remarks>
    /// The set exists so a guard can ask whether a name resolves WITHOUT rendering the component.
    /// That is only worth anything while it says the same thing the switch does — a set that has
    /// drifted is worse than no set, because <c>IsKnownName</c> would then vouch for a name that
    /// draws an empty <c>&lt;g&gt;</c>, or refuse one that draws correctly. Neither direction
    /// reports itself, which is why both are asserted here rather than just the first.
    /// <para>
    /// The switch arms are read out of the file's source, exactly as the test above reads them,
    /// so the set is checked against the compiled behaviour rather than against a second copy of
    /// itself.
    /// </para>
    /// </remarks>
    [Fact]
    public void IconKnownNames_MatchesTheSwitchExactly()
    {
        var icon = File.ReadAllText(Path.Combine(Root().FullName, "Shared", "Icon.razor"));

        // Only the arms below the KnownNames block: the set's own entries are bare string
        // literals with no `=>` after them, so the arm pattern cannot pick them up.
        var defined = Regex.Matches(icon, @"""(?<name>[a-z][a-z0-9-]*)""\s*=>")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet();

        Assert.NotEmpty(defined);

        var declared = CloudSelfService.Shared.Icon.KnownNames;

        Assert.True(declared.SetEquals(defined),
            "Icon.KnownNames has drifted from the switch, so IsKnownName now answers for names " +
            "that draw nothing (or refuses names that draw). In the switch but not the set: " +
            string.Join(", ", defined.Except(declared).OrderBy(n => n, StringComparer.Ordinal)) +
            ". In the set but not the switch: " +
            string.Join(", ", declared.Except(defined).OrderBy(n => n, StringComparer.Ordinal)));

        // The accessor, not just the backing set: a name in the switch must answer true, and an
        // invented one must answer false rather than throwing.
        Assert.True(CloudSelfService.Shared.Icon.IsKnownName(defined.First()));
        Assert.False(CloudSelfService.Shared.Icon.IsKnownName("no-such-glyph"));
        Assert.False(CloudSelfService.Shared.Icon.IsKnownName(null));
    }
}
