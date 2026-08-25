using System;
using System.Collections.Generic;

namespace TieriChallengesFFXIV;

/// <summary>
/// What KIND of thing a challenge is, from the player's point of view — and the colour it wears.
///
/// <para><b>Derived, never authored.</b> There is no theme field on a challenge. A challenge is
/// blue BECAUSE it has chain steps and green BECAUSE it has several objectives; the colour is a
/// readout of the structure, not a decoration applied on top of it. A picked theme could disagree
/// with the mechanics — a yellow challenge that behaves like a chain, a blue one with nothing to
/// chain — and every such combination would be a bug that looked deliberate.</para>
/// </summary>
public enum ChallengeTheme
{
    /// <summary>Gold. One objective, done or not.</summary>
    Normal = 0,

    /// <summary>Blue. A chain of steps, each replacing the last; only the whole chain completes.</summary>
    Quest = 1,

    /// <summary>Green. One challenge with several objectives, in any order or a fixed one.</summary>
    Adventure = 2,
}

/// <summary>
/// One step of a <see cref="ChallengeTheme.Quest"/> chain.
///
/// <para>A step carries its own name, description, hint and ZONE, because a chain that stays in
/// one place is barely a chain — the interesting ones walk the player around. The zone is what
/// makes the chain re-file itself under wherever it currently points; see
/// <c>ZoneIndex.TerritoryOf</c>.</para>
///
/// <para><b>A step has its own GUID but never enters the completion ledger.</b> Only the CHAIN's
/// GUID is ever recorded as complete, which is what keeps overall progress honest: a five-step
/// chain is one challenge out of the total, not five, and finishing a step must not make the
/// headline percentage lurch. The step id exists so progress can name which step is current
/// without depending on its position, and so reordering steps mid-authoring does not silently
/// shift a player forward or back.</para>
/// </summary>
[Serializable]
public sealed class ChainStep
{
    public string Id { get; set; } = string.Empty;

    public string Title  { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Hint   { get; set; } = string.Empty;

    /// <summary>This step's zone. 0 falls back to the chain's own territory.</summary>
    public ushort TerritoryId   { get; set; }
    public string TerritoryName { get; set; } = string.Empty;

    /// <summary>How this step's areas are satisfied, exactly as a composite challenge's are.</summary>
    public AreaMode Mode { get; set; } = AreaMode.Single;

    /// <summary>The step's stops. Same shape and same evaluator as <c>CustomChallenge.Requirements</c>.</summary>
    public List<AreaRequirement> Requirements { get; set; } = new();

    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

    /// <summary>
    /// Everything the tracker needs. Same rules as a composite challenge, for the same reasons —
    /// a half-authored step would silently never fire and stall the whole chain behind it.
    /// </summary>
    public bool IsWellFormed()
    {
        if (Requirements == null || Requirements.Count == 0) return false;
        if (Mode == AreaMode.Single && Requirements.Count != 1) return false;

        foreach (var r in Requirements)
            if (r == null || !r.IsWellFormed()) return false;

        return true;
    }

    public ChainStep Clone()
    {
        var s = new ChainStep
        {
            Id = Id, Title = Title, Detail = Detail, Hint = Hint,
            TerritoryId = TerritoryId, TerritoryName = TerritoryName,
            Mode = Mode,
        };

        if (Requirements != null)
            foreach (var r in Requirements) s.Requirements.Add(r.Clone());

        return s;
    }
}
