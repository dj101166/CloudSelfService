using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using Markdig;

namespace CloudSelfService.Services;

/// <summary>One released version, as the release-notes dialog renders it.</summary>
/// <param name="Version">The version string, without the surrounding brackets.</param>
/// <param name="Date">The release date exactly as written, or <see langword="null"/> when the heading carried none.</param>
/// <param name="HtmlBody">That version's sections and bullets, rendered to HTML.</param>
public sealed record ChangelogEntry(string Version, string? Date, string HtmlBody);

/// <summary>Reads <c>CHANGELOG.md</c> out of the assembly and parses it into versioned entries.</summary>
/// <remarks>
/// The changelog is embedded rather than read from disk, so the release notes the version chip
/// opens and the <c>CHANGELOG.md</c> a reviewer reads in the repository are one file and cannot
/// disagree about what shipped. Releasing means adding an entry
/// here and nowhere else — <see cref="CurrentVersion"/> is the top entry's version, so the chip
/// cannot advertise a version the notes have no entry for.
/// <para>
/// <b>This class never throws.</b> The version chip lives in the layout and therefore renders on
/// every page — so a missing resource or a changelog whose headings have drifted yields no
/// entries, the chip simply does not render, and nothing else is affected. A failure to read
/// release notes must never take down the application the notes describe.
/// </para>
/// </remarks>
public static class ChangelogDocument
{
    /// <summary>The embedded resource name, set by the <c>EmbeddedResource</c> item in the csproj.</summary>
    private const string ResourceName = "CloudSelfService.Docs.CHANGELOG.md";

    /// <summary>
    /// Matches a version heading such as <c>## [1.0.13] - August 6, 2026</c>, with the date optional.
    /// </summary>
    /// <remarks>
    /// Anchored to the start of a line so a bracketed reference inside prose cannot be mistaken for
    /// the start of a new release. Every gap is <c>[^\S\n]</c> — horizontal whitespace only — which
    /// keeps the whole match on the heading's own line: with a plain <c>\s*</c> the optional date
    /// part reaches across the blank line below and swallows the release's first bullet as its date,
    /// because a bullet opens with the same hyphen the date separator uses.
    /// </remarks>
    private static readonly Regex VersionHeading = new(
        @"^##[^\S\n]+\[(?<version>[^\]]+)\][^\S\n]*(?:[-–—][^\S\n]*(?<date>[^\n]+?))?[^\S\n]*$",
        RegexOptions.Multiline | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // PublicationOnly so a transient failure is retried on the next read. The default
    // ExecutionAndPublication mode caches the EXCEPTION, which would poison the version
    // chip - rendered on every page - for the whole process lifetime.
    private static readonly Lazy<IReadOnlyList<ChangelogEntry>> Parsed =
        new(() => ParseOrEmpty(ReadSource()), LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Every released version, newest first, in the order the file lists them.</summary>
    public static IReadOnlyList<ChangelogEntry> Entries => Parsed.Value;

    /// <summary>The version the chip shows, or <see langword="null"/> when there is nothing to show.</summary>
    public static string? CurrentVersion => CurrentVersionOf(Entries);

    /// <summary>Picks the version to display from a parsed set of entries.</summary>
    /// <param name="entries">The parsed entries.</param>
    /// <returns>The first entry's version, or <see langword="null"/> when there are none.</returns>
    /// <remarks>Separate from <see cref="CurrentVersion"/> so the rule can be tested without the assembly.</remarks>
    internal static string? CurrentVersionOf(IReadOnlyList<ChangelogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries.Count == 0 ? null : entries[0].Version;
    }

    /// <summary>Parses changelog markdown into entries.</summary>
    /// <param name="markdown">The changelog source.</param>
    /// <returns>One entry per version heading found, in file order; empty when none is.</returns>
    /// <remarks>
    /// Each entry's body is handed to Markdig whole rather than being picked apart here, which is
    /// what lets the <c>### 🔄 Changed</c> style headings render without this parser knowing the
    /// section names — a new heading needs no code change.
    /// </remarks>
    internal static IReadOnlyList<ChangelogEntry> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        MatchCollection headings = VersionHeading.Matches(markdown);

        if (headings.Count == 0)
        {
            return [];
        }

        List<ChangelogEntry> entries = new(headings.Count);

        for (int i = 0; i < headings.Count; i++)
        {
            Match heading = headings[i];

            // The body runs from the end of this heading to the start of the next one, so a version
            // never absorbs the release below it.
            int bodyStart = heading.Index + heading.Length;
            int bodyEnd = i + 1 < headings.Count ? headings[i + 1].Index : markdown.Length;

            string body = markdown[bodyStart..bodyEnd];
            string? date = heading.Groups["date"].Success ? heading.Groups["date"].Value.Trim() : null;

            entries.Add(new ChangelogEntry(
                heading.Groups["version"].Value.Trim(),
                string.IsNullOrWhiteSpace(date) ? null : date,
                Markdown.ToHtml(body, Pipeline)));
        }

        return entries;
    }

    /// <summary>
    /// Parses the changelog, returning an empty list rather than throwing. The version chip is
    /// in the layout, on every page, so this must never be the reason a page fails to render.
    /// </summary>
    /// <param name="source">The changelog source.</param>
    /// <returns>The parsed entries, or an empty list when parsing fails for any reason.</returns>
    internal static IReadOnlyList<ChangelogEntry> ParseOrEmpty(string source)
        => ParseOrEmpty(source, Parse);

    /// <summary>
    /// Parses the changelog with an injected parser, returning an empty list rather than throwing.
    /// </summary>
    /// <remarks>
    /// The parser is a parameter purely so the catch can be tested. <see cref="Parse"/> is very hard
    /// to make throw - malformed markdown simply fails to match the heading regex and yields no
    /// entries - so a test that feeds bad input proves nothing about this catch. Injecting a parser
    /// that throws is the only way to see the guard work.
    /// </remarks>
    /// <param name="source">The changelog source.</param>
    /// <param name="parse">The parsing function to invoke.</param>
    /// <returns>The parsed entries, or an empty list when parsing fails for any reason.</returns>
    internal static IReadOnlyList<ChangelogEntry> ParseOrEmpty(
        string source, Func<string?, IReadOnlyList<ChangelogEntry>> parse)
    {
        try
        {
            return parse(source);
        }
        catch
        {
            return Array.Empty<ChangelogEntry>();
        }
    }

    /// <summary>The Markdig pipeline used for entry bodies.</summary>
    /// <remarks>
    /// Deliberately plain. The changelog is written by this team and lives in the assembly, so it
    /// needs no sanitising and no extensions beyond CommonMark.
    /// </remarks>
    private static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder().Build();

    /// <summary>Reads the embedded changelog, returning empty rather than throwing when it cannot.</summary>
    /// <returns>The changelog source, or an empty string.</returns>
    private static string ReadSource()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                return string.Empty;
            }

            using StreamReader reader = new(stream, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            // Swallowed on purpose, and deliberately broad. See the class remarks: the chip is on
            // every page, so an unreadable changelog degrades to no chip rather than to an unusable
            // application. This caught only IOException until 2026-10-07, which left
            // BadImageFormatException and NotImplementedException from GetManifestResourceStream
            // able to take down every page in the app.
            return string.Empty;
        }
    }
}
