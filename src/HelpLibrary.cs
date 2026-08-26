using System;
using System.Collections.Generic;
using System.IO;

namespace TieriChallengesFFXIV;

/// <summary>One paragraph of a help section. Bullets are drawn indented with a marker.</summary>
public sealed record HelpBlock(string Text, bool IsBullet);

/// <summary>
/// One searchable, jump-to-able entry in the help document.
/// </summary>
public sealed class HelpSection
{
    public string Category { get; init; } = string.Empty;
    public string Title    { get; init; } = string.Empty;

    public List<HelpBlock> Blocks { get; } = new();

    /// <summary>
    /// Extra search terms that are never displayed.
    ///
    /// <para>The point of the whole feature: a player looking for the difficulty stars might type
    /// "filled in", "solid", "shape" or "how hard" — none of which appear in the section's visible
    /// text. Without these the help is only findable by someone who already knows what we called
    /// the thing, which is precisely the person who does not need help.</para>
    /// </summary>
    public List<string> Keywords { get; } = new();

    /// <summary>Everything searchable, lowercased once at load so matching is a plain Contains.</summary>
    public string SearchBlob { get; private set; } = string.Empty;

    /// <summary>First paragraph, trimmed — the one-line preview under a search result.</summary>
    public string Summary { get; private set; } = string.Empty;

    internal void Freeze()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Category).Append('\n').Append(Title).Append('\n');

        foreach (var b in Blocks) sb.Append(b.Text).Append('\n');
        foreach (var k in Keywords) sb.Append(k).Append('\n');

        SearchBlob = sb.ToString().ToLowerInvariant();

        foreach (var b in Blocks)
        {
            if (b.IsBullet || string.IsNullOrWhiteSpace(b.Text)) continue;
            Summary = b.Text.Length <= 120 ? b.Text : b.Text.Substring(0, 117).TrimEnd() + "…";
            break;
        }
    }

    public bool Matches(string lowerTerm) =>
        string.IsNullOrEmpty(lowerTerm) || SearchBlob.Contains(lowerTerm, StringComparison.Ordinal);
}

/// <summary>
/// Loads and parses <c>docs/HELP.md</c>, shipped beside the DLL.
///
/// <para><b>A document, not a resource.</b> The help lives as an ordinary Markdown file for the
/// same reason the background images and cue audio do: it can be read, reviewed and diffed in the
/// repo, and it is legible to somebody browsing the project on GitHub. The hidden keywords are
/// HTML comments, so they are invisible there as well as here.</para>
///
/// <para><b>The format is minimal on purpose.</b> Headings, paragraphs, bullets, keyword comments.
/// Anything richer would need a Markdown renderer, and the value of this feature is the search and
/// the keywords, not typography.</para>
/// </summary>
internal static class HelpLibrary
{
    private const string FileName = "HELP.md";

    private static List<HelpSection>? _sections;
    private static string _error = string.Empty;

    /// <summary>Why the help could not be loaded, or empty. Shown in the window rather than swallowed.</summary>
    public static string Error => _error;

    public static IReadOnlyList<HelpSection> Sections => _sections ??= Load();

    /// <summary>Drop the parsed copy so the next read re-parses. Dev aid while writing the document.</summary>
    public static void Reload() => _sections = null;

    /// <summary>Where the shipped document lives — beside the DLL, like the icons and sounds.</summary>
    public static string PathOnDisk
    {
        get
        {
            string dir = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty;
            return Path.Combine(dir, FileName);
        }
    }

    private static List<HelpSection> Load()
    {
        var result = new List<HelpSection>();
        _error = string.Empty;

        try
        {
            string path = PathOnDisk;
            if (!File.Exists(path))
            {
                _error = $"The help document is missing.\n\nExpected it at:\n{path}";
                Plugin.Log.Warning($"[Help] {FileName} not found at {path}");
                return result;
            }

            Parse(File.ReadAllLines(path), result);

            if (result.Count == 0)
                _error = "The help document was found but contains no sections.";

            Plugin.Log.Information($"[Help] loaded {result.Count} section(s) from {FileName}.");
        }
        catch (Exception ex)
        {
            _error = "The help document could not be read.\n\n" + ex.Message;
            Plugin.Log.Error(ex, "[Help] failed to load");
        }

        return result;
    }

    private static void Parse(string[] lines, List<HelpSection> into)
    {
        string category = string.Empty;
        HelpSection? current = null;

        // Paragraphs are accumulated across lines and flushed on a blank line, so the document can
        // be hard-wrapped for readability without every wrapped line becoming its own paragraph.
        var paragraph = new System.Text.StringBuilder();
        bool paragraphIsBullet = false;

        void FlushParagraph()
        {
            if (current != null && paragraph.Length > 0)
                current.Blocks.Add(new HelpBlock(paragraph.ToString().Trim(), paragraphIsBullet));

            paragraph.Clear();
            paragraphIsBullet = false;
        }

        void FlushSection()
        {
            FlushParagraph();
            if (current != null) { current.Freeze(); into.Add(current); }
            current = null;
        }

        bool inComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw  = lines[i];
            string line = raw.Trim();

            // ── HTML comments ────────────────────────────────────────────────
            // The keyword lines and the format note at the top of the file are both comments, so
            // this has to handle a multi-line one as well as the single-line keyword form.
            if (inComment)
            {
                if (line.Contains("-->", StringComparison.Ordinal)) inComment = false;
                continue;
            }

            if (line.StartsWith("<!--", StringComparison.Ordinal))
            {
                string inner = line;

                if (line.Contains("-->", StringComparison.Ordinal))
                {
                    int start = line.IndexOf("<!--", StringComparison.Ordinal) + 4;
                    int end   = line.IndexOf("-->", StringComparison.Ordinal);
                    inner = end > start ? line.Substring(start, end - start).Trim() : string.Empty;
                }
                else
                {
                    inComment = true;
                    inner = string.Empty;   // a multi-line comment is the format note, not keywords
                }

                const string marker = "keywords:";
                if (current != null && inner.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string k in inner.Substring(marker.Length).Split(','))
                    {
                        string term = k.Trim();
                        if (term.Length > 0) current.Keywords.Add(term);
                    }
                }

                continue;
            }

            // ── Headings ─────────────────────────────────────────────────────
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushSection();
                current = new HelpSection { Category = category, Title = line.Substring(3).Trim() };
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushSection();
                category = line.Substring(2).Trim();
                continue;
            }

            // ── Body ─────────────────────────────────────────────────────────
            if (line.Length == 0) { FlushParagraph(); continue; }

            if (current == null) continue;   // preamble before the first section

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                paragraphIsBullet = true;
                paragraph.Append(line.Substring(2).Trim());
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(Clean(line));
        }

        FlushSection();
    }

    /// <summary>
    /// Strip the Markdown emphasis the document uses for readability in a text editor. The window
    /// draws plain text, so leaving the asterisks in would show them literally.
    /// </summary>
    private static string Clean(string s) =>
        s.Replace("**", string.Empty, StringComparison.Ordinal)
         .Replace("`",  string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Sections matching a search term, best first.
    ///
    /// <para>A title hit outranks a body or keyword hit: someone typing "sound" wants the sound
    /// section, not every section that happens to mention a sound.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Three tiers, best first.</b> A title hit outranks a phrase hit, which outranks an
    /// all-words hit: someone typing "sound" wants the sound section, not every section that
    /// mentions a noise.</para>
    ///
    /// <para><b>The all-words tier is what makes loose descriptions work.</b> A plain substring
    /// test finds nothing for "turn off sound" — no single string contains that phrase — even
    /// though the sound section carries both "turn off" and "sound". Since the whole point of this
    /// page is to be findable by someone who cannot name the thing, and such a person types a
    /// phrase rather than a keyword, matching every word separately is the tier that earns its
    /// keep.</para>
    /// </remarks>
    public static List<HelpSection> Search(string term)
    {
        var all = Sections;

        if (string.IsNullOrWhiteSpace(term)) return new List<HelpSection>(all);

        string lower = term.Trim().ToLowerInvariant();
        string[] words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var titleHits  = new List<HelpSection>();
        var phraseHits = new List<HelpSection>();
        var wordHits   = new List<(HelpSection Section, int Score)>();

        foreach (var s in all)
        {
            if (s.Title.Contains(lower, StringComparison.OrdinalIgnoreCase)) { titleHits.Add(s); continue; }
            if (s.Matches(lower))                                           { phraseHits.Add(s); continue; }

            if (words.Length > 1)
            {
                int score = WordScore(s, words);
                if (score > 0) wordHits.Add((s, score));
            }
        }

        // Most words matched first. A question like "how do i change colours" will not have every
        // word anywhere — "how" and "do" are not in the colours section — so demanding all of them
        // finds nothing at all, which is the failure mode this page exists to avoid. Ranking by
        // how much of the question landed puts the right section on top and leaves the near
        // misses below it rather than discarding them.
        wordHits.Sort((a, b) => b.Score.CompareTo(a.Score));

        titleHits.AddRange(phraseHits);
        foreach (var (section, _) in wordHits) titleHits.Add(section);
        return titleHits;
    }

    /// <summary>
    /// How many of the query's meaningful words appear in this section. 0 means "not worth
    /// showing" — either nothing matched, or so little did that it would be noise.
    /// </summary>
    private static int WordScore(HelpSection s, string[] words)
    {
        int meaningful = 0;
        int hits       = 0;

        foreach (string w in words)
        {
            // Filler is skipped rather than scored. "how", "the", "my", "is" and "do" appear in
            // nearly every section, so counting them would flatten every score toward the same
            // number and destroy the ranking they were supposed to help.
            if (w.Length < 3 || Filler.Contains(w)) continue;

            meaningful++;
            if (s.SearchBlob.Contains(w, StringComparison.Ordinal)) hits++;
        }

        if (meaningful == 0 || hits == 0) return 0;

        // At least half of the real words have to land. Below that a single common word like
        // "challenge" would match everything and the result list would be the whole document.
        return hits * 2 >= meaningful ? hits : 0;
    }

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "how", "the", "and", "for", "you", "your", "are", "was", "can", "does", "did",
        "what", "why", "when", "where", "who", "this", "that", "these", "those",
        "with", "from", "into", "out", "get", "got", "put", "make", "made", "have", "has",
        "not", "but", "all", "any", "one", "its", "it's", "there", "then", "than",
        "some", "just", "very", "want", "need", "please", "help",
    };
}
