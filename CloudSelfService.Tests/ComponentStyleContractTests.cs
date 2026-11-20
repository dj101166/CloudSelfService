using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// Three contracts between a component's markup and the stylesheets, each covering a mistake that
/// produces no build error, no console message and no test failure anywhere else.
/// </summary>
/// <remarks>
/// A component's <c>@code</c> block hands the browser two kinds of style string: a class name
/// (<c>badge--success</c>) and a token reference (<c>var(--outcome-fail)</c>). Neither is checked
/// by anything. A class nothing styles renders unstyled, and an undefined custom property resolves
/// to the empty string rather than throwing — which is how a rule paints nothing while its markup
/// reads as correct.
/// <para>
/// All three read the scoped <c>.razor.css</c> files as well as <c>wwwroot/css/</c>. That is where
/// every remaining page task does its work, so a guard confined to the top-level stylesheets would
/// stop watching precisely where the next change lands — and a scoped file out-ranks app.css on
/// both specificity and link order, which makes it the easiest place to reintroduce any of this.
/// </para>
/// </remarks>
public class ComponentStyleContractTests
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

    private static IEnumerable<string> ComponentDirectories() =>
        new[] { "Pages", "Shared" }
            .Select(d => Path.Combine(Root().FullName, d))
            .Where(Directory.Exists);

    private static IEnumerable<string> RazorFiles() =>
        ComponentDirectories()
            .SelectMany(d => Directory.GetFiles(d, "*.razor", SearchOption.AllDirectories));

    /// <summary>Every component's own scoped stylesheet.</summary>
    private static IEnumerable<string> ScopedCssFiles() =>
        ComponentDirectories()
            .SelectMany(d => Directory.GetFiles(d, "*.razor.css", SearchOption.AllDirectories));

    private static IEnumerable<string> TopLevelCssFiles() =>
        Directory.GetFiles(
            Path.Combine(Root().FullName, "wwwroot", "css"), "*.css", SearchOption.TopDirectoryOnly);

    /// <summary>Every stylesheet the browser is served that anyone here authors.</summary>
    private static IEnumerable<string> AuthoredCssFiles() =>
        TopLevelCssFiles().Concat(ScopedCssFiles());

    /// <summary>
    /// Every authored stylesheet, concatenated, with <c>/* ... */</c> comments removed.
    /// </summary>
    /// <remarks>
    /// The stripping is load-bearing for all checks below, each of which asks "does this name
    /// appear in the CSS". These files are heavily commented and the comments name classes and
    /// tokens constantly, so a name existing ONLY inside a comment would vouch for itself: a class
    /// deleted from the stylesheet but still described in a comment would keep passing. An
    /// unterminated comment is not stripped here at all, which is why StylesheetIntegrityTests
    /// counts <c>/*</c> against <c>*/</c> in every authored stylesheet, scoped ones included.
    /// </remarks>
    private static string AllAuthoredCss() =>
        Regex.Replace(
            string.Join("\n", AuthoredCssFiles().Select(File.ReadAllText)),
            @"/\*.*?\*/", "", RegexOptions.Singleline);

    [Fact]
    public void EveryTokenNamedInAComponent_IsDefinedInAStylesheet()
    {
        var css = AllAuthoredCss();
        var missing = new List<string>();

        // Both halves of a component: its markup and @code block, and its own scoped stylesheet.
        // A token reference is equally silent in either.
        foreach (var file in RazorFiles().Concat(ScopedCssFiles()))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"var\((--[a-z0-9-]+)\)"))
            {
                var token = m.Groups[1].Value;
                // A definition is `--name:`; a use is `var(--name)`. Only the former counts, or a
                // token that exists nowhere but is referenced twice would vouch for itself.
                if (!Regex.IsMatch(css, Regex.Escape(token) + @"\s*:"))
                {
                    missing.Add($"{Path.GetFileName(file)} names {token}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "A custom property that is not defined resolves to the empty string, so the declaration " +
            "reading it is simply dropped — no error, no warning, nothing drawn: " +
            string.Join("; ", missing.Distinct()));
    }

    [Fact]
    public void EveryComponentModifierNamedInARazorFile_IsStyledSomewhere()
    {
        var css = AllAuthoredCss();
        var missing = new List<string>();

        foreach (var file in RazorFiles())
        {
            var text = File.ReadAllText(file);
            // The BEM modifier vocabulary only. A bare `btn` or `card` is deliberately unqualified
            // during the migration and is not this test's business.
            //
            // The lookbehind is load-bearing: without it the match starts mid-name and
            // `table__row--link` is reported as the non-existent `.row--link`.
            //
            // The `__?` in the block part is equally load-bearing, and its absence made this check
            // blind to most of what it exists to guard. `[_-]` matches ONE separator, so the
            // pattern only ever reached `block--modifier`; every `block__element--modifier` —
            // `table__row--link` — matched nothing here, and the element check below
            // excludes them too because its `$` anchor forbids a `--` suffix. So that whole shape
            // was checked by NEITHER test.
            foreach (Match m in Regex.Matches(text, @"(?<![\w-])[a-z][a-z0-9]*(?:__?[a-z0-9]+|-[a-z0-9]+)*--[a-z0-9-]+"))
            {
                var name = m.Value;

                // `(?![\w-])` rather than a bare Contains: a substring test lets a LONGER rule
                // vouch for a shorter name, so `.gen__dot--idle` in the stylesheet would satisfy a
                // misspelt `.gen__dot--id` in the markup and the typo would render unstyled. The
                // element check below has always been written this way and its own remarks explain
                // why; the two halves of one gate disagreeing about that is the actual defect.
                // Zero prefix-only cases exist today, so this closes a hole rather than fixing a
                // visible break.
                if (!Regex.IsMatch(css, @"\." + Regex.Escape(name) + @"(?![\w-])"))
                {
                    missing.Add($"{Path.GetFileName(file)} uses .{name}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "A class name built in an @code block is invisible to every markup grep, so a modifier " +
            "nothing styles renders as plain text and nothing reports it: " +
            string.Join("; ", missing.Distinct()));
    }

    /// <summary>
    /// <b>Empty, and meant to stay that way.</b> A place to quarantine a known-unstyled BEM name
    /// rather than delay a change that cannot style it yet.
    /// <para>
    /// Adding a name here is a deferral, not a fix, and the staleness assertion below makes it an
    /// expensive one: the gate fails when a quarantined name stops being needed, so a list entry has
    /// to be removed deliberately rather than left to rot.
    /// </para>
    /// <para>
    /// The regex below uses <c>(?![\w-])</c> rather than a `grep -w`-style boundary, which treats
    /// `-` as a separator and so lets `.dash__bar-head` vouch for `.dash__bar`.
    /// </para>
    /// </summary>
    private static readonly string[] KnownUnstyledClasses = [];

    /// <summary>
    /// Every <c>class="..."</c> attribute value in a Razor file, with quotes inside a
    /// <c>@(...)</c> expression treated as data rather than as the attribute's terminator.
    /// </summary>
    /// <remarks>
    /// A regex cannot do this. <c>class="([^"]*)"</c> ends at the first inner quote and
    /// <c>class="(.*?)"</c> is no better; a heuristic that ends on a quote followed by whitespace
    /// truncates mid-literal instead. Razor nests real string literals inside an attribute value,
    /// so the terminator is the first quote at expression depth ZERO, which needs a scan that
    /// counts <c>@(</c> and its parentheses.
    /// <para>
    /// <b>The scan is string-aware, and it has to be.</b> Counting brackets without knowing which
    /// of them are inside a string literal mis-parses two shapes, each silently and each looking
    /// authoritative — which is worse than a regex that obviously cannot cope. Measured, before
    /// <c>inString</c> existed: an unbalanced <c>)</c> in a literal
    /// (<c>class="a @(f ? "x)" : null)"</c>) drove the depth to zero early and truncated the value
    /// to <c>a @(f ? "x)</c>, exactly as the regex did; and an unbalanced <c>(</c> left the depth
    /// permanently positive, so the attribute never terminated and the <c>class=</c> attributes
    /// after it were swallowed — all of them if the markup is on one line (measured: nothing
    /// returned where two existed), or the rest of that line in a real multi-line component
    /// (measured: one value lost from this page). Either way the loss is silent.
    /// </para>
    /// <para>
    /// An escaped <c>\"</c> belongs on that list only when a parenthesis follows it inside the
    /// literal (<c>"x\")"</c>), which is the same depth-corruption bug wearing a different hat.
    /// A plain <c>class="a @(f ? "x\"y" : null)"</c> parses IDENTICALLY under both versions,
    /// because the escaped quote sits at depth &gt; 0 where the bracket-only scan never terminated
    /// either. This paragraph previously claimed three shapes; it was measured at two.
    /// </para>
    /// <para>
    /// Being string-aware PARSES these correctly rather than skipping them, which is why there is
    /// no "give up when the depth looks wrong" guard here: such a guard cannot fire on the first
    /// case at all (the depth reads zero at the terminator, which is precisely the bug) and would
    /// discard a perfectly well-formed value in the second. A skip would also be its own quiet
    /// failure — an unexamined attribute is indistinguishable from one containing no class names.
    /// </para>
    /// <para>
    /// <b>Where this scan still omits silently.</b> Three shapes, none present in the tree, all
    /// measured. Naming them is the point: silent TRUNCATION is gone, but silent OMISSION is not,
    /// and an undocumented omission is what makes a guard look more total than it is.
    /// <list type="bullet">
    /// <item><description><c>class='...'</c>, single-quoted, matches nothing here and is dropped
    /// whole. Pre-existing, and zero occurrences in the tree.</description></item>
    /// <item><description>An attribute split across lines is dropped at the <c>\n</c> break. The
    /// break is a resynchronisation point for the SCAN, not a recovery of that value — the value
    /// itself is lost. Zero occurrences.</description></item>
    /// <item><description>A verbatim literal ending in a backslash (<c>@(f ? @"C:\" : null)</c>)
    /// was dropped whole until the <c>verbatim</c> branch above existed. Zero occurrences, and
    /// handled now rather than merely documented, because it was introduced by the escape branch
    /// this very method added.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Verified against thirteen shapes: the two mis-parses above, the escaped quote with and
    /// without a following parenthesis, verbatim literals in all three prefixes (<c>@"</c>,
    /// <c>$@"</c>, <c>@$"</c>) plus a doubled-quote escape, nested <c>@(((...)))</c>, an
    /// interpolated <c>@($"... {(a ? "b__c" : "d")}")</c>, balanced parentheses inside a literal, a
    /// plain attribute, and an unterminated one. Across the real tree it yields byte-identical
    /// results to the bracket-only version on all 1,552 attribute values in 30 files, so every one
    /// of these hardens the scanner against input the app does not contain yet.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> ClassAttributeValues(string text)
    {
        const string Open = "class=\"";
        var i = 0;

        while (true)
        {
            var k = text.IndexOf(Open, i, StringComparison.Ordinal);
            if (k < 0) yield break;

            var j = k + Open.Length;
            var depth = 0;
            var inString = false;
            var verbatim = false;
            var buf = new System.Text.StringBuilder();
            string? found = null;

            while (j < text.Length)
            {
                var c = text[j];

                // An unterminated attribute would otherwise run to the end of the file.
                if (c == '\n') break;

                // Inside a string literal nothing is syntax: a quote ends the literal, a backslash
                // escapes the next character, and a parenthesis is just a character. Tracking this
                // is what stops an unbalanced bracket INSIDE a literal from corrupting the depth
                // count -- see the remarks above for what that used to do.
                if (inString)
                {
                    // A VERBATIM literal (@"..", $@"..", @$"..") escapes a quote by doubling it and
                    // treats a backslash as an ordinary character. Handling it is what stops
                    // `@(f ? @"C:\" : null)` -- valid C\# -- from eating its own closing quote and
                    // silently dropping the whole attribute.
                    if (verbatim)
                    {
                        if (c == '"' && j + 1 < text.Length && text[j + 1] == '"')
                        {
                            buf.Append("\"\"");
                            j += 2;
                            continue;
                        }

                        if (c == '"') inString = false;
                        buf.Append(c);
                        j++;
                        continue;
                    }

                    if (c == '\\' && j + 1 < text.Length)
                    {
                        buf.Append(c).Append(text[j + 1]);
                        j += 2;
                        continue;
                    }

                    if (c == '"') inString = false;
                    buf.Append(c);
                    j++;
                    continue;
                }

                if (c == '"')
                {
                    // At depth zero this quote closes the attribute; inside an expression it opens
                    // a literal.
                    if (depth == 0) { found = buf.ToString(); break; }

                    inString = true;

                    // The prefix is already in the buffer, so the literal's kind is read back from
                    // the last character or two: @", $@" or @$" is verbatim, a bare " is not.
                    var p1 = buf.Length > 0 ? buf[buf.Length - 1] : '\0';
                    var p2 = buf.Length > 1 ? buf[buf.Length - 2] : '\0';
                    verbatim = p1 == '@' || (p1 == '$' && p2 == '@') || (p1 == '@' && p2 == '$');

                    buf.Append(c);
                    j++;
                    continue;
                }

                if (c == '@' && j + 1 < text.Length && text[j + 1] == '(')
                {
                    depth++;
                    buf.Append("@(");
                    j += 2;
                    continue;
                }

                if (depth > 0 && c == '(') depth++;
                else if (depth > 0 && c == ')') depth--;

                buf.Append(c);
                j++;
            }

            if (found is not null) yield return found;
            i = j + 1;
        }
    }

    /// <summary>
    /// The class names in one <c>class="..."</c> attribute value, including those that appear only
    /// inside a Razor expression.
    /// </summary>
    /// <remarks>
    /// Splitting the raw attribute on spaces misses every name held in a conditional —
    /// <c>class="gen__rail @(open ? "gen__rail-open" : null)"</c> yields the tokens
    /// <c>@(open</c>, <c>?</c> and so on, none of which is a class name, while the real name
    /// <c>gen__rail-open</c> is inside a string literal and never examined. Such a name is invisible
    /// to the element check, and the modifier check only sees it if it carries a <c>--</c>. So the
    /// literals are pulled out and split as well.
    /// <para>
    /// This only works because <see cref="ClassAttributeValues"/> hands over the WHOLE attribute.
    /// The obvious <c>class="([^"]*)"</c> stops at the first inner quote, so it yields
    /// <c>gen__rail @(true ? </c> and the literal falls outside the capture entirely — measured,
    /// by planting such a name and watching this check pass anyway.
    /// </para>
    /// <para>
    /// Zero element-shaped names without a <c>--</c> currently live only inside an expression, so
    /// this closes a hole rather than fixing a visible break. Names carrying <c>--</c> are excluded
    /// by the element pattern and stay the modifier check's business, so nothing is reported twice.
    /// </para>
    /// <para>
    /// <b>The literal regex is not escape-aware, and a name can be lost to that.</b>
    /// <c>"([^"]*)"</c> stops at an escaped <c>\"</c>, so the value
    /// <c>a @(f ? "x\"y b__real" : null)</c> yields the single literal <c>x\</c> and
    /// <c>b__real</c> is never emitted at all. Zero class values in the tree contain a backslash,
    /// <c>@"</c> or <c>$"</c>, so nothing is missed today. It is left as a regex rather than a
    /// second hand-written scanner because the cost of being wrong here is one unexamined name,
    /// where in <see cref="ClassAttributeValues"/> it was a corrupted value or a swallowed file.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> ClassTokens(string attributeValue)
    {
        foreach (var t in attributeValue.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return t;
        }

        if (!attributeValue.Contains('@')) yield break;

        foreach (Match lit in Regex.Matches(attributeValue, @"""(?<lit>[^""]*)"""))
        {
            foreach (var t in lit.Groups["lit"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return t;
            }
        }
    }

    /// <summary>
    /// The companion to the modifier check above, for the other half of the BEM vocabulary.
    /// </summary>
    /// <remarks>
    /// <c>EveryComponentModifierNamedInARazorFile_IsStyledSomewhere</c> only sees names carrying a
    /// <c>--</c> modifier, so a plain element name — <c>conn__mono</c> — could be misspelt, renamed, or simply never given a rule and
    /// nothing would say so.
    /// <para>
    /// Names are read from <c>class="..."</c> attributes AND from BEM-shaped string literals in
    /// <c>.razor</c> and <c>.cs</c> files under Pages/ and Shared/, so a class returned by a helper
    /// or a ternary in an <c>@code</c> block is held to the same rule.
    /// </para>
    /// </remarks>
    private static bool IsBemElement(string name) =>
        Regex.IsMatch(name, @"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:__[a-z0-9]+(?:-[a-z0-9]+)*)+$");

    /// <summary>
    /// Space-separated tokens of every string literal in the component sources, for the names a
    /// <c>class="..."</c> scan cannot see: a helper's return value, a field, a ternary in @code.
    /// Only BEM-shaped tokens are acted on by the caller, so ordinary prose never matches.
    /// </summary>
    private static IEnumerable<(string File, string Token)> CodeLiteralTokens()
    {
        var files = RazorFiles().Concat(ComponentDirectories()
            .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories)));

        foreach (var file in files)
        {
            foreach (Match lit in Regex.Matches(File.ReadAllText(file), @"""(?<lit>[^""\
]*)"""))
            {
                foreach (var t in lit.Groups["lit"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return (file, t);
                }
            }
        }
    }

    [Fact]
    public void EveryComponentElementClassNamedInARazorFile_IsStyledSomewhere()
    {
        var css = AllAuthoredCss();
        var missing = new List<string>();
        var quarantineHit = new HashSet<string>();

        foreach (var file in RazorFiles())
        {
            foreach (var attrValue in ClassAttributeValues(File.ReadAllText(file)))
            {
                foreach (var name in ClassTokens(attrValue))
                {
                    // Element names only. Each hyphen must be followed by a word part, which is what
                    // keeps `--` out: those are the modifier test's, and a name matched by both
                    // would be reported twice with two different explanations.
                    //
                    // The `__` segment REPEATS. With it written once, a two-`__` name was matched by
                    // neither check — the modifier regex requires a `--` and this one anchored at `$`
                    // after a single element part, e.g. `app__brand__text`.
                    if (!IsBemElement(name)) continue;

                    if (KnownUnstyledClasses.Contains(name)) { quarantineHit.Add(name); continue; }

                    if (!Regex.IsMatch(css, @"\." + Regex.Escape(name) + @"(?![\w-])"))
                    {
                        missing.Add($"{Path.GetFileName(file)} uses .{name}");
                    }
                }
            }
        }

        foreach (var (file, name) in CodeLiteralTokens())
        {
            if (!IsBemElement(name)) continue;
            if (KnownUnstyledClasses.Contains(name)) { quarantineHit.Add(name); continue; }

            if (!Regex.IsMatch(css, @"\." + Regex.Escape(name) + @"(?![\w-])"))
            {
                missing.Add($"{Path.GetFileName(file)} uses .{name}");
            }
        }

        Assert.True(missing.Count == 0,
            "A BEM element class nothing styles renders as unstyled markup and no build, test or " +
            "console message reports it — add the rule, or drop the class: " +
            string.Join("; ", missing.Distinct()));

        var stale = KnownUnstyledClasses.Except(quarantineHit).ToList();
        Assert.True(stale.Count == 0,
            "These names are quarantined in KnownUnstyledClasses but no longer need to be — either " +
            "they are now styled or the markup dropped them. Remove them from the list: " +
            string.Join("; ", stale));
    }

    [Fact]
    public void ProgressFillRules_SetBackgroundColorRatherThanTheShorthand()
    {
        var offenders = new List<string>();

        foreach (var file in AuthoredCssFiles())
        {
            var css = File.ReadAllText(file);
            offenders.AddRange(Regex
                .Matches(css, @"(?<selector>[^{}]*\.progress__bar[^{}]*)\{(?<body>[^{}]*)\}")
                .Where(m => Regex.IsMatch(m.Groups["body"].Value, @"(^|[;\s])background\s*:"))
                .Select(m => $"{Path.GetFileName(file)}: {m.Groups["selector"].Value.Trim()}"));
        }

        Assert.True(offenders.Count == 0,
            "The `background` shorthand resets `background-image` to none. `.progress__bar--busy` " +
            "is (0,1,0) and the fill rules out-rank it, so a shorthand there silently wipes the " +
            "stripes that say an operation is still working — measured, and it rendered as nothing " +
            "at all. Use `background-color`: " + string.Join("; ", offenders));
    }
}
