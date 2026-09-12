#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY — the <b>Test City</b> panel: build it, clear it, and dial in the tiles,
/// the blocks and the look. Everything about the experiment lives in three gated files (this,
/// <see cref="TestCityService"/>, <see cref="TestCityRender"/>) for the same reason the dig lab is
/// one file per test: a feature whose rules are in a doc, whose commands are in the command switch
/// and whose settings are in a header is four places to forget one of, and the one people forget is
/// the gate.
///
/// <para>Raw ImGui rather than PanacheUI, which is permitted here: the match-the-main-window rule
/// covers <i>player-facing</i> surfaces, and no player can reach this.</para>
///
/// <para><b>A slider that changes WHERE something goes rebuilds the city; one that changes how it
/// LOOKS does not.</b> Tile size, grid extent, block pitch and the height range all feed placement
/// and the measured lattice, so they re-run the build on release — and that re-rolls heights and
/// window patterns, which is stated on screen rather than being a surprise. Opacity, lit fraction
/// and the colours are pure drawing and take effect on the next frame with the city untouched.</para>
/// </summary>
internal sealed class TestCityWindow
{
    public bool IsVisible;

    private readonly TestCityService _city;

    private static readonly Vector4 Head = new(0.95f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Rule = new(0.68f, 0.68f, 0.76f, 1f);
    private static readonly Vector4 Ok   = new(0.44f, 0.86f, 0.62f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.62f, 0.35f, 1f);

    public TestCityWindow(TestCityService city) => _city = city;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(520, 640), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Test City  (DEV ONLY)###tc_test_city", ref IsVisible))
        {
            ImGui.End();
            return;
        }

        try { DrawBody(); }
        catch (Exception ex) { Diag.Error($"[City] window draw failed: {ex.Message}"); }

        ImGui.End();
    }

    private void DrawBody()
    {
        ImGui.TextColored(Head, "DEVELOPER BUILD ONLY");
        ImGui.TextColored(Rule,
            "None of this ships. The service, the renderer and this window are all behind\n"
          + "#if DEV_BUILD, so a player cannot reach any of it.");

        ImGui.Separator();

        DrawActions();
        DrawStatus();

        ImGui.Separator();

        Section("Ground grid — 1:1 tiles", DrawGrid);
        Section("Blocks — where buildings go", DrawBlocks);
        Section("Buildings — height and floors", DrawHeights);
        Section("Look — opacity, windows, colours", DrawLook);
    }

    private void DrawActions()
    {
        if (ImGui.Button("Build city here")) Say(_city.Build());

        ImGui.SameLine();

        ImGui.BeginDisabled(!_city.IsBuilt);
        if (ImGui.Button("Rebuild")) Say(_city.Build());
        ImGui.SameLine();
        if (ImGui.Button("Clear")) Say(_city.Clear());
        ImGui.EndDisabled();

        ImGui.TextDisabled("built on the ground under your feet; cleared automatically on a zone change");
    }

    /// <summary>
    /// The live readout, and it is not decoration.
    ///
    /// <para><b>Panels is the number that can tell a rendering fault from an empty city.</b> There is
    /// no depth buffer, so face visibility is decided here rather than by the GPU — and a visibility
    /// test that came out inverted would draw precisely the four hidden faces of every building,
    /// which on screen is indistinguishable from nothing having been placed. Buildings above zero
    /// with panels at zero says the cull is backwards; both at zero says placement found nowhere to
    /// build. Without this line those two look the same.</para>
    /// </summary>
    private void DrawStatus()
    {
        if (!_city.IsBuilt)
        {
            ImGui.TextColored(Warn, "NO CITY — press Build.");
            return;
        }

        var o = _city.Origin;

        ImGui.TextColored(Ok, $"{_city.BuildingCount} building(s) · {_city.PanelsDrawn} panel(s) "
                            + "drawn last frame");
        ImGui.TextDisabled($"{_city.TilesAcross}×{_city.TilesAcross} tiles · "
                         + $"grid corner ({o.X:0.#}, {o.Z:0.#})");

        // Which eye the culling and the ordering used. A wrong eye position looks exactly like broken
        // geometry, so the fallback is said out loud rather than substituted quietly.
        if (_city.EyeFromCamera)
            ImGui.TextDisabled("eye: game camera");
        else
            ImGui.TextColored(Warn, "eye: PLAYER FALLBACK — camera unreadable, faces may sort oddly");

        if (_city.BuildingCount > 0 && _city.PanelsDrawn == 0)
            ImGui.TextColored(Warn, "buildings placed but nothing drawn — face culling is inverted");
    }

    private void DrawGrid()
    {
        ImGui.Checkbox("Draw the tile grid", ref _city.ShowGrid);

        ImGui.ColorEdit3("Grid colour", ref _city.GridRgb);

        ImGui.SliderFloat("Tile size (yalms)", ref _city.TileSize, 0.5f, 8f, "%.2f");
        RebuildIfReleased();

        ImGui.SliderInt("Tiles across", ref _city.GridTiles, 4, 48);
        RebuildIfReleased();

        ImGui.TextDisabled("The grid is measured once per build — one downward raycast per tile\n"
                         + "corner, so 48 tiles is ~2,400 rays paid in a single frame. Buildings\n"
                         + "take their corners from this same lattice, which is what makes them\n"
                         + "line up with the tiles rather than merely look as though they do.");
    }

    private void DrawBlocks()
    {
        ImGui.SliderInt("Building footprint (tiles)", ref _city.BuildingTiles, 1, 12);
        RebuildIfReleased();

        ImGui.SliderInt("Street width (tiles)", ref _city.StreetTiles, 1, 8);
        RebuildIfReleased();

        ImGui.SliderInt("Clear tiles around you", ref _city.ClearTiles, 0, 20);
        RebuildIfReleased();

        ImGui.TextDisabled("Blocks are laid on a fixed pitch of footprint + street. A regular grid\n"
                         + "is deliberate: a half-tile misalignment shows up across the whole scene\n"
                         + "instead of being arguable on one box.");
    }

    private void DrawHeights()
    {
        ImGui.SliderFloat("Shortest (yalms)", ref _city.MinHeight, 3f, 80f, "%.0f");
        RebuildIfReleased();

        ImGui.SliderFloat("Tallest (yalms)", ref _city.MaxHeight, 3f, 120f, "%.0f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            // Kept in order here rather than at use: a max below the min silently produces a city of
            // identical shortest buildings, which reads as the randomiser being broken.
            if (_city.MaxHeight < _city.MinHeight) _city.MaxHeight = _city.MinHeight;
            _city.Rebuild();
        }

        ImGui.SliderFloat("Floor height (yalms)", ref _city.FloorHeight, 1.5f, 8f, "%.2f");
        RebuildIfReleased();

        ImGui.TextDisabled("Heights are rounded to a whole number of floors, so the top window row\n"
                         + "is never a sliver. The range is honoured to within one storey.");
    }

    private void DrawLook()
    {
        ImGui.SliderFloat("Wall opacity", ref _city.WallAlpha, 0.05f, 1f, "%.2f");

        ImGui.Checkbox("Windows", ref _city.ShowWindows);
        ImGui.SameLine();
        ImGui.Checkbox("Roofs", ref _city.ShowRoofs);
        ImGui.SameLine();
        ImGui.Checkbox("Edges", ref _city.ShowEdges);

        ImGui.SliderInt("Lit windows (%)", ref _city.LitPercent, 0, 100);

        ImGui.ColorEdit3("Lit window", ref _city.LitRgb);
        ImGui.ColorEdit3("Dark window / door", ref _city.DarkRgb);

        ImGui.TextDisabled("Which windows are lit is fixed per building at build time — rolling it\n"
                         + "per frame would strobe the whole city. Wall shading comes from one\n"
                         + "fixed light, which is what makes five flat fills read as a solid.");
    }

    /// <summary>
    /// Re-runs the build when the slider just released. <b>On release, never per frame</b> — the
    /// build re-measures the ground with a raycast per tile corner, and doing that while a slider is
    /// being dragged would fire thousands of rays a second.
    /// </summary>
    private void RebuildIfReleased()
    {
        if (ImGui.IsItemDeactivatedAfterEdit()) _city.Rebuild();
    }

    /// <summary>One collapsible block, closed by default. ImGui remembers each header's state in its
    /// own ini, so a section opened once stays open across sessions.</summary>
    private static void Section(string title, Action body)
    {
        if (!ImGui.CollapsingHeader(title)) return;

        ImGui.Indent();

        // Guarded individually rather than relying on the caller's catch: one section throwing must
        // not take the rest of the window with it, and an Unindent that never ran would shift every
        // window drawn afterwards.
        try { body(); }
        catch (Exception ex) { Diag.Error($"[City] section '{title}' failed: {ex.Message}"); }

        ImGui.Unindent();
    }

    private static void Say(string message) => Plugin.ChatGui.Print("[Challenges] " + message);
}
#endif
