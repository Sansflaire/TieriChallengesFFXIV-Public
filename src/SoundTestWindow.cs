#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Sound;

using PanacheUI.Components;
using PanacheUI.Core;
using PanacheUI.Rendering;

namespace TieriChallengesFFXIV;

/// <summary>
/// Developer sound test. One row per cue with a Play button, plus a browser for walking the UI
/// bank by ear.
///
/// <para>Exists because every sound problem in this plugin so far has been unfalsifiable from
/// code: an out-of-range entry, a wrong bank, and a prepared-but-never-fired handle all look
/// identical from the outside — the engine accepts the request, reports success, and stays quiet.
/// Only listening distinguishes them, and doing that through chat commands meant retyping a
/// command per guess.</para>
///
/// <para>The cue rows read <see cref="GameSound.CueTarget"/>, the same mapping playback uses, so
/// the panel cannot advertise a target that differs from what would really play. Rows come from
/// <see cref="SoundService.AllCues"/>, so a new cue appears here without touching this file.</para>
///
/// <para>Compiled out of the public DLL entirely — the whole file is inside DEV_BUILD, so this is
/// absent rather than hidden.</para>
/// </summary>
internal sealed class SoundTestWindow : IDisposable
{
    private const int SurfaceW = 470;
    // header 52 + 4 section labels/hairlines + 6 rows at 46. Recount when a row is added; the
    // surface is a fixed size, so anything past it is simply not drawn.
    private const int SurfaceH = 508;

    private const float RowH     = 46f;
    private const float PadX     = 14f;
    private const float BankMaxE = 53f;   // SE_UI holds 54 sounds, so 0–53

    private static readonly PColor Accent   = PColor.FromHex("#E3B341");
    private static readonly PColor StatusOk = PColor.FromHex("#7FD6A9");
    private static readonly PColor Neutral  = PColor.FromHex("#8B8794");
    private static readonly PColor TextHi   = PColor.White.WithOpacity(0.94f);

    private readonly ITextureProvider _texProvider;

    private PanacheSurface? _surface;
    private readonly DateTime _start = DateTime.UtcNow;

    private string? _hoverId;
    private string? _hoverNext;

    /// <summary>
    /// Scratch entry for the bank browser — not a cue, just what Play will audition.
    ///
    /// <para>Starts at 50 on purpose: that entry is the one confirmed audible by ear, so it is
    /// the reference for "is audio working at all". If it is silent the problem is global, not
    /// this entry or this bank.</para>
    /// </summary>
    private uint _probe = 50;

    public bool IsVisible;

    public SoundTestWindow(ITextureProvider texProvider) => _texProvider = texProvider;

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(SurfaceW, SurfaceH), ImGuiCond.FirstUseEver);

        // NoTitleBar — the Panache header is the chrome (DESIGN_SYSTEM §1.1).
        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse;

        if (!ImGui.Begin("##tc_sound_test", ref IsVisible, flags))
        {
            ImGui.End();
            return;
        }

        _surface ??= new PanacheSurface(_texProvider, SurfaceW, SurfaceH);

        _hoverNext = null;
        var root = BuildTree();

        var origin     = ImGui.GetCursorScreenPos();
        var mouse      = ImGui.GetMousePos();
        var localMouse = new Vector2(mouse.X - origin.X, mouse.Y - origin.Y);

        bool mouseDown  = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool mouseClick = ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                       && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows
                                              | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                              | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

        var (tex, _) = _surface.Render(root, time, localMouse, mouseDown, mouseClick,
                                       ImGui.GetIO().MouseWheel, ImGui.GetIO().DeltaTime,
                                       forceRedraw: false);

        if (tex.HasValue)
            ImGui.Image(tex.Value, new Vector2(SurfaceW, SurfaceH));

        _hoverId = _hoverNext;

        ImGui.End();
    }

    private Node BuildTree()
    {
        var root = new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = SurfaceW;
            s.HeightMode      = SizeMode.Fixed; s.Height = SurfaceH;
            s.Flow            = Flow.Vertical;
            s.BackgroundColor = Theme.Base;
        });

        root.AppendChild(Header());
        root.AppendChild(SectionLabel("CUES"));

        foreach (var (cue, label, when) in SoundService.AllCues)
            root.AppendChild(CueRow(cue, label, when));

        root.AppendChild(Hairline());
        root.AppendChild(SectionLabel("BUSES"));
        root.AppendChild(BusRow());

        root.AppendChild(Hairline());
        root.AppendChild(SectionLabel($"{GameSound.UiBank}  ·  entries 0–{BankMaxE:0}"));
        root.AppendChild(BankRow());
        root.AppendChild(ScanRow());

        return root;
    }

    /// <summary>
    /// Header with a close control in the top-right.
    ///
    /// <para>NoTitleBar is mandatory for a Panache window (DESIGN_SYSTEM §1.1), so ImGui draws no
    /// close box and the chrome has to provide one. This window originally shipped with neither,
    /// which made it unclosable except by the command that opened it.</para>
    /// </summary>
    private Node Header()
    {
        var head = new Node().WithStyle(s =>
        {
            s.Flow                  = Flow.Horizontal;
            s.WidthMode             = SizeMode.Fill;
            s.HeightMode            = SizeMode.Fixed; s.Height = 52;
            s.Padding               = new EdgeSize(11, PadX, 8, PadX);
            s.BackgroundColor       = Theme.Panel2;
            s.BackgroundGradientEnd = Theme.Panel;
            s.Gap                   = 8;
        });

        var text = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Gap        = 2;
        });

        text.AppendChild(new Node().WithText("Sound Test").WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.FontSize      = 15f;
            s.Bold          = true;
            s.Color         = Accent;
            s.PointerEvents = PointerEvents.None;
        }));

        text.AppendChild(new Node().WithText("Developer build only — absent from the public DLL.")
            .WithStyle(s =>
            {
                s.WidthMode     = SizeMode.Fill;
                s.HeightMode    = SizeMode.Fit;
                s.FontSize      = 10f;
                s.Color         = Theme.TextSubtle;
                s.PointerEvents = PointerEvents.None;
            }));

        head.AppendChild(text);

        // Plain ASCII "X" — the bundled font renders the multiplication sign as a tofu box.
        head.AppendChild(Btn("snd_close", "X", PColor.FromHex("#E57B72"),
                             () => IsVisible = false, 6f));

        return head;
    }

    /// <summary>One cue: what it is, where it currently points, and a button to hear it.</summary>
    private Node CueRow(SoundService.Cue cue, string label, string when)
    {
        var (bank, entry) = GameSound.CueTarget(cue);

        var row = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = RowH;
            s.Padding    = new EdgeSize(0, PadX);
            s.Gap        = 10;
        });

        var text = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Margin     = new EdgeSize(7, 0, 0, 0);
            s.Gap        = 2;
        });

        text.AppendChild(new Node().WithText($"{label}  ·  {when}").WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 12f;
            s.Bold         = true;
            s.Color        = TextHi;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        // A path that does not exist plays "successfully" and stays silent, so a missing bank has
        // to be called out here or it looks identical to a bank that simply has no audio.
        bool exists = GameSound.BankExists(bank);

        // A shipped .wav has no entry index — showing "#0" beside one implies a choice that does
        // not exist.
        bool wav = GameSound.IsWave(bank);

        string sub = wav ? $"{ShortBank(bank)}  ·  shipped file"
                         : $"{ShortBank(bank)}  #{entry}";

        if (!exists) sub += "   ·   FILE NOT FOUND";

        text.AppendChild(new Node().WithText(sub).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 10f;
            s.Color        = exists ? Theme.TextMuted : PColor.FromHex("#E57B72");
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        row.AppendChild(text);
        row.AppendChild(Btn($"play_{cue}", "Play", StatusOk, () => Plugin.Sound.Play(cue), 9f));

        return row;
    }

    /// <summary>Walk the UI bank by ear without retyping a command per guess.</summary>
    private Node BankRow()
    {
        var row = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = RowH;
            s.Padding    = new EdgeSize(0, PadX);
            s.Gap        = 6;
        });

        row.AppendChild(new Node().WithText(_probe == 50 ? "Entry 50  (reference)" : $"Entry {_probe}")
            .WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Margin     = new EdgeSize(13, 0, 0, 0);
            s.FontSize   = 12.5f;
            s.Bold       = true;
            s.Color      = Accent;
        }));

        // Clamped to the real bank: past 53 the engine accepts the index and plays nothing, which
        // is the exact trap that made entries 55 and 85 look broken rather than absent.
        row.AppendChild(Btn("probe_m10", "-10", Neutral, () => Step(-10), 10f));
        row.AppendChild(Btn("probe_m1",  "-1",  Neutral, () => Step(-1),  10f));
        row.AppendChild(Btn("probe_p1",  "+1",  Neutral, () => Step(+1),  10f));
        row.AppendChild(Btn("probe_p10", "+10", Neutral, () => Step(+10), 10f));
        row.AppendChild(Btn("probe_play", "Play", StatusOk,
                            () => Plugin.Sound.PlayEntry(_probe), 10f));

        return row;
    }

    private Node ScanRow()
    {
        var row = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = RowH;
            s.Padding    = new EdgeSize(0, PadX);
            s.Gap        = 6;
        });

        row.AppendChild(new Node().WithText("Walk every entry, or silence a looping one")
            .WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.Margin       = new EdgeSize(14, 0, 0, 0);
                s.FontSize     = 10f;
                s.Color        = Theme.TextMuted;
                s.TextOverflow = TextOverflow.Ellipsis;
            }));

        row.AppendChild(Btn("scan_go",   "Scan all", Accent,
                            () => Plugin.Sound.StartScan(0, (uint)BankMaxE), 10f));
        row.AppendChild(Btn("scan_stop", "Stop",     PColor.FromHex("#E57B72"),
                            () => Plugin.Sound.StopScan(), 10f));

        return row;
    }

    /// <summary>
    /// Live bus readout plus the one lever that might unblock the zingles.
    ///
    /// <para>The zingle banks load correctly and still make no sound because the Zingle bus reads
    /// zero, and BypassVolumeRules does not get past it — the bus is applied after the category.
    /// Match SE copies the sound-effect bus's volume onto it, which is a test rather than a fix:
    /// it changes the RUNNING GAME's audio, not a plugin setting, and does not persist.</para>
    /// </summary>
    private Node BusRow()
    {
        float zingle = GameSound.BusVolume(SoundBus.Zingle);
        float se     = GameSound.BusVolume(SoundBus.SE);
        bool  shut   = zingle <= 0.001f;

        var row = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = RowH;
            s.Padding    = new EdgeSize(0, PadX);
            s.Gap        = 6;
        });

        var text = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Margin     = new EdgeSize(7, 0, 0, 0);
            s.Gap        = 2;
        });

        text.AppendChild(new Node().WithText($"Zingle {zingle:0.00}   ·   SE {se:0.00}").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 12f;
            s.Bold       = true;
            s.Color      = shut ? PColor.FromHex("#E57B72") : TextHi;
        }));

        text.AppendChild(new Node()
            .WithText(shut ? "Zingle bus is shut — zingle cues cannot sound" : "Zingle bus is open")
            .WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = 10f;
                s.Color        = Theme.TextMuted;
                s.TextOverflow = TextOverflow.Ellipsis;
            }));

        row.AppendChild(text);
        row.AppendChild(Btn("bus_match", "Match SE", Accent,
                            () => GameSound.SetBusVolume(SoundBus.Zingle, se), 9f));
        row.AppendChild(Btn("bus_zero", "Zero", Neutral,
                            () => GameSound.SetBusVolume(SoundBus.Zingle, 0f), 9f));

        return row;
    }

    private void Step(int delta)
    {
        long next = (long)_probe + delta;
        _probe = (uint)Math.Clamp(next, 0, (long)BankMaxE);
    }

    private static string ShortBank(string bank)
    {
        int slash = bank.LastIndexOf('/');
        return slash >= 0 && slash < bank.Length - 1 ? bank.Substring(slash + 1) : bank;
    }

    // SkiaRenderer paints no hover cue of its own, so every button tracks its own — same pattern
    // as MainWindow.Pill.
    private Node Btn(string id, string text, PColor accent, Action onClick, float topMargin)
    {
        var node = PUI.PillButton(id, text, accent);
        node.WithStyle(s => s.Margin = new EdgeSize(topMargin, 0, 0, 0));

        if (_hoverId == id)
            node.WithStyle(s => s.BackgroundColor = accent.WithOpacity(0.32f));

        node.OnClick      += _ => onClick();
        node.OnMouseEnter += _ => _hoverNext = id;
        return node;
    }

    private static Node SectionLabel(string text) =>
        new Node().WithText(text).WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = 22;
            s.Padding    = new EdgeSize(6, PadX, 0, PadX);
            s.FontSize   = 9.5f;
            s.Bold       = true;
            s.Color      = Accent.WithOpacity(0.65f);
        });

    private static Node Hairline() =>
        new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fill;
            s.HeightMode      = SizeMode.Fixed; s.Height = 1;
            s.Margin          = new EdgeSize(6, PadX, 0, PadX);
            s.BackgroundColor = PColor.White.WithOpacity(0.06f);
        });
}
#endif
