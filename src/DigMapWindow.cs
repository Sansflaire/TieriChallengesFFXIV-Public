#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using LSheets = Lumina.Excel.Sheets;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. The game's own map image with every candidate dig spot plotted on it.
///
/// <para><b>This is the answer to "can a generated spot land somewhere impossible", and it answers
/// it by looking rather than by reasoning.</b> Placement is rejection sampling against raycast
/// geometry, which is not something that can be proved correct at a desk — the only honest check is
/// to draw a few hundred of them on the map a player would use and see whether any sit in the sea,
/// inside a wall, or off the edge entirely.</para>
///
/// <para><b>It also verifies the landmark conversion, which is the thing actually in doubt.</b> The
/// marker-to-world maths is an algebraic inverse of two formulas this repo spot-checked and never
/// confirmed in game. Landmarks are plotted in cyan: if each cyan dot sits on its own label on the
/// map image, the conversion is right and a clue that cannot be followed is merely a hard clue. If
/// they sit somewhere else, the conversion is the bug, and no amount of clue tuning would have
/// found that.</para>
/// </summary>
internal sealed class DigMapWindow
{
    public bool IsVisible;

    private readonly DigTests _tests;

    private List<Vector3> _spots = new();
    private string        _status = "Press Sample.";

    /// <summary>How many candidates a sample draws. Enough to show a pattern, not so many that a
    /// bad one hides in the crowd.</summary>
    private int _sampleCount = 200;

    /// <summary>Map the plotted spots belong to, so a zone change cannot leave stale dots.</summary>
    private uint _spotsForMap;

    public DigMapWindow(DigTests tests) => _tests = tests;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(720, 820), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Dig Map Debug  (DEV ONLY)###tc_dig_map", ref IsVisible))
        {
            ImGui.End();
            return;
        }

        try { Body(); }
        catch (Exception ex) { Diag.Error($"[Dig] map debug failed: {ex.Message}"); }

        ImGui.End();
    }

    private void Body()
    {
        uint mapId = DigLandmarks.CurrentMapId();

        ImGui.TextColored(Rule,
            "RED    every candidate spot placement would accept, plotted on the real map.\n"
          + "CYAN   the named landmarks clues are written from. If these do not sit on their own\n"
          + "       labels, the marker-to-world conversion is wrong and every clue is wrong with\n"
          + "       it — that is the single most useful thing this window tells you.\n"
          + "GREEN  you.\n"
          + "A red dot in the sea, inside a wall, or past the edge is a placement bug. A red dot\n"
          + "somewhere real but unreachable is the known limit: this has no navmesh, so a sealed\n"
          + "courtyard reads as ground.");

        ImGui.Spacing();
        ImGui.SliderInt("Samples", ref _sampleCount, 20, 1000);

        if (ImGui.Button("Sample"))
        {
            _spots       = _tests.Roam.SampleCandidates(_sampleCount);
            _spotsForMap = mapId;
            _status      = $"{_spots.Count} of {_sampleCount} placed.";

            if (_spots.Count < _sampleCount)
                _status += " Fewer than asked means placement ran out of valid ground — spacing, "
                         + "the roof test or the step test rejected the rest.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear")) { _spots.Clear(); _status = "cleared."; }

        ImGui.SameLine();
        ImGui.TextDisabled(_status);

        if (_spotsForMap != 0 && _spotsForMap != mapId)
            ImGui.TextColored(Warn, "These dots were sampled on a DIFFERENT map — sample again.");

        ImGui.Separator();

        if (!DigLandmarks.TryWorldBounds(out _, out _))
        {
            ImGui.TextColored(Warn, "No map bounds — is the map sheet readable here?");
            return;
        }

        DrawCanvas(mapId);
    }

    /// <summary>
    /// The map image with everything plotted over it.
    ///
    /// <para><b>Positions are placed by MAP coordinate, not by world coordinate scaled to the
    /// canvas.</b> The map image covers exactly map 1..1+span on both axes, so converting each point
    /// through the same world-to-map formula the game's own readout uses puts it where the game
    /// would put it. Scaling raw world coordinates to the canvas would be a second, different
    /// projection that happened to look plausible.</para>
    /// </summary>
    private void DrawCanvas(uint mapId)
    {
        DrawToggles();
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        float side = MathF.Max(64f, MathF.Min(avail.X, avail.Y));

        var origin   = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var rect     = new Vector2(side, side);

        var tex = MapTexture(mapId, out string path);

        if (tex != null)
        {
            drawList.AddImage(tex.Handle, origin, origin + rect);
        }
        else
        {
            // No image is a degraded view, not a broken one — the dots still carry the answer.
            drawList.AddRectFilled(origin, origin + rect, ImGui.GetColorU32(new Vector4(0.10f, 0.09f, 0.12f, 1f)));

            for (int i = 1; i < 8; i++)
            {
                float t = side * i / 8f;
                uint  g = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.08f));
                drawList.AddLine(origin + new Vector2(t, 0f), origin + new Vector2(t, side), g);
                drawList.AddLine(origin + new Vector2(0f, t), origin + new Vector2(side, t), g);
            }
        }

        drawList.AddRect(origin, origin + rect, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)));

        float span = MathF.Max(1f, DigLandmarks.MapSpan);

        bool Plot(Vector3 world, out Vector2 at)
        {
            at = default;
            if (!DigLandmarks.TryMapCoords(world, out float mx, out float my)) return false;

            at = origin + new Vector2((mx - 1f) / span * side, (my - 1f) / span * side);
            return true;
        }

        // Landmarks first, so a red spot sitting on one is still visible.
        uint cyan = ImGui.GetColorU32(new Vector4(0.30f, 0.85f, 0.95f, 0.95f));
        uint grey = ImGui.GetColorU32(new Vector4(0.62f, 0.66f, 0.72f, 0.85f));

        foreach (var l in DigLandmarks.ForCurrentMap())
        {
            // A HOUSING PLOT NUMBER IS NOT A LANDMARK, and on a ward map they outnumber the real
            // ones several to one — which is why this view was unreadable and why a clue once
            // anchored itself to "60". Split by whether the whole name is digits: that is exactly
            // what a plot marker is, and it needs no lookup table to decide.
            bool numbered = IsAllDigits(l.Name);

            if (numbered ? !_showPlots : !_showLandmarks) continue;
            if (!Plot(l.World, out var at)) continue;

            uint tint = numbered ? grey : cyan;

            drawList.AddCircleFilled(at, numbered ? 3f : 4f, tint, 12);

            if (_showLabels) drawList.AddText(at + new Vector2(6f, -7f), tint, l.Name);
        }

        if (_showClueAnchors) DrawClueAnchors(drawList, Plot);

        // THE WALKABLE BOX AND ITS QUADRANT LINES, drawn over the real map image.
        //
        // This exists because "dead centre" was reported for a spot three-quarters of the way east
        // and there was no way to see WHY without it. The box is what every direction word is
        // measured against, so drawing it turns an argument about a clue into a look at a rectangle:
        // if the box does not sit over the ward, the box is the bug; if it does, the wording is.
        if (_showBox && DigLandmarks.WalkableBox(out var boxLo, out var boxHi))
        {
            bool okA = Plot(new Vector3(boxLo.X, 0f, boxLo.Y), out var cornerA);
            bool okB = Plot(new Vector3(boxHi.X, 0f, boxHi.Y), out var cornerB);

            if (okA && okB)
            {
                var lo2 = new Vector2(MathF.Min(cornerA.X, cornerB.X), MathF.Min(cornerA.Y, cornerB.Y));
                var hi2 = new Vector2(MathF.Max(cornerA.X, cornerB.X), MathF.Max(cornerA.Y, cornerB.Y));

                uint lime = ImGui.GetColorU32(new Vector4(0.45f, 1f, 0.35f, 0.90f));
                uint dim  = ImGui.GetColorU32(new Vector4(0.45f, 1f, 0.35f, 0.35f));

                drawList.AddRect(lo2, hi2, lime, 0f, ImDrawFlags.None, 2f);

                // The QUADRANT boundaries: the centre cross plus the dead-band either side of it,
                // which is the band Quadrant() treats as "neither east nor west". Drawn because a
                // spot inside that band reads as "the middle" and looks off-centre on the map — the
                // exact thing that made this look broken when it was working.
                float cx = (lo2.X + hi2.X) * 0.5f, cy = (lo2.Y + hi2.Y) * 0.5f;
                float bx = (hi2.X - lo2.X) * 0.14f, by = (hi2.Y - lo2.Y) * 0.14f;

                drawList.AddLine(new Vector2(cx, lo2.Y), new Vector2(cx, hi2.Y), lime, 1.5f);
                drawList.AddLine(new Vector2(lo2.X, cy), new Vector2(hi2.X, cy), lime, 1.5f);

                foreach (float o in new[] { -1f, 1f })
                {
                    drawList.AddLine(new Vector2(cx + bx * o, lo2.Y), new Vector2(cx + bx * o, hi2.Y), dim);
                    drawList.AddLine(new Vector2(lo2.X, cy + by * o), new Vector2(hi2.X, cy + by * o), dim);
                }

                // Thirds, matching the EASY grid clue's nine cells.
                uint faint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.22f));

                for (int i = 1; i < 3; i++)
                {
                    float tx = lo2.X + (hi2.X - lo2.X) * i / 3f;
                    float ty = lo2.Y + (hi2.Y - lo2.Y) * i / 3f;

                    drawList.AddLine(new Vector2(tx, lo2.Y), new Vector2(tx, hi2.Y), faint);
                    drawList.AddLine(new Vector2(lo2.X, ty), new Vector2(hi2.X, ty), faint);
                }

                uint tag = ImGui.GetColorU32(new Vector4(0.45f, 1f, 0.35f, 0.95f));

                drawList.AddText(lo2 + new Vector2(4f, 4f), tag,
                    DigTuning.TryManualBox(Plugin.ClientState.TerritoryType, out _, out _, out _, out _)
                        ? "walkable box — SET BY HAND (drag the corners)"
                        : DigNavmesh.WalkableSampleCount > 0
                            ? $"walkable box — navmesh, {DigNavmesh.WalkableSampleCount} samples"
                            : "walkable box — derived (drag a corner to take it over by hand)");

                DragHandles(drawList, origin, rect, lo2, hi2, boxLo, boxHi, side);
            }
        }

        uint red = ImGui.GetColorU32(new Vector4(1f, 0.25f, 0.25f, 0.95f));
        int outside = 0;

        foreach (var s in _showSpots ? _spots : System.Linq.Enumerable.Empty<Vector3>())
        {
            if (!Plot(s, out var at)) continue;

            // Counted, not hidden. A dot outside the image is the exact failure this window was
            // built to catch, so it is drawn anyway — clamped to the border so it cannot be missed.
            bool off = at.X < origin.X || at.Y < origin.Y
                    || at.X > origin.X + side || at.Y > origin.Y + side;

            if (off)
            {
                outside++;
                at = Vector2.Clamp(at, origin, origin + rect);
                drawList.AddCircle(at, 7f, red, 12, 2f);
                continue;
            }

            drawList.AddCircleFilled(at, 3f, red, 10);
        }

        if (_showPlayer && Plugin.ObjectTable.LocalPlayer is { } player
                        && Plot(player.Position, out var me))
        {
            uint green = ImGui.GetColorU32(new Vector4(0.35f, 1f, 0.45f, 1f));
            drawList.AddCircleFilled(me, 5f, green, 14);
            drawList.AddCircle(me, 8f, green, 14, 1.5f);
        }

        ImGui.Dummy(rect);

        if (outside > 0)
            ImGui.TextColored(Warn, $"{outside} spot(s) fell OUTSIDE the map image — ringed on the border.");
        else if (_spots.Count > 0)
            ImGui.TextColored(Ok, $"All {_spots.Count} spot(s) are inside the map.");

        ImGui.TextDisabled(tex != null ? $"map texture: {path}" : $"no map texture at {path} — drawing a grid instead");
    }

    /// <summary>
    /// The map's background image.
    ///
    /// <para>The path is built from <c>Map.Id</c> ("s1h1/00" becomes
    /// <c>ui/map/s1h1/00/s1h1_00_m.tex</c>). <b>If that is wrong the window degrades to a grid and
    /// prints the path it tried</b> rather than failing silently — the dots are the point, and the
    /// image is context.</para>
    /// </summary>
    /// <summary>Which corner is being dragged: 0 none, 1 north-west, 2 south-east.</summary>
    private static int _dragCorner;

    /// <summary>
    /// Grab handles on the box's two corners, so the bounds can be set by dragging on the map.
    ///
    /// <para><b>This replaces walking to the corners, which was not actually possible.</b> A box's
    /// corner is frequently inside a building, on the far side of a wall, or outside the ward
    /// entirely — the very places a bounding box has to reach and a character cannot. Asking someone
    /// to stand at one was asking for the one thing the box exists to describe and the player cannot
    /// visit.</para>
    ///
    /// <para><b>It does go through the map-coordinate conversion, unlike the walk-there buttons</b>
    /// (which are kept for exactly that reason). That is acceptable here because it is
    /// self-consistent: the corner lands where it was dropped relative to the landmarks and the
    /// image, all of which are drawn through the same transform. If that transform is off, the
    /// dots will visibly not sit on their labels — which is the thing this window was built to
    /// show.</para>
    /// </summary>
    private static void DragHandles(ImDrawListPtr drawList, Vector2 origin, Vector2 rect,
                                    Vector2 lo2, Vector2 hi2,
                                    Vector2 boxLo, Vector2 boxHi, float side)
    {
        uint fill = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.90f));
        uint edge = ImGui.GetColorU32(new Vector4(0.10f, 0.35f, 0.10f, 1f));

        const float Grab = 7f;

        var nw = lo2;
        var se = hi2;

        foreach (var (at, id) in new[] { (nw, 1), (se, 2) })
        {
            bool active = _dragCorner == id;

            drawList.AddRectFilled(at - new Vector2(Grab), at + new Vector2(Grab),
                                   active ? edge : fill, 2f);
            drawList.AddRect(at - new Vector2(Grab), at + new Vector2(Grab),
                             active ? fill : edge, 2f);
        }

        // REAL ImGui ITEMS, not a hit test against the mouse position.
        //
        // Drawn handles are draw-list graphics: ImGui never knows they were clicked, so it saw a
        // drag on empty window background and moved the WINDOW instead — the corner could not be
        // grabbed at all. An InvisibleButton makes the click an item interaction, which both
        // suppresses the window move and gives correct press/hold/release semantics for free.
        //
        // The cursor is saved and restored around them: these are placed by absolute screen
        // position over an image drawn with the draw list, and leaving the layout cursor parked on
        // the last handle would push everything after it to the wrong place.
        var saveCursor = ImGui.GetCursorScreenPos();

        foreach (var (at, id) in new[] { (nw, 1), (se, 2) })
        {
            ImGui.SetCursorScreenPos(at - new Vector2(Grab + 3f));
            ImGui.InvisibleButton($"##boxcorner{id}", new Vector2((Grab + 3f) * 2f));

            if (ImGui.IsItemActive())  _dragCorner = id;
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }

        ImGui.SetCursorScreenPos(saveCursor);

        var mouse = ImGui.GetMousePos();

        if (_dragCorner != 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            // Screen -> map -> world, the exact inverse of how everything here is plotted.
            var clamped = Vector2.Clamp(mouse, origin, origin + rect);

            float span = MathF.Max(1f, DigLandmarks.MapSpan);
            float mx   = (clamped.X - origin.X) / side * span + 1f;
            float my   = (clamped.Y - origin.Y) / side * span + 1f;

            if (DigLandmarks.TryWorldFromMap(mx, my, out var world))
            {
                // Seed from the DERIVED box the first time, so taking it over by hand starts from
                // something sensible instead of collapsing to a point at the dragged corner.
                if (!DigTuning.BoxSet || DigTuning.BoxTerritory != Plugin.ClientState.TerritoryType)
                    DigTuning.SeedBox(Plugin.ClientState.TerritoryType,
                                      boxLo.X, boxLo.Y, boxHi.X, boxHi.Y);

                DigTuning.DragBoxCorner(_dragCorner == 1, Plugin.ClientState.TerritoryType,
                                        world.X, world.Z);
            }
        }

        if (_dragCorner != 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            _dragCorner = 0;
            DigTuning.FinishBoxDrag();
            DigLandmarks.Invalidate();
        }
    }

    // ── what the view draws ──────────────────────────────────────────────────
    //
    // Session state rather than persisted settings. These are "what am I looking at right now"
    // switches flicked while chasing one question, not preferences — and putting them in
    // dig-tuning.json would add six fields, six migrations and a reset path for something whose
    // right value changes three times in a minute.
    private static bool _showLandmarks  = true;
    private static bool _showPlots;                 // OFF by default: they outnumber everything
    private static bool _showLabels     = true;
    private static bool _showClueAnchors;
    private static bool _showBox        = true;
    private static bool _showSpots      = true;
    private static bool _showPlayer     = true;

    /// <summary>The display toggles. Drawn above the map, because they change what it means.</summary>
    private static void DrawToggles()
    {
        ImGui.Checkbox("Landmarks", ref _showLandmarks);
        ImGui.SameLine(); ImGui.Checkbox("Plot numbers", ref _showPlots);
        ImGui.SameLine(); ImGui.Checkbox("Labels", ref _showLabels);
        ImGui.SameLine(); ImGui.Checkbox("Box + quadrants", ref _showBox);

        ImGui.Checkbox("Candidate spots", ref _showSpots);
        ImGui.SameLine(); ImGui.Checkbox("You", ref _showPlayer);
        ImGui.SameLine(); ImGui.Checkbox("Clue anchors", ref _showClueAnchors);

        ImGui.TextDisabled("Plot numbers are off by default — on a housing map they outnumber the "
                         + "real landmarks several to one.");
    }

    /// <summary>
    /// The anchors each clue CATEGORY can actually reach for, in its own colour.
    ///
    /// <para>Separate from the landmark layer because they are a different question. Landmarks are
    /// what the map draws; these are what the clue writer can name — aethernet shards it found in
    /// the object table, NPC placements from the Level sheet, enemy group centroids. Seeing them
    /// on the map is how "why did every clue anchor to the same thing" gets answered.</para>
    /// </summary>
    private static void DrawClueAnchors(ImDrawListPtr drawList, PlotFn plot)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var around = player?.Position ?? Vector3.Zero;

        Layer(DigClueSources.AetherytesNear(around), new Vector4(1f, 0.85f, 0.25f, 0.95f));
        Layer(DigClueSources.Sections(),             new Vector4(0.65f, 0.55f, 1f, 0.95f));
        Layer(DigClueSources.MapNpcs(),              new Vector4(0.40f, 1f, 0.60f, 0.95f));
        Layer(DigClueSources.NearbyEnemies(around),  new Vector4(1f, 0.45f, 0.75f, 0.95f));

        void Layer(IReadOnlyList<DigClueSources.Anchor> anchors, Vector4 colour)
        {
            uint c = ImGui.GetColorU32(colour);

            foreach (var a in anchors)
            {
                if (!plot(a.World, out var at)) continue;

                drawList.AddCircle(at, 6f, c, 14, 2f);
                if (_showLabels) drawList.AddText(at + new Vector2(8f, 2f), c, a.Name);
            }
        }
    }

    private delegate bool PlotFn(Vector3 world, out Vector2 at);

    /// <summary>Whether a marker name is purely a housing plot number.</summary>
    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;

        foreach (char ch in s) if (ch < '0' || ch > '9') return false;
        return true;
    }

    private static Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? MapTexture(
        uint mapId, out string path)
    {
        path = string.Empty;

        try
        {
            var maps = Plugin.DataManager.GetExcelSheet<LSheets.Map>();
            if (maps?.GetRowOrDefault(mapId) is not { } map) return null;

            string id = map.Id.ExtractText();
            if (id.Length == 0) return null;

            // THE SLASH IS REMOVED, NOT REPLACED. Map.Id is "r1h1/00" and the texture is
            // ui/map/r1h1/00/r1h100_m.tex — the directory keeps the slash, the FILENAME drops it.
            // Building "r1h1_00_m.tex" is why every candidate missed and the window has been blank
            // since it was written: the loop looked thorough while every entry in it was wrong the
            // same way. The underscore form is kept last in case some map really does use it.
            string flat   = id.Replace("/", string.Empty);
            string scored = id.Replace("/", "_");

            // PROBED, not guessed. DataManager.FileExists asks the game's own index whether a path
            // is real, so the right one is found by checking rather than by my being confident about
            // a naming convention — and when none exists the window can say so instead of showing
            // an empty square that looks like a rendering bug.
            string[] candidates =
            {
                $"ui/map/{id}/{flat}_m.tex",     // the normal one
                $"ui/map/{id}/{flat}m_m.tex",    // some maps carry an 'm' variant
                $"ui/map/{id}/{flat}d.tex",
                $"ui/map/{id}/{flat}_s.tex",     // small version, better than nothing
                $"ui/map/{id}/{flat}.tex",
                $"ui/map/{id}/{scored}_m.tex",   // underscore form, last resort
            };

            // ASKED FOR, not pre-screened. An earlier version only tried a candidate that
            // DataManager.FileExists confirmed, and no image ever appeared — so either the probe
            // disagrees with the texture loader about what exists, or the loader can produce a
            // texture the index check rejects. Either way the check was filtering out the answer.
            // Asking the loader directly is the shorter question, and it is the one that matters.
            foreach (var c in candidates)
            {
                Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? wrap = null;

                try   { wrap = Plugin.TextureProvider.GetFromGame(c).GetWrapOrDefault(); }
                catch { /* not this one */ }

                if (wrap == null) continue;

                path = c;
                return wrap;
            }

            // Named so a wrong guess is reportable rather than just a blank square. A texture that
            // is merely still decoding also lands here for a frame or two and then resolves.
            path = $"map id \"{id}\" — none of {candidates.Length} candidate paths loaded "
                 + $"(first tried {candidates[0]})";
            return null;
        }
        catch { return null; }
    }

    private static readonly Vector4 Rule = new(0.68f, 0.68f, 0.76f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.62f, 0.35f, 1f);
    private static readonly Vector4 Ok   = new(0.44f, 0.86f, 0.62f, 1f);
}
#endif
