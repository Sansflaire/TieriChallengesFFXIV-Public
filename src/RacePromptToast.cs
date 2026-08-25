using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

using PanacheUI.Components;
using PanacheUI.Core;
using PanacheUI.Rendering;

namespace TieriChallengesFFXIV;

/// <summary>
/// The bottom-right race panel. Two states on one surface:
///
/// <list type="bullet">
///   <item><b>Armed</b> — the player is standing in a race's start volume. Offers Start, and a
///         "Don't show these" that suppresses the prompt globally.</item>
///   <item><b>Running</b> — a live clock, the time to beat, and Abandon.</item>
/// </list>
///
/// <para><b>Suppression hides the prompt, never the clock.</b> Turning off "do you want to start
/// this?" is a statement about being interrupted; a running timer the player deliberately started
/// is the only way to see how they are doing, and hiding it would make the race unplayable rather
/// than quieter.</para>
///
/// <para>Sits directly above the slot <see cref="ProgressToast"/> occupies, so the two can never
/// overlap. Both are bottom-right and both take input, and a progress notification landing on top
/// of a Start button would eat the click.</para>
/// </summary>
internal sealed class RacePromptToast : IDisposable
{
    private const int SurfaceW = 336;
    private const int SurfaceH = 112;

    private const float MarginX = 24f;

    /// <summary>
    /// Clears the progress toast entirely: its own bottom margin, its full height, and a gap.
    /// Hard-coded against <see cref="ProgressToast"/>'s dimensions on purpose — a computed stack
    /// would need the two to know about each other for a layout that never changes.
    /// </summary>
    private const float MarginY = 24f + 104f + 12f;

    private static readonly PColor Accent  = PColor.FromHex("#E3B341");
    private static readonly PColor Live    = PColor.FromHex("#7FD6A9");
    private static readonly PColor Danger  = PColor.FromHex("#E06C5A");
    private static readonly PColor TextHi  = PColor.White.WithOpacity(0.94f);

    private readonly ITextureProvider _texProvider;
    private readonly Configuration    _config;
    private readonly CompletionStore  _store;
    private readonly ChallengeTracker _tracker;
    private readonly Action           _save;

    private PanacheSurface? _surface;
    private readonly DateTime _start = DateTime.UtcNow;

    public RacePromptToast(ITextureProvider texProvider, Configuration config,
                           CompletionStore store, ChallengeTracker tracker, Action save)
    {
        _texProvider = texProvider;
        _config      = config;
        _store       = store;
        _tracker     = tracker;
        _save        = save;
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }

    public void Draw()
    {
        string? runningId = _tracker.RunningRaceId;
        string? armedId   = null;

        if (runningId == null && !_config.RacePromptSuppressed)
        {
            var armed = _tracker.ArmedRaces;
            if (armed.Count > 0) armedId = armed[0];   // one line at a time; overlapping starts are pathological
        }

        string? id = runningId ?? armedId;
        if (id == null) return;

        var def = ChallengeCatalog.FindCustom(_config, id);
        if (def == null) return;

        bool running = runningId != null;

        var viewport = ImGui.GetMainViewport();
        float uiScale = UiScale.Factor;
        int   physW   = (int)(SurfaceW * uiScale);
        int   physH   = (int)(SurfaceH * uiScale);

        var pos = new Vector2(
            viewport.Pos.X + viewport.Size.X - physW - MarginX,
            viewport.Pos.Y + viewport.Size.Y - physH - MarginY);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(physW, physH), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        // Takes input — it has buttons. Same trade as ProgressToast: small, and parked in the
        // corner rather than anywhere a click could be aimed at the game.
        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("##tc_race_toast", flags))
        {
            ImGui.End();
            return;
        }

        _surface ??= new PanacheSurface(_texProvider, physW, physH);
        _surface.Resize(physW, physH);
        _surface.Scale = uiScale;

        var root = BuildTree(def, running);

        var origin     = ImGui.GetCursorScreenPos();
        var mouse      = ImGui.GetMousePos();
        var localMouse = new Vector2(mouse.X - origin.X, mouse.Y - origin.Y);

        bool mouseDown  = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool mouseClick = ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                       && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows
                                              | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                              | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

        // forceRedraw: the clock changes every frame while running, and the surface caches its
        // texture otherwise — without this the timer would freeze at whatever it read when the
        // tree last changed shape.
        var (tex, _) = _surface.Render(root, time, localMouse, mouseDown, mouseClick,
                                       0f, ImGui.GetIO().DeltaTime, forceRedraw: running);

        if (tex.HasValue)
            ImGui.Image(tex.Value, new Vector2(physW, physH));

        ImGui.End();
    }

    private Node BuildTree(CustomChallenge def, bool running)
    {
        var edge = running ? Live : Accent;

        var root = new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = SurfaceW;
            s.HeightMode      = SizeMode.Fixed; s.Height = SurfaceH;
            s.Flow            = Flow.Horizontal;
            s.BackgroundColor = PColor.FromHex("#12101A").WithOpacity(0.96f);
            s.BorderRadius    = 8f;
            s.BorderColor     = edge.WithOpacity(0.45f);
            s.BorderWidth     = 1;
        });

        root.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode              = SizeMode.Fixed; s.Width = 3;
            s.HeightMode             = SizeMode.Fill;
            s.BackgroundColor        = edge.WithOpacity(0.85f);
            s.BorderRadiusTopLeft    = 8f;
            s.BorderRadiusBottomLeft = 8f;
        }));

        var body = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fill;
            s.Padding    = new EdgeSize(10, 12, 9, 13);
            s.Gap        = 3;
        });

        string title = string.IsNullOrWhiteSpace(def.Title) ? "(unnamed race)" : def.Title;

        body.AppendChild(new Node().WithText(title).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 12.5f;
            s.Bold         = true;
            s.Color        = TextHi;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        if (running) BuildRunning(body, def);
        else         BuildArmed(body, def);

        root.AppendChild(body);
        return root;
    }

    private void BuildArmed(Node body, CustomChallenge def)
    {
        body.AppendChild(new Node().WithText("Ready to start timed challenge?").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 11f;
            s.Bold       = true;
            s.Color      = Accent.WithOpacity(0.95f);
        }));

        body.AppendChild(new Node().WithText(SubtitleFor(def)).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 10f;
            s.Color        = Theme.TextMuted;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        var footer = FooterRow();

        var quiet = PUI.PillButton("race_quiet", "Don't show these", Theme.TextSubtle);
        quiet.WithStyle(s => s.HoverBackgroundColor = PColor.White.WithOpacity(0.14f));
        quiet.OnClick += _ =>
        {
            _config.RacePromptSuppressed = true;
            _save();
            Plugin.ChatGui.Print("[Challenges] Race prompts hidden. "
                               + "Start races from the challenge list, or re-enable them in Settings.");
        };
        footer.AppendChild(quiet);

        string captured = def.Id;
        var startBtn = PUI.PillButton("race_start", "Start!", Accent);
        startBtn.WithStyle(s => s.HoverBackgroundColor = Accent.WithOpacity(0.32f));
        startBtn.OnClick += _ =>
        {
            if (!_tracker.TryStartRace(captured))
                Plugin.ChatGui.PrintError("[Challenges] Stand in the start area to begin the run.");
        };
        footer.AppendChild(startBtn);

        body.AppendChild(footer);
    }

    private void BuildRunning(Node body, CustomChallenge def)
    {
        double elapsed = _tracker.RunningElapsedSeconds;
        bool   timed   = def.RaceFailSeconds > 0;
        double left    = timed ? def.RaceFailSeconds - elapsed : 0;

        // Turns red for the last five seconds. The clock is the one thing a runner is watching, so
        // "you are about to lose" has to read without being parsed.
        var clockColor = timed && left <= 5 ? Danger : Live;

        body.AppendChild(new Node().WithText(CompletionStore.FormatRaceTime(elapsed)).WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 20f;
            s.Bold       = true;
            s.Color      = clockColor;
        }));

        string sub = timed
            ? $"{Math.Max(0, left):0.0}s left" + BestSuffix(def)
            : "no time limit" + BestSuffix(def);

        body.AppendChild(new Node().WithText(sub).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 10f;
            s.Color        = Theme.TextMuted;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        var footer = FooterRow();

        var abandon = PUI.PillButton("race_abandon", "Abandon", Danger);
        abandon.WithStyle(s => s.HoverBackgroundColor = Danger.WithOpacity(0.32f));
        abandon.OnClick += _ => _tracker.AbandonRace();
        footer.AppendChild(abandon);

        body.AppendChild(footer);
    }

    private static Node FooterRow()
    {
        var footer = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Gap        = 8;
            s.Margin     = new EdgeSize(4, 0, 0, 0);
        });

        // Spacer, so the buttons sit right-aligned without a justify mode.
        footer.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.PointerEvents = PointerEvents.None;
        }));

        return footer;
    }

    private string SubtitleFor(CustomChallenge def)
    {
        string limit = def.RaceFailSeconds > 0
            ? $"Time limit {CompletionStore.FormatRaceTime(def.RaceFailSeconds)}"
            : "No time limit";

        return limit + BestSuffix(def);
    }

    private string BestSuffix(CustomChallenge def)
    {
        double? best = _store.BestRaceTime(def.Id);
        return best.HasValue ? $"  ·  best {CompletionStore.FormatRaceTime(best.Value)}" : string.Empty;
    }
}
