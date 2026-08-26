using System;
using System.Collections.Generic;

namespace TieriChallengesFFXIV;

/// <summary>
/// A search box that waits for the typing to stop before it searches.
///
/// <para><b>Why debounce at all.</b> Matching runs over every challenge's title, description and
/// hint, and the list rebuilds from scratch every frame. Filtering on each keystroke means
/// re-running the whole match while the player is mid-word, and — worse — the list visibly
/// thrashes as partial words match and unmatch. Waiting for a pause means one search per intent
/// rather than one per character.</para>
///
/// <para>The <see cref="Raw"/> text updates immediately so the box never feels laggy; only
/// <see cref="Active"/>, the term the list actually filters by, waits.</para>
/// </summary>
internal sealed class DebouncedSearch
{
    /// <summary>How long the typing has to stop for. Trist's spec: at least a second.</summary>
    private const long DelayMs = 1000;

    private string _raw    = string.Empty;
    private string _active = string.Empty;
    private long   _changedAtMs;
    private bool   _pending;

    /// <summary>What is in the box right now. Echoes every keystroke.</summary>
    public string Raw => _raw;

    /// <summary>What the list is filtering by. Lags <see cref="Raw"/> until typing stops.</summary>
    public string Active => _active;

    /// <summary>True while a change is waiting out the delay — the box shows a "…" cue.</summary>
    public bool IsWaiting => _pending;

    public bool HasTerm => !string.IsNullOrWhiteSpace(_active);

    public void SetText(string value)
    {
        value ??= string.Empty;
        if (string.Equals(value, _raw, StringComparison.Ordinal)) return;

        _raw         = value;
        _changedAtMs = Environment.TickCount64;
        _pending     = true;

        // Clearing the box is not a search, it is a cancel — and waiting a second to show
        // everything again reads as the control being broken. Applied immediately.
        if (string.IsNullOrWhiteSpace(value))
        {
            _active  = string.Empty;
            _pending = false;
        }
    }

    /// <summary>Call once per frame. Promotes the pending term once the delay has elapsed.</summary>
    public void Tick()
    {
        if (!_pending) return;
        if (Environment.TickCount64 - _changedAtMs < DelayMs) return;

        _active  = _raw.Trim();
        _pending = false;
    }

    public void Clear()
    {
        _raw     = string.Empty;
        _active  = string.Empty;
        _pending = false;
    }

    /// <summary>
    /// Does any of this text match the active term? Case-insensitive substring across every field
    /// given — a player searching "chocobo" should find it whether it is in the name, the
    /// description or the hint.
    /// </summary>
    public bool Matches(params string?[] fields)
    {
        if (!HasTerm) return true;

        foreach (string? f in fields)
        {
            if (!string.IsNullOrEmpty(f)
                && f.Contains(_active, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Which kinds of challenge the list is showing.
///
/// <para>Held as a set of things to HIDE rather than to show, so a filter written before a new
/// challenge shape existed cannot silently hide it — an unknown kind is visible by default, which
/// is the safe direction for a control the player may have set months ago and forgotten.</para>
/// </summary>
public enum ChallengeFilterFlag
{
    /// <summary>Challenges already finished.</summary>
    Completed,

    /// <summary>Not yet finished.</summary>
    Incomplete,

    /// <summary>Quest chains.</summary>
    Quests,

    /// <summary>Multi-objective adventures.</summary>
    Adventures,

    /// <summary>Timed races.</summary>
    Races,

    /// <summary>Ordinary single-objective challenges.</summary>
    Standard,

    /// <summary>Challenges not in the official catalogue.</summary>
    Custom,
}

/// <summary>Decides whether a row survives the filter menu. Pure — no UI, no state of its own.</summary>
internal static class ChallengeFilter
{
    public static readonly (ChallengeFilterFlag Flag, string Label)[] All =
    {
        (ChallengeFilterFlag.Incomplete, "Not done yet"),
        (ChallengeFilterFlag.Completed,  "Completed"),
        (ChallengeFilterFlag.Standard,   "Standard challenges"),
        (ChallengeFilterFlag.Quests,     "Quests"),
        (ChallengeFilterFlag.Adventures, "Adventures"),
        (ChallengeFilterFlag.Races,      "Races"),
        (ChallengeFilterFlag.Custom,     "Custom (not official)"),
    };

    public static bool IsHidden(Configuration cfg, ChallengeFilterFlag flag) =>
        cfg.HiddenFilters != null && cfg.HiddenFilters.Contains(flag.ToString());

    public static void Toggle(Configuration cfg, ChallengeFilterFlag flag)
    {
        cfg.HiddenFilters ??= new List<string>();

        string key = flag.ToString();
        if (!cfg.HiddenFilters.Remove(key)) cfg.HiddenFilters.Add(key);
    }

    public static void ShowAll(Configuration cfg) => cfg.HiddenFilters?.Clear();

    public static bool AnyHidden(Configuration cfg) => cfg.HiddenFilters is { Count: > 0 };

    /// <summary>
    /// Should this challenge be shown? Every clause is "hidden by an explicit toggle", so the
    /// default state — nothing toggled — shows everything.
    /// </summary>
    public static bool Passes(Configuration cfg, ChallengeDef def, bool done)
    {
        if (cfg.HiddenFilters is not { Count: > 0 }) return true;

        if (done  && IsHidden(cfg, ChallengeFilterFlag.Completed))  return false;
        if (!done && IsHidden(cfg, ChallengeFilterFlag.Incomplete)) return false;

        if (!def.IsOfficial && IsHidden(cfg, ChallengeFilterFlag.Custom)) return false;

        // Shape. A race is a race whatever its theme, so it is tested first.
        if (def.Kind == ChallengeKind.RaceTimer)
            return !IsHidden(cfg, ChallengeFilterFlag.Races);

        return def.Theme switch
        {
            ChallengeTheme.Quest     => !IsHidden(cfg, ChallengeFilterFlag.Quests),
            ChallengeTheme.Adventure => !IsHidden(cfg, ChallengeFilterFlag.Adventures),
            _                        => !IsHidden(cfg, ChallengeFilterFlag.Standard),
        };
    }
}
