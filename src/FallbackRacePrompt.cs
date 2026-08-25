using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// Plain-ImGui twin of <see cref="RacePromptToast"/>, for when PanacheUI is switched off or could
/// not be loaded.
///
/// <para>A race is unplayable without its clock — there is no "degrade to text" option here the
/// way there is for a decorative popup, so this carries the same two states and the same buttons.
/// Themed through <see cref="DialogTheme"/> like every other player-facing raw-ImGui surface.</para>
/// </summary>
internal sealed class FallbackRacePrompt
{
    private const float WindowW = 336f;
    private const float MarginX = 24f;

    /// <summary>Matches <see cref="RacePromptToast"/>'s offset so the two never disagree.</summary>
    private const float MarginY = 24f + 104f + 12f;

    private static readonly Vector4 Accent = new(0.89f, 0.70f, 0.25f, 1f);
    private static readonly Vector4 Live   = new(0.50f, 0.84f, 0.66f, 1f);
    private static readonly Vector4 Danger = new(0.88f, 0.42f, 0.35f, 1f);

    private readonly Configuration    _config;
    private readonly CompletionStore  _store;
    private readonly ChallengeTracker _tracker;
    private readonly Action           _save;

    /// <summary>Matches <see cref="RacePromptToast.ResultHoldSeconds"/>.</summary>
    private const double ResultHoldSeconds = 6.0;

    private RaceEndedEvent? _result;
    private long _resultAtMs;

    public FallbackRacePrompt(Configuration config, CompletionStore store,
                              ChallengeTracker tracker, Action save)
    {
        _config  = config;
        _store   = store;
        _tracker = tracker;
        _save    = save;
    }

    /// <summary><inheritdoc cref="RacePromptToast.OnRaceEnded"/></summary>
    public void OnRaceEnded(RaceEndedEvent e, bool firstCompletion)
    {
        if (firstCompletion) return;

        _result     = e;
        _resultAtMs = Environment.TickCount64;
    }

    public void Draw()
    {
        string? runningId = _tracker.RunningRaceId;

        var result = _result;
        if (result != null
            && (Environment.TickCount64 - _resultAtMs) / 1000.0 > ResultHoldSeconds)
        {
            _result = result = null;
        }

        string? armedId = null;
        if (runningId == null && result == null && !_config.RacePromptSuppressed)
        {
            var armed = _tracker.ArmedRaces;
            if (armed.Count > 0) armedId = armed[0];
        }

        string? id = runningId ?? result?.Id ?? armedId;
        if (id == null) return;

        var def = ChallengeCatalog.FindCustom(_config, id);
        if (def == null) return;

        bool running = runningId != null;

        float scale = UiScale.Factor;
        float w = WindowW * scale;
        float h = 112f * scale;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            new Vector2(viewport.Pos.X + viewport.Size.X - w - MarginX,
                        viewport.Pos.Y + viewport.Size.Y - h - MarginY), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(w, h), ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoNav;

        // Push/Pop must bracket Begin and both must run even when Begin returns false, or the
        // style stack goes unbalanced for every window drawn after this one.
        DialogTheme.Push();
        bool open = ImGui.Begin("##tc_race_fallback", flags);

        if (open)
        {
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(def.Title) ? "(unnamed race)" : def.Title);
            ImGui.Separator();

            if (running)             DrawRunning(def);
            else if (result != null) DrawResult(result);
            else                     DrawArmed(def);
        }

        ImGui.End();
        DialogTheme.Pop();
    }

    private void DrawArmed(CustomChallenge def)
    {
        ImGui.TextColored(Accent, "Ready to start timed challenge?");

        string limit = def.RaceFailSeconds > 0
            ? $"Time limit {CompletionStore.FormatRaceTime(def.RaceFailSeconds)}"
            : "No time limit";
        ImGui.TextDisabled(limit + BestSuffix(def));

        if (ImGui.Button("Start!", new Vector2(90, 24)))
        {
            if (!_tracker.TryStartRace(def.Id))
                Plugin.ChatGui.PrintError("[Challenges] Stand in the start area to begin the run.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Don't show these", new Vector2(150, 24)))
        {
            _config.RacePromptSuppressed = true;
            _save();
            Plugin.ChatGui.Print("[Challenges] Race prompts hidden. "
                               + "Start races from the challenge list, or re-enable them in Settings.");
        }
    }

    private void DrawResult(RaceEndedEvent result)
    {
        bool best     = result.Outcome == RaceOutcome.Finished && result.NewBest;
        bool finished = result.Outcome == RaceOutcome.Finished;

        var color = best ? Accent : finished ? Live : Danger;

        ImGui.TextColored(color, best ? "PERSONAL BEST" : result.Describe());
        ImGui.TextColored(color, CompletionStore.FormatRaceTime(result.Seconds));

        if (best && result.PreviousBest.HasValue)
            ImGui.TextDisabled($"beat {CompletionStore.FormatRaceTime(result.PreviousBest.Value)}");
        else if (best)
            ImGui.TextDisabled("first recorded time");
        else if (result.PreviousBest.HasValue)
            ImGui.TextDisabled($"best stands at {CompletionStore.FormatRaceTime(result.PreviousBest.Value)}");
    }

    private void DrawRunning(CustomChallenge def)
    {
        double elapsed = _tracker.RunningElapsedSeconds;
        bool   timed   = def.RaceFailSeconds > 0;
        double left    = timed ? def.RaceFailSeconds - elapsed : 0;

        ImGui.TextColored(timed && left <= 5 ? Danger : Live,
                          CompletionStore.FormatRaceTime(elapsed));

        ImGui.TextDisabled((timed ? $"{Math.Max(0, left):0.0}s left" : "no time limit") + BestSuffix(def));

        if (ImGui.Button("Abandon", new Vector2(110, 24))) _tracker.AbandonRace();
    }

    private string BestSuffix(CustomChallenge def)
    {
        double? best = _store.BestRaceTime(def.Id);
        return best.HasValue ? $"   best {CompletionStore.FormatRaceTime(best.Value)}" : string.Empty;
    }
}
