#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY — the <b>Test City</b> lab. <b>One window, one tab per test</b>, at Sansflaire's
/// instruction: the city on one tab, the circular goal area on another, and any future test as
/// another tab rather than another window. The dig lab's lesson applies — everything about a test
/// (its rules, its commands, its buttons and its settings) lives in one gated place, because a test
/// spread across four files is four places to forget one of, and the one people forget is the gate.
///
/// <para>Raw ImGui rather than PanacheUI, which is permitted here: the match-the-main-window rule
/// covers <i>player-facing</i> surfaces, and no player can reach this.</para>
///
/// <para><b>A slider that changes WHERE something goes rebuilds; one that changes how it LOOKS does
/// not.</b> Tile size, extent, block pitch, height range and measure spacing all feed placement or
/// the measured ground, so they re-run the build on release — and that re-rolls heights and window
/// patterns, which is stated on screen rather than being a surprise. Opacity, lit fraction, colours
/// and the occlusion toggle are pure drawing and take effect on the next frame.</para>
/// </summary>
internal sealed class TestCityWindow
{
    public bool IsVisible;

    private readonly TestCityService _city;
    private readonly TestGoalService _goal;

    private static readonly Vector4 Head = new(0.95f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Rule = new(0.68f, 0.68f, 0.76f, 1f);
    private static readonly Vector4 Ok   = new(0.44f, 0.86f, 0.62f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.62f, 0.35f, 1f);

    public TestCityWindow(TestCityService city, TestGoalService goal)
    {
        _city = city;
        _goal = goal;
    }

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(560, 700), ImGuiCond.FirstUseEver);

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
            "None of this ships. Every service, renderer and tab here is behind #if DEV_BUILD,\n"
          + "so a player cannot reach any of it.");

        ImGui.Separator();

        if (!ImGui.BeginTabBar("##tc_city_tabs")) return;

        if (ImGui.BeginTabItem("City"))
        {
            // Guarded per tab: one tab throwing must not take the tab bar down with it, and an
            // EndTabItem that never ran would unbalance ImGui for every window drawn afterwards.
            try { DrawCityTab(); }
            catch (Exception ex) { Diag.Error($"[City] city tab failed: {ex.Message}"); }

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Goal Area"))
        {
            try { DrawGoalTab(); }
            catch (Exception ex) { Diag.Error($"[Goal] tab failed: {ex.Message}"); }

            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // ── tab 1: the city ──────────────────────────────────────────────────────

    private void DrawCityTab()
    {
        DrawCityActions();
        DrawCityStatus();

        ImGui.Separator();

        Section("Ground grid — tiles and measurement", DrawGrid);
        Section("Grid drawing — fine window and major lines", DrawGridDraw);
        Section("Blocks — where buildings go", DrawBlocks);
        Section("Buildings — height and floors", DrawHeights);
        Section("Look — opacity, windows, colours", DrawLook);
        Section("Occlusion — hide what the world blocks", DrawOcclusion);
    }

    private void DrawCityActions()
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
    /// <para><b>Panels is the number that tells a rendering fault from an empty city.</b> There is no
    /// depth buffer, so face visibility is decided in our code rather than by the GPU — and a test
    /// that came out inverted would draw precisely the hidden faces of every building, which on
    /// screen is indistinguishable from nothing having been placed. Buildings above zero with panels
    /// at zero says the cull is backwards; both at zero says placement found nowhere to build.</para>
    /// </summary>
    private void DrawCityStatus()
    {
        if (!_city.IsBuilt)
        {
            ImGui.TextColored(Warn, "NO CITY — press Build.");
            return;
        }

        ImGui.TextColored(Ok, $"{_city.RenderedCount} of {_city.BuildingCount} building(s) drawn · "
                            + $"{_city.PanelsDrawn} panel(s) · {_city.RaysLastFrame} occlusion ray(s) "
                            + "last frame");

        ImGui.TextDisabled($"{_city.TilesAcross}×{_city.TilesAcross} tiles of "
                         + $"{_city.BuiltTileSize:0.###}y = {_city.Extent:0}y across · ground sampled "
                         + $"{_city.SamplesPerSide}×{_city.SamplesPerSide} every "
                         + $"{_city.SampleSpacing:0.##}y");

        // Which eye the culling, the ordering and the occlusion used. A wrong eye position looks
        // exactly like broken geometry, so the fallback is said out loud rather than substituted
        // quietly.
        if (_city.EyeFromCamera) ImGui.TextDisabled("eye: game camera");
        else ImGui.TextColored(Warn, "eye: PLAYER FALLBACK — camera unreadable, faces may sort oddly");

        if (_city.BuildingCount > 0 && _city.PanelsDrawn == 0)
            ImGui.TextColored(Warn, "buildings placed but nothing drawn — face culling is inverted");

        if (_city.SampleSpacing > _city.BuiltTileSize * 4f)
            ImGui.TextColored(Warn, $"ground is measured every {_city.SampleSpacing:0.##}y but tiles "
                                  + $"are {_city.BuiltTileSize:0.###}y — the grid is draped, not "
                                  + "followed. Tighten measure spacing or shrink the extent.");
    }

    private void DrawGrid()
    {
        ImGui.Checkbox("Draw the tile grid", ref _city.ShowGrid);

        ImGui.ColorEdit3("Grid colour", ref _city.GridRgb);

        ImGui.SliderFloat("Tile size (yalms)", ref _city.TileSize, 0.05f, 8f, "%.3f");
        RebuildIfReleased();

        ImGui.SliderInt("Tiles across", ref _city.GridTiles, 4, 400);
        RebuildIfReleased();

        ImGui.SliderFloat("Ground measured every (yalms)", ref _city.MeasureSpacing, 0.5f, 16f, "%.2f");
        RebuildIfReleased();

        ImGui.TextDisabled("MEASUREMENT IS DECOUPLED FROM TILES, and it has to be: one ray per tile\n"
                         + "corner is 66,000 rays at 400 tiles. Ground is sampled on its own spacing\n"
                         + "and every height is interpolated from it, so rays scale with the city's\n"
                         + "SIZE and are capped at 65 per side whatever these sliders say. The cost\n"
                         + "is that coarse sampling drapes the grid over terrain instead of following\n"
                         + "it — the readout above warns when the two are badly mismatched.");
    }

    private void DrawGridDraw()
    {
        ImGui.SliderInt("Fine tiles around you", ref _city.FineTiles, 2, TestCityGrid.MaxFine);
        ImGui.SliderInt("Major line every N tiles", ref _city.MajorEvery, 1, 64);
        ImGui.Checkbox("Checkerboard", ref _city.ShowChecker);

        ImGui.TextDisabled("The fine 1:1 grid is drawn in a window that FOLLOWS YOU, and major lines\n"
                         + "cover the whole extent. Drawing every tile of a 400-tile grid would be\n"
                         + "160,000 checker quads a frame, so this is the one part that does not\n"
                         + "scale with the city. Both are capped; the major step widens itself rather\n"
                         + "than drawing a partial grid, because a grid with a false edge is worse\n"
                         + "than a coarse one.");
    }

    private void DrawBlocks()
    {
        ImGui.SliderInt("Building footprint (tiles)", ref _city.BuildingTiles, 1, 64);
        RebuildIfReleased();

        ImGui.SliderInt("Street width (tiles)", ref _city.StreetTiles, 1, 64);
        RebuildIfReleased();

        ImGui.SliderInt("Clear tiles around you", ref _city.ClearTiles, 0, 200);
        RebuildIfReleased();

        ImGui.SliderFloat("Draw distance (yalms, 0 = no limit)", ref _city.DrawDistance, 0f, 800f, "%.0f");

        ImGui.TextDisabled("Blocks sit on a fixed pitch of footprint + street. Placement caps at 400\n"
                         + "buildings and DRAWING caps at the nearest 64 — that second cap, not the\n"
                         + "first, is what keeps a huge city affordable, since cost has to be bounded\n"
                         + "by something that does not grow with the extent.");
    }

    private void DrawHeights()
    {
        ImGui.SliderFloat("Shortest (yalms)", ref _city.MinHeight, 3f, 120f, "%.0f");
        RebuildIfReleased();

        ImGui.SliderFloat("Tallest (yalms)", ref _city.MaxHeight, 3f, 200f, "%.0f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            // Kept in order here rather than at use: a max below the min silently produces a city of
            // identical shortest buildings, which reads as the randomiser being broken.
            if (_city.MaxHeight < _city.MinHeight) _city.MaxHeight = _city.MinHeight;
            _city.Rebuild();
        }

        ImGui.SliderFloat("Floor height (yalms)", ref _city.FloorHeight, 1.5f, 8f, "%.2f");
        RebuildIfReleased();

        ImGui.TextDisabled("Heights round to a whole number of floors, so the top window row is never\n"
                         + "a sliver. The range is honoured to within one storey.");
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

        ImGui.TextDisabled("Which windows are lit is fixed per building at build time — rolling it per\n"
                         + "frame would strobe the whole city. Wall shading comes from one fixed\n"
                         + "light, which is what makes five flat fills read as a solid.");
    }

    /// <summary>
    /// The three switches that separate the three ways the depth route can submit a draw and still
    /// show nothing.
    ///
    /// <para><b>Each one answers exactly one question and is silent about the others.</b> That
    /// separation is the whole value: a combined "debug mode" that changed all three at once could
    /// not tell you which one mattered. They are also all independent of the occlusion RADIO —
    /// selecting "Off" there switches to the painted renderer and tests none of this, which is
    /// precisely the trap that cost a round of testing.</para>
    /// </summary>
    private void DrawDepthDiagnostics()
    {
        ImGui.Separator();

        if (!ImGui.CollapsingHeader("Diagnostics — why is nothing drawing?"))
            return;

        ImGui.TextDisabled("Three switches, three different questions. Turn ON one at a time.");
        ImGui.Spacing();

        // ── 1. presentation ──────────────────────────────────────────────────
        ImGui.Checkbox("1 · Split presentation probe (cyan | magenta)", ref _city.DebugClearOnly);
        ImGui.TextDisabled("Draws NO geometry. Splits the screen in two:\n"
                         + "  LEFT  half  cyan, via AddQuadFilled - NO texture at all.\n"
                         + "  RIGHT half  magenta, via AddImageQuad - our D3D surface.\n"
                         + "\n"
                         + "  cyan + magenta -> presentation works; fault is in the geometry.\n"
                         + "  cyan only      -> image/texture presentation is the fault.\n"
                         + "  neither        -> this code is not running; look upstream.\n"
                         + "\n"
                         + "The left half is the CONTROL, and the first version of this probe\n"
                         + "did not have one — so \"no magenta\" meant either \"the image did\n"
                         + "not present\" or \"this never ran\", which need opposite fixes. A\n"
                         + "probe that cannot separate those answers nothing.");

        ImGui.Spacing();

        // ── 2. the scene-depth comparison ────────────────────────────────────
        ImGui.Checkbox("2 · Bypass the scene-depth comparison", ref _city.DebugBypassSceneDepth);
        ImGui.TextDisabled("Keeps the D3D route and its own depth buffer, but stops comparing\n"
                         + "against the game's captured depth.\n"
                         + "  city appears -> the comparison was rejecting every fragment.\n"
                         + "  still blank  -> the comparison is innocent; look at transform or\n"
                         + "                  presentation instead.\n"
                         + "This is NOT the same as the \"Off\" radio above, which leaves the\n"
                         + "D3D renderer entirely and draws the painted city.");

        ImGui.Spacing();

        // ── 3. the transform ─────────────────────────────────────────────────
        ImGui.TextUnformatted("3 · Where our matrix puts you, vs. where Dalamud does");
        ImGui.TextDisabled("Always on — pure CPU arithmetic, no GPU involved. The painted\n"
                         + "renderer has always worked and it projects through Dalamud's\n"
                         + "WorldToScreen, so that is a trusted second opinion for the same\n"
                         + "question. Both pixel figures roughly equal, and near the middle of\n"
                         + "the screen when you are centred -> the transform is fine. Wildly\n"
                         + "different, or w<=0 while you are plainly on screen -> the matrix is\n"
                         + "the fault and nothing downstream is worth looking at yet.");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Ok);
        ImGui.TextUnformatted(_city.DebugProjection);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Separator();

        // ── 4. dump it all to the log ────────────────────────────────────────
        if (ImGui.Button("Dump one frame to the Dalamud log"))
            _city.DebugLogNextFrame = true;

        ImGui.TextDisabled("Writes every number above — plus the full ViewProj, the mesh and\n"
                         + "submission counts, the SRV pointer, the viewport and target sizes,\n"
                         + "and the depth-capture format — to dalamud.log as [City] TRACE lines.\n"
                         + "ONE frame, then it disarms itself; at 60 Hz a per-frame log is\n"
                         + "unreadable within seconds. Click it once with the depth mode selected\n"
                         + "and the log can be read directly, instead of someone retyping\n"
                         + "numbers off a panel.");
    }

    private void DrawOcclusion()
    {
        int mode = (int)_city.Mode;

        if (ImGui.RadioButton("Off — draw everything", ref mode, (int)CityOcclusion.None))
            _city.Mode = CityOcclusion.None;

        if (ImGui.RadioButton("Collision raycasts (approximate)", ref mode, (int)CityOcclusion.Raycast))
            _city.Mode = CityOcclusion.Raycast;

        if (ImGui.RadioButton("D3D11 depth buffer (per-pixel)", ref mode, (int)CityOcclusion.DepthBuffer))
            _city.Mode = CityOcclusion.DepthBuffer;

        _city.Mode = (CityOcclusion)mode;

        ImGui.Separator();

        // Which route ACTUALLY ran, not which one is selected. The depth path falls back to the
        // painted one on any failure, in the same frame — and a silent fallback would read as the
        // depth path working badly rather than as it not running at all.
        if (_city.Mode == CityOcclusion.DepthBuffer)
        {
            // SAY WHAT WAS MEASURED, NOT WHAT WAS HOPED. This line read "ACTIVE — real geometry,
            // depth-tested per pixel" through the whole of 0.84.54.9, during which a zero colour-write
            // mask meant the GPU never wrote a pixel. Nothing on this path inspects the finished
            // surface, so the strongest honest claim is that the work was submitted.
            if (_city.UsingDepthRenderer && _city.DepthSubmitted)
                ImGui.TextColored(Ok, "SUBMITTED — geometry handed to the GPU this frame\n"
                                    + "(nothing here reads the finished surface, so \"submitted\"\n"
                                    + "is the strongest claim this panel can make — your eyes\n"
                                    + "are the only check that the city actually appeared)");
            else if (_city.UsingDepthRenderer)
                ImGui.TextColored(Warn, "RAN, BUT DREW NOTHING — the mesh was empty this frame");
            else
                ImGui.TextColored(Warn, "SELECTED BUT NOT RUNNING — painted fallback is drawing");

            ImGui.TextDisabled($"d3d: {_city.DepthStatus}");

            if (_city.DepthAvailable && !_city.DepthCaptured)
                ImGui.TextColored(Warn, "geometry is drawing but the game's depth buffer was not\n"
                                      + "captured — everything will appear unoccluded");

            ImGui.SliderFloat("Depth bias", ref _city.DepthBias, 0f, 0.0005f, "%.6f");
            ImGui.TextDisabled("Raise this if a building's base speckles against the ground it\n"
                             + "stands on; lower it if a wall shows through something in front of\n"
                             + "it. Reverse-Z is non-linear, so the right value up close is not the\n"
                             + "right value at 200 yalms — which is why it is a knob.");

            DrawDepthDiagnostics();
        }

        ImGui.Separator();

        ImGui.TextDisabled("D3D11 DEPTH BUFFER is the real thing and the default. Every occluder the\n"
                         + "game drew counts — characters, foliage, particles, all of it — and the\n"
                         + "test is per pixel. Buildings become genuine triangles with their own\n"
                         + "depth buffer, so they occlude EACH OTHER correctly too; the painter's\n"
                         + "sort and the back-face cull are gone, along with the sign error a\n"
                         + "winding test invites.\n"
                         + "\n"
                         + "How: CopyResource the game's depth-stencil, draw the city into our own\n"
                         + "offscreen target, discard any fragment the scene depth says is behind\n"
                         + "the world, then blit that surface to the ImGui background list. ZERO\n"
                         + "render hooks — ported from FFXIV-TV's CopyBlitRenderer and XivMediaPlayer's\n"
                         + "DepthTestedRenderer, whose hook-based sibling took dozens of versions to\n"
                         + "stabilise and buys only HUD-over-geometry, which we never needed.\n"
                         + "\n"
                         + "COLLISION RAYCASTS are kept as the fallback and for comparison: only the\n"
                         + "collision mesh occludes (no characters or foliage) and edges are blocky\n"
                         + "at a 3x3 grid per face. Flipping between the two is how the difference\n"
                         + "gets judged.");
    }

    // ── tab 2: the goal area ─────────────────────────────────────────────────

    private void DrawGoalTab()
    {
        ImGui.TextColored(Head, "TEST 2 — CIRCULAR GOAL AREA");
        ImGui.TextColored(Rule,
            "A circle on the ground whose wall pulses up from it and fades, repeatedly.\n"
          + "Walk in and it turns green, plays the objective cue and says so once.");

        ImGui.Separator();

        if (ImGui.Button("Place goal area here")) Say(_goal.Place());

        ImGui.SameLine();

        ImGui.BeginDisabled(!_goal.IsPlaced);
        if (ImGui.Button("Move here")) Say(_goal.Place());
        ImGui.SameLine();
        if (ImGui.Button("Clear")) Say(_goal.Clear());
        ImGui.EndDisabled();

        ImGui.TextDisabled("the ring's ground is measured ONCE at placement — a pulse redraws every\n"
                         + "frame, and a ray per vertex per frame would be thousands a second");

        if (_goal.IsPlaced)
        {
            float d = _goal.EdgeDistance;

            if (_goal.IsInside)
                ImGui.TextColored(Ok, $"INSIDE — {-d:0.#}y past the edge · {_goal.RingPoints} ring point(s)");
            else
                ImGui.TextDisabled($"outside — {d:0.#}y to the edge · {_goal.RingPoints} ring point(s)");
        }
        else
        {
            ImGui.TextColored(Warn, "NOT PLACED — press Place.");
        }

        ImGui.Separator();

        Section("Shape — radius, height, segments", DrawGoalShape);
        Section("Pulse — speed and how many", DrawGoalPulse);
        Section("Look — colour, opacity, falloff", DrawGoalLook);
    }

    private void DrawGoalShape()
    {
        ImGui.SliderFloat("Radius (yalms)", ref _goal.Radius, 1f, 60f, "%.1f");
        RemeasureIfReleased();

        ImGui.SliderFloat("Wall height (yalms)", ref _goal.WallHeight, 0.5f, 40f, "%.1f");

        ImGui.SliderInt("Ring segments", ref _goal.Segments, 8, 96);
        RemeasureIfReleased();

        ImGui.Checkbox("Ground disc", ref _goal.ShowDisc);
        ImGui.SameLine();
        ImGui.Checkbox("Ground ring", ref _goal.ShowRing);

        ImGui.TextDisabled("Radius and segments re-measure the ring; height does not, since the wall's\n"
                         + "height is a per-frame value and the ground it stands on has not moved.");
    }

    private void DrawGoalPulse()
    {
        ImGui.SliderFloat("Pulse length (seconds)", ref _goal.PulseSeconds, 0.2f, 8f, "%.2f");
        ImGui.SliderInt("Pulses per burst", ref _goal.PulseCount, 1, 6);

        ImGui.SliderFloat("Delay between bursts (seconds)", ref _goal.PulseDelay, 0f, 10f, "%.2f");
        ImGui.SliderFloat("+ random up to (seconds)", ref _goal.PulseDelayRandom, 0f, 10f, "%.2f");

        ImGui.TextDisabled("Pulses are staggered by an even share of the rise, so N of them are\n"
                         + "evenly spaced within the burst whatever N is. Height eases out — a\n"
                         + "linear rise reads as a loading bar — and the fade is held at full for\n"
                         + "the first 55%% so the wall is solid while it is still growing.\n"
                         + "\n"
                         + "THE DELAY IS MEASURED FROM THE END OF THE BURST, not from the start of\n"
                         + "the last pulse: the final pulse begins part-way through the rise and\n"
                         + "still needs a full rise to finish, so timing it any other way would\n"
                         + "start the next burst over the tail of the old one and the gap would not\n"
                         + "be the gap on the slider. At 0 delay bursts run back to back, which is\n"
                         + "what this did before the delay existed.\n"
                         + "\n"
                         + "The random amount is drawn ONCE when a cycle rolls over. Rolling it per\n"
                         + "frame would not be a random delay — the wait would end on whichever\n"
                         + "frame happened to draw a low number, which is shorter on average than\n"
                         + "the slider says and never actually the slider's value.");
    }

    private void DrawGoalLook()
    {
        ImGui.ColorEdit3("Colour", ref _goal.Rgb);

        ImGui.Checkbox("Turn green inside", ref _goal.TintOnEntry);

        if (_goal.TintOnEntry)
        {
            ImGui.ColorEdit3("Inside colour", ref _goal.InsideRgb);
            ImGui.SliderFloat("Tint fade (seconds)", ref _goal.TintFadeSeconds, 0.05f, 5f, "%.2f");
            ImGui.TextDisabled("Crossfades both ways, and from wherever it got to — leaving and\n"
                             + "re-entering mid-fade continues rather than snapping. Placing the\n"
                             + "area starts AT the inside colour, because you are standing in it.");
        }

        ImGui.SliderFloat("Base opacity", ref _goal.BaseAlpha, 0.05f, 1f, "%.2f");
        ImGui.SliderFloat("Fade curve", ref _goal.FadeCurve, 0.3f, 4f, "%.2f");

        ImGui.Checkbox("Hide what the world blocks", ref _goal.Occlude);
        ImGui.TextDisabled("OFF by default, unlike the city: this is a marker you are meant to find,\n"
                         + "and an objective you cannot see through a fence is a worse failure than\n"
                         + "one that shows through it. One ray per segment when on, not the nine a\n"
                         + "city face costs.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-runs the city build when the slider just released. <b>On release, never per frame</b> — a
    /// build re-measures the ground, and doing that while a slider is dragged would fire thousands of
    /// rays a second.
    /// </summary>
    private void RebuildIfReleased()
    {
        if (ImGui.IsItemDeactivatedAfterEdit()) _city.Rebuild();
    }

    /// <summary>Same contract for the goal area's ring.</summary>
    private void RemeasureIfReleased()
    {
        if (ImGui.IsItemDeactivatedAfterEdit()) _goal.Remeasure();
    }

    /// <summary>One collapsible block, closed by default. ImGui remembers each header's state in its
    /// own ini, so a section opened once stays open across sessions.</summary>
    private static void Section(string title, Action body)
    {
        if (!ImGui.CollapsingHeader(title)) return;

        ImGui.Indent();

        // Guarded individually rather than relying on the caller's catch: one section throwing must
        // not take the rest of the tab with it, and an Unindent that never ran would shift every
        // window drawn afterwards.
        try { body(); }
        catch (Exception ex) { Diag.Error($"[City] section '{title}' failed: {ex.Message}"); }

        ImGui.Unindent();
    }

    private static void Say(string message) => Plugin.ChatGui.Print("[Challenges] " + message);
}
#endif
