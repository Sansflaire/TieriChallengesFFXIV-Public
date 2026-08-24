using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace TieriChallengesFFXIV;

/// <summary>
/// Live game state — character, zone, and in dev builds the animation/mount/minion/outfit
/// readouts — shown on demand rather than permanently.
///
/// <para>These lines used to sit in the main window's header, where they cost four rows of
/// vertical space in every session to answer a question that is only asked while authoring a
/// challenge. They are now behind the Info button.</para>
///
/// <para><b>This also makes the plugin cheaper.</b> The refresh only runs while this window is
/// open. <see cref="PlayerStateReader.DescribeOutfit"/> walks the whole outfit index against a
/// live equipment read; previously that ran on a timer for as long as the main window was open,
/// whether or not anyone was looking at it.</para>
///
/// <para>Plain ImGui on purpose. DESIGN_SYSTEM §10 permits it for standard popups, and a real
/// ImGui window brings a title bar and a close box with it — which is exactly what the Panache
/// windows had to grow by hand.</para>
/// </summary>
internal sealed class StatusWindow
{
    /// <summary>Twice a second. The outfit walk is far too expensive to run at frame rate.</summary>
    private const int RefreshIntervalMs = 500;

    private readonly Configuration _config;

    public bool IsVisible;

    private long   _nextRefreshMs;
    private string _character = "—";
    private string _zone      = "—";

    private ExcelSheet<TerritoryType>? _territorySheet;

#if DEV_BUILD
    private string _animation = "—";
    private string _mount     = "—";
    private string _minion    = "—";
    private string _outfit    = "—";
#endif

    public StatusWindow(Configuration config) => _config = config;

    public void Draw()
    {
        if (!IsVisible) return;

        Refresh();

        ImGui.SetNextWindowSize(new Vector2(380, 0), ImGuiCond.FirstUseEver);

        // See DialogTheme's class remark: pushed/popped around the whole Begin/End pair, and
        // this file still carries no PanacheUI type reference.
        DialogTheme.Push();

        if (ImGui.Begin("Live status##tc_status", ref IsVisible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(DialogTheme.Accent, _character);
            ImGui.TextUnformatted(_zone);

#if DEV_BUILD
            // Suppressed in public-preview mode so the dev build renders exactly as the public
            // one does — same rule as every other developer affordance.
            if (!_config.PublicPreview)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                ImGui.TextDisabled("Developer");
                ImGui.TextUnformatted($"Animation: {_animation}");
                ImGui.TextUnformatted($"Mount: {_mount}");
                ImGui.TextUnformatted($"Minion: {_minion}");
                ImGui.TextUnformatted($"Outfit: {_outfit}");
            }
#endif
        }
        ImGui.End();

        DialogTheme.Pop();
    }

    private void Refresh()
    {
        long now = Environment.TickCount64;
        if (now < _nextRefreshMs) return;
        _nextRefreshMs = now + RefreshIntervalMs;

        _character = DescribeCharacter();
        _zone      = DescribeZone();

#if DEV_BUILD
        if (_config.PublicPreview) return;

        _animation = PlayerStateReader.DescribeAnimation();
        _mount     = PlayerStateReader.DescribeMount();
        _minion    = PlayerStateReader.DescribeMinion();
        _outfit    = PlayerStateReader.DescribeOutfit();
#endif
    }

    // ── Live game state ──────────────────────────────────────────────────────
    //
    // Lived in MainWindow and again, copy-pasted, in FallbackWindow. One copy now, read by both
    // through this window.

    private static string DescribeCharacter()
    {
        try
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp == null) return "Character: not logged in";

            string job = lp.ClassJob.ValueNullable?.Abbreviation.ToString() ?? "???";
            return $"Character: {lp.Name} — {job} Lv{lp.Level}";
        }
        catch
        {
            return "Character: unavailable";
        }
    }

    private string DescribeZone()
    {
        try
        {
            ushort territory = (ushort)Plugin.ClientState.TerritoryType;
            if (territory == 0) return "Zone: —";

            _territorySheet ??= Plugin.DataManager.GetExcelSheet<TerritoryType>();
            string name = _territorySheet?.GetRowOrDefault(territory)?
                              .PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;

            return string.IsNullOrEmpty(name)
                ? $"Zone: territory {territory}"
                : $"Zone: {name}  (territory {territory})";
        }
        catch
        {
            return "Zone: unavailable";
        }
    }
}
