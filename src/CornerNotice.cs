using System;

namespace TieriChallengesFFXIV;

/// <summary>
/// One thing worth saying in the bottom-right corner.
///
/// <para>The corner popup started life showing only step progress, so it took a
/// <see cref="ProgressEvent"/> directly. Zone arrival needs the same popup with different words
/// and no progress bar, so the renderers now take this instead — a small view model rather than a
/// domain event. Adding a third kind of notice means adding a factory here and nothing else.</para>
///
/// <para>References no PanacheUI type; both renderers share it.</para>
/// </summary>
public sealed class CornerNotice
{
    /// <summary>Headline. A challenge name, or a summary like "3 challenges here".</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Second line, in the accent colour.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Progress bar. Suppressed for notices where a fraction would be meaningless.</summary>
    public bool  ShowBar  { get; init; }
    public float Fraction { get; init; }

    /// <summary>Label on the button.</summary>
    public string ActionLabel { get; init; } = "Show";

    /// <summary>
    /// What the button reveals. Null opens the window without selecting anything, which is right
    /// for a zone notice covering several challenges.
    /// </summary>
    public string? ChallengeId { get; init; }
    public string? Category    { get; init; }

    /// <summary>A step of a multi-step challenge landed.</summary>
    public static CornerNotice ForProgress(ProgressEvent e)
    {
        string title = string.IsNullOrWhiteSpace(e.Title) ? "(unnamed challenge)" : e.Title;

        return new CornerNotice
        {
            Title       = title,
            Detail      = $"Objective  {e.Done}/{e.Total}",
            ShowBar     = true,
            Fraction    = e.Total > 0 ? Math.Clamp(e.Done / (float)e.Total, 0f, 1f) : 0f,
            ChallengeId = e.Id,
            Category    = e.Category,
        };
    }

    /// <summary>
    /// The player arrived somewhere with challenges still open.
    ///
    /// <para>No progress bar: the count is "how many are available here", not a fraction of
    /// anything, and drawing an empty bar under it would read as "0% done".</para>
    /// </summary>
    public static CornerNotice ForZone(string zoneName, int count, string? soleId, string? soleCategory)
        => new()
        {
            Title       = count == 1 ? "A challenge is available here"
                                     : $"{count} challenges are available here",
            Detail      = string.IsNullOrWhiteSpace(zoneName) ? "This zone" : zoneName,
            ShowBar     = false,
            ActionLabel = "Open",

            // Only preselect when there is exactly one; picking one arbitrarily out of several
            // would highlight a challenge the player never asked about.
            ChallengeId = count == 1 ? soleId : null,
            Category    = count == 1 ? soleCategory : null,
        };
}
