using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

using CloudSelfService.Services;

namespace CloudSelfService.Tests;

/// <summary>
/// Covers the parsing of <c>CHANGELOG.md</c> into the entries the release-notes dialog renders.
/// </summary>
/// <remarks>
/// Two properties matter more than the rest and are asserted deliberately. The first is that the
/// version chip and the notes it opens read from one file, so they cannot disagree about what
/// shipped — <see cref="ChangelogDocument.CurrentVersion"/> is the top entry's version and nothing
/// else. The second is degradation: the chip lives in the layout and therefore renders on every
/// page, so a missing or malformed changelog must yield no entries rather than an exception. A
/// failure to read release notes must never take down the application the notes describe.
/// </remarks>
public class ChangelogDocumentTests
{
    private const string TwoEntries = """
        # Changelog

        ## [1.1.0] - September 16, 2026

        ### ✨ Added

        - **A second thing.** Explanation of the second thing.

        ## [1.0.0] - September 1, 2026

        ### ✨ Added

        - **The first thing.** Explanation of the first thing.

        ### 🐛 Fixed

        - **Something that was wrong.** And is no longer.
        """;

    [Fact]
    public void Parse_ReadsEveryEntryInFileOrder()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogDocument.Parse(TwoEntries);

        Assert.Equal(2, entries.Count);
        Assert.Equal("1.1.0", entries[0].Version);
        Assert.Equal("1.0.0", entries[1].Version);
    }

    [Fact]
    public void Parse_ExtractsTheReleaseDate()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogDocument.Parse(TwoEntries);

        Assert.Equal("September 16, 2026", entries[0].Date);
        Assert.Equal("September 1, 2026", entries[1].Date);
    }

    [Fact]
    public void Parse_RendersEachEntryBodyAsHtml()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogDocument.Parse(TwoEntries);

        // The section headings and their bullets come from Markdig rather than a bespoke renderer,
        // which is what keeps the emoji headers working without this class knowing they exist.
        Assert.Contains("Added", entries[0].HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<li>", entries[0].HtmlBody, StringComparison.Ordinal);
        Assert.Contains("second thing", entries[0].HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_KeepsEachEntrysBodyToItself()
    {
        // A version section must not absorb the next one, or the newest release would appear to
        // contain the whole history.
        IReadOnlyList<ChangelogEntry> entries = ChangelogDocument.Parse(TwoEntries);

        Assert.DoesNotContain("first thing", entries[0].HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("second thing", entries[1].HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EntryWithSeveralSections_KeepsThemAll()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogDocument.Parse(TwoEntries);

        Assert.Contains("Added", entries[1].HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Fixed", entries[1].HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EntryWithoutADate_StillYieldsTheVersion()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogDocument.Parse("## [2.0.0]\n\n- something");

        Assert.Single(entries);
        Assert.Equal("2.0.0", entries[0].Version);
        Assert.Null(entries[0].Date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# Changelog\n\nNothing has shipped yet.")]
    [InlineData("## Not a version heading\n\n- text")]
    public void Parse_NothingRecognisable_YieldsNoEntriesRatherThanThrowing(string markdown)
    {
        // Degradation, not an exception: the chip renders on every page.
        Assert.Empty(ChangelogDocument.Parse(markdown));
    }

    [Fact]
    public void CurrentVersion_IsTheTopEntrysVersion()
    {
        Assert.Equal("1.1.0", ChangelogDocument.CurrentVersionOf(ChangelogDocument.Parse(TwoEntries)));
    }

    [Fact]
    public void CurrentVersion_WithNoEntries_IsNull()
    {
        Assert.Null(ChangelogDocument.CurrentVersionOf(ChangelogDocument.Parse("")));
    }

    [Fact]
    public void ReadSource_SwallowsAnyException_NotJustIOException()
    {
        // The chip renders on EVERY page. The class comment says an unreadable changelog must
        // degrade to no chip rather than an unusable app - but the catch only honoured that for
        // one exception type. GetManifestResourceStream can throw BadImageFormatException or
        // NotImplementedException; Parse can throw on malformed input.
        var ex = Record.Exception(() => ChangelogDocument.ParseOrEmpty("## [not a valid heading\n\u0000\uFFFF"));
        Assert.Null(ex);
    }

    [Fact]
    public void ParseOrEmpty_SwallowsAnythingTheParserThrows()
    {
        // The catch exists so an unreadable changelog degrades to no version chip rather than
        // taking down every page. Proven by INJECTING the throw: the test above feeds malformed
        // markdown, which Parse never actually throws on, so the catch could be deleted outright
        // and the suite stayed green.
        var entries = ChangelogDocument.ParseOrEmpty("anything", _ => throw new InvalidOperationException("boom"));

        Assert.Empty(entries);
    }

    [Fact]
    public void AFailedReadIsNotCachedForever()
    {
        // Lazy<T> with the default ExecutionAndPublication mode caches the EXCEPTION, so one
        // transient failure poisons the version chip for the whole process lifetime. PublicationOnly
        // retries instead.
        //
        // Pinned against the source text rather than by reflecting on Lazy's non-public Mode
        // property, because Mode reads back as null once the value has been created - and other
        // tests in this assembly read ChangelogDocument.Entries, so a reflective assertion would
        // pass or vacuously read null depending on test order. The static field is also created
        // once per process, so the retry cannot be exercised directly on it. Reading the
        // declaration is the one check that cannot silently stop meaning anything.
        string source = ChangelogDocumentSource();

        Assert.Contains("LazyThreadSafetyMode.PublicationOnly", source, StringComparison.Ordinal);

        // And the broad catch that the mode change is worthless without.
        Assert.Contains("catch (Exception)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (IOException)", source, StringComparison.Ordinal);

        // Behaviour, as far as a once-per-process static allows: reading twice never throws.
        Assert.Null(Record.Exception(() => ChangelogDocument.Entries));
        Assert.Null(Record.Exception(() => ChangelogDocument.Entries));
    }

    /// <summary>Finds the repository's own <c>ChangelogDocument.cs</c> from the test assembly's location.</summary>
    /// <returns>The file's contents.</returns>
    /// <remarks>
    /// Walks up from the test binary rather than taking a relative path, so the test survives being
    /// run from the repository root, from the test project, or from an IDE with its own working
    /// directory. <c>App.razor</c> is the anchor because it is the one file guaranteed to sit at
    /// the app root and nowhere else on the way up - the same anchor the other guard suites use.
    /// </remarks>
    private static string ChangelogDocumentSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "App.razor")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(
            Path.Combine(directory.FullName, "Services", "ChangelogDocument.cs"));
    }

    [Fact]
    public void TheRealEmbeddedChangelogParses()
    {
        // Guards the wiring as well as the parser: this fails if the EmbeddedResource is dropped
        // from the csproj, if its LogicalName changes, or if CHANGELOG.md's headings drift from the
        // shape the dialog expects.
        Assert.NotEmpty(ChangelogDocument.Entries);
        Assert.False(string.IsNullOrWhiteSpace(ChangelogDocument.CurrentVersion));
        Assert.NotNull(ChangelogDocument.Entries[0].Date);
    }
}
