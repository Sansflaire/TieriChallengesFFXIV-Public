using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// The full requirement sheet for a quest chain or an adventure: every step or objective, what it
/// asks for, and which are already done.
///
/// <para><b>Why this exists.</b> A challenge row has one line of description. That is enough for
/// "ride a Fat Chocobo in the Lavender Beds" and hopeless for a five-step quest or a nine-objective
/// adventure — the row can only ever show the leg the player is on, so without this window the
/// shape of the whole thing is invisible.</para>
///
/// <para><b>Raw ImGui, themed.</b> Permitted by DESIGN_SYSTEM §10 anti-pattern 8 (standard popups),
/// and required here because the content is an arbitrarily long scrolling list of mixed text that
/// PanacheUI has no table or list primitive for. It pushes <see cref="DialogTheme"/> so it matches
/// the main window, per the standing rule that every player-facing surface does.</para>
///
/// <para><b>Spoilers.</b> A chain's later steps are NOT listed while unreached — the whole point of
/// a chain is that step three is a surprise until step two lands. An adventure's objectives ARE all
/// listed, because they have no order and the player is meant to plan a route through them.</para>
/// </summary>
internal sealed class ObjectiveWindow
{
    private readonly Configuration    _config;
    private readonly CompletionStore  _store;
    private readonly ChallengeTracker _tracker;

    private string? _openId;

    // Properties, not fields: DialogTheme now reads the user-recolourable Palette, and a
    // `static readonly` copy would freeze at class-init and never see a colour change.
    private static Vector4 Gold   => DialogTheme.Accent;
    private static Vector4 Green  => DialogTheme.StatusOk;
    private static Vector4 Blue   => Palette.Vec(PaletteSlot.Quest);
    private static Vector4 Muted  => DialogTheme.TextMuted;

    public ObjectiveWindow(Configuration config, CompletionStore store, ChallengeTracker tracker)
    {
        _config  = config;
        _store   = store;
        _tracker = tracker;
    }

    public bool IsOpen => _openId != null;

    public void Open(string challengeId) => _openId = challengeId;
    public void Close()                  => _openId = null;

    /// <summary>Toggle — the row's button calls this, so pressing it twice closes the sheet.</summary>
    public void Toggle(string challengeId)
        => _openId = string.Equals(_openId, challengeId, StringComparison.OrdinalIgnoreCase)
            ? null : challengeId;

    public void Draw()
    {
        if (_openId == null) return;

        var c = ChallengeCatalog.FindCustom(_config, _openId);
        if (c == null) { _openId = null; return; }

        float scale = UiScale.Factor;
        ImGui.SetNextWindowSize(new Vector2(460 * scale, 420 * scale), ImGuiCond.FirstUseEver);

        bool open = true;

        // Push/Pop must bracket Begin, and both must run even when Begin returns false, or the
        // style stack goes unbalanced for every window drawn afterwards.
        DialogTheme.Push();
        bool shown = ImGui.Begin($"{TitleFor(c)}###tc_objectives", ref open,
                                 ImGuiWindowFlags.NoSavedSettings);

        if (shown)
        {
            if (c.IsChain) DrawChain(c);
            else           DrawAdventure(c);
        }

        ImGui.End();
        DialogTheme.Pop();

        if (!open) _openId = null;
    }

    private static string TitleFor(CustomChallenge c) =>
        string.IsNullOrWhiteSpace(c.Title) ? "Objectives" : c.Title;

    // ── Quest chain ──────────────────────────────────────────────────────────

    private void DrawChain(CustomChallenge c)
    {
        int current = Math.Clamp(Plugin.Progress.ChainStep(c.Id), 0, c.ChainSteps.Count);
        bool done   = _store.IsComplete(c.Id);

        // "Quest: <name>", not a bare "QUEST". The kind alone told the player nothing they could not
        // see from the colour, and the challenge's actual name was nowhere on this sheet except the
        // window's title bar — which is easy to miss and gone entirely when the window is docked.
        ImGui.TextColored(Blue, $"Quest: {TitleFor(c)}");
        ImGui.SameLine();
        ImGui.TextDisabled(done
            ? "Complete"
            : $"Step {Math.Min(current + 1, c.ChainSteps.Count)} of {c.ChainSteps.Count}");

        if (!string.IsNullOrWhiteSpace(c.Detail))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(c.Detail);
        }

        ImGui.Separator();
        DrawProgressBar(done ? 1f : current / (float)c.ChainSteps.Count, Blue);
        ImGui.Separator();

        if (ImGui.BeginChild("##tc_obj_scroll", new Vector2(0, 0), false))
        {
            for (int i = 0; i < c.ChainSteps.Count; i++)
            {
                var step = c.ChainSteps[i];
                bool stepDone = done || i < current;
                bool isNow    = !done && i == current;

                // Unreached steps are withheld, not greyed out. A chain's later legs are meant to
                // be a surprise; listing them dimmed would spoil the whole route at a glance and
                // make the reveal pointless.
                if (!stepDone && !isNow)
                {
                    ImGui.TextDisabled($"{i + 1}.  ???");
                    continue;
                }

                ImGui.PushID(i);

                var color = stepDone ? Green : Blue;
                ImGui.TextColored(color, $"{i + 1}.  {Marker(stepDone)}  "
                                       + $"{(string.IsNullOrWhiteSpace(step.Title) ? "(unnamed step)" : step.Title)}");

                ImGui.Indent(18f);

                if (!string.IsNullOrWhiteSpace(step.Detail))
                    ImGui.TextWrapped(step.Detail);

                if (isNow)
                {
                    // Only the CURRENT step lists its stops. A completed one has nothing left to
                    // tell the player, and the objective detail is part of what makes an unreached
                    // step a surprise.
                    DrawZoneLine(step.TerritoryId != 0 ? step.TerritoryId : c.TerritoryId);
                    DrawStops(step.Id, step.Mode, step.Requirements, persist: true);

                    if (step.HasHint)
                    {
                        ImGui.Spacing();
                        ImGui.TextColored(Muted, $"Hint: {step.Hint}");
                    }
                }

                ImGui.Unindent(18f);
                ImGui.Spacing();
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    // ── Adventure ────────────────────────────────────────────────────────────

    private void DrawAdventure(CustomChallenge c)
    {
        bool done = _store.IsComplete(c.Id);
        var reqs  = c.Requirements ?? new List<AreaRequirement>();

        // Through the tracker, never straight off the disk. A SessionOnly adventure never writes to
        // the progress store at all, so reading the store alone made this sheet report 0 of N for
        // the entire run of one — directly under the gold line promising the progress it was
        // failing to show. See ChallengeTracker.SatisfiedStops.
        IReadOnlySet<int> stops = done ? AllOf(reqs.Count)
                                       : _tracker.SatisfiedStops(c.Id, !c.SessionOnly);
        int satisfied = Math.Min(stops.Count, reqs.Count);

        // Same shape as the quest header above, so the two sheets read alike.
        ImGui.TextColored(Green, $"Adventure: {TitleFor(c)}");
        ImGui.SameLine();
        ImGui.TextDisabled(done ? "Complete" : $"{satisfied} of {reqs.Count} objectives");

        if (!string.IsNullOrWhiteSpace(c.Detail))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(c.Detail);
        }

        ImGui.Separator();
        DrawProgressBar(reqs.Count > 0 ? satisfied / (float)reqs.Count : 0f, Green);
        ImGui.Separator();

        DrawZoneLine(c.TerritoryId);

        if (c.Mode == AreaMode.InOrder)
            ImGui.TextDisabled("These must be done in order.");
        if (c.SessionOnly)
            ImGui.TextColored(Gold, "All in one login session — progress resets when you log out.");

        ImGui.Spacing();

        if (ImGui.BeginChild("##tc_obj_scroll", new Vector2(0, 0), false))
        {
            for (int i = 0; i < reqs.Count; i++)
            {
                bool stopDone = stops.Contains(i);

                // In-order adventures hide everything past the next one, for the same reason a
                // chain hides its later steps.
                bool hidden = c.Mode == AreaMode.InOrder && !stopDone && i > satisfied;
                if (hidden)
                {
                    ImGui.TextDisabled($"{i + 1}.  ???");
                    continue;
                }

                ImGui.TextColored(stopDone ? Green : Gold,
                                  $"{i + 1}.  {Marker(stopDone)}  {reqs[i].DisplayLabel}");

                ImGui.Indent(18f);
                DrawConditions(reqs[i]);
                ImGui.Unindent(18f);
                ImGui.Spacing();
            }
        }
        ImGui.EndChild();

        if (!string.IsNullOrWhiteSpace(c.Hint))
        {
            ImGui.Separator();
            ImGui.TextColored(Muted, $"Hint: {c.Hint}");
        }
    }

    // ── Shared pieces ────────────────────────────────────────────────────────

    private void DrawStops(string key, AreaMode mode, List<AreaRequirement> reqs, bool persist)
    {
        if (reqs == null || reqs.Count == 0) return;

        var stops = _tracker.SatisfiedStops(key, persist);
        int satisfied = stops.Count;

        for (int i = 0; i < reqs.Count; i++)
        {
            bool stopDone = mode == AreaMode.InOrder ? i < satisfied : stops.Contains(i);

            // A single-stop step is the step; repeating its label under itself is noise.
            if (reqs.Count > 1)
            {
                ImGui.TextColored(stopDone ? Green : Gold,
                                  $"   {Marker(stopDone)}  {reqs[i].DisplayLabel}");
                ImGui.Indent(12f);
            }

            DrawConditions(reqs[i]);

            if (reqs.Count > 1) ImGui.Unindent(12f);
        }
    }

    /// <summary>
    /// The condition lines for one stop, in the same words the creator shows.
    /// </summary>
    /// <remarks>
    /// A presence-only stop prints NOTHING. It used to print "· be here", which is true of every
    /// stop in the plugin — you are always required to be there — so it added a line to every
    /// objective while distinguishing none of them. The step's description and hint are what say
    /// where "here" is; this list is for the extra conditions layered on top, and when there are
    /// none the honest rendering is silence.
    /// </remarks>
    private static void DrawConditions(AreaRequirement req)
    {
        if (req?.Conditions == null || req.Conditions.Count == 0) return;

        foreach (var cond in req.Conditions)
            ImGui.TextDisabled($"   · {cond.Describe()}");
    }

    private void DrawZoneLine(ushort territoryId)
    {
        if (territoryId == 0) return;

        // DisplayName, never ZoneName — this is a player-facing surface and the mask must hold.
        ImGui.TextDisabled($"Zone: {ZoneIndex.DisplayName(_config, territoryId)}");
    }

    private static void DrawProgressBar(float fraction, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
        ImGui.ProgressBar(Math.Clamp(fraction, 0f, 1f), new Vector2(-1, 8f * UiScale.Factor), string.Empty);
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// A tick or an empty box, never a colour change alone — the one distinction a colour-blind
    /// player is least likely to catch is exactly the one this list depends on.
    /// </summary>
    private static string Marker(bool done) => done ? "[x]" : "[ ]";

    private static IReadOnlySet<int> AllOf(int count)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < count; i++) set.Add(i);
        return set;
    }
}
