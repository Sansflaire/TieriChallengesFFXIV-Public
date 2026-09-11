#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. The PLAYER'S reference map for an activity — what the clue vocabulary means,
/// drawn on the map they are looking at.
///
/// <para><b>It is a different window from <see cref="DigMapWindow"/> and not a mode of it, because it
/// answers a different person's question.</b> The debug map exists to catch a generator placing a
/// spot somewhere impossible, so it plots candidate spots, hand-drawn null zones, per-gate rejections
/// and a pathfinding verifier — every one of which is either an answer ("here is where a spot went")
/// or an authoring tool. Showing a player the candidate spots would hand them the solution to the
/// game, and showing them the null zones would show them our scaffolding.</para>
///
/// <para>What is left is exactly the vocabulary: where the map's middle is, which ninth is which,
/// which way 4 o'clock points, and what the named things a clue can mention actually are. A player
/// who reads "north-east of the Ingleside aethernet shard, in the southern third" can find both
/// halves of that sentence here without being told where to dig.</para>
///
/// <para><b>Raw ImGui, themed, and that is the sanctioned route rather than a shortcut.</b> The house
/// rule is that every player-facing surface matches the main window's style "whether or not it is
/// actually PanacheUI", with <see cref="DialogTheme"/> bracketing the Begin/End. PanacheUI cannot
/// draw this: a node's image is an SkiaSharp bitmap, the map is a game <c>.tex</c> handle, and the
/// overlay is a few hundred lines and arcs with no node equivalent. Converting the texture and
/// inventing a vector-primitive node would be a framework project, not a window.</para>
/// </summary>
internal sealed class ActivityMapWindow
{
    public bool IsVisible;

    private static float   _zoom = 1f;
    private static Vector2 _pan  = Vector2.Zero;
    private static bool    _panning;

    private static bool _showRegions  = true;
    private static bool _showClock    = true;
    private static bool _showAnchors  = true;
    private static bool _showYou      = true;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(680, 780), ImGuiCond.FirstUseEver);

        // Push/Pop bracket the Begin itself and BOTH run even when Begin returns false, or the style
        // stack goes unbalanced for every window drawn after this one.
        DialogTheme.Push();

        bool open = ImGui.Begin("Trail Reference Map###tc_activity_map", ref IsVisible);

        if (open)
        {
            try { Body(); }
            catch (Exception ex) { Diag.Error($"[Activity] reference map failed: {ex.Message}"); }
        }

        ImGui.End();
        DialogTheme.Pop();
    }

    private void Body()
    {
        uint mapId = DigLandmarks.CurrentMapId();

        if (mapId == 0)
        {
            ImGui.TextColored(DialogTheme.Danger, "This zone has no map.");
            return;
        }

        ImGui.TextColored(DialogTheme.TextMuted,
            "What the clue words mean, on the map you are standing in. No dig spots are shown.");

        ImGui.Checkbox("Regions", ref _showRegions);
        ImGui.SameLine(); ImGui.Checkbox("Clock", ref _showClock);
        ImGui.SameLine(); ImGui.Checkbox("Named places", ref _showAnchors);
        ImGui.SameLine(); ImGui.Checkbox("You", ref _showYou);
        ImGui.SameLine();
        if (ImGui.Button("Reset view")) { _zoom = 1f; _pan = Vector2.Zero; }

        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        float side = MathF.Max(64f, MathF.Min(avail.X, avail.Y - LegendH));

        var origin   = ImGui.GetCursorScreenPos();
        var rect     = new Vector2(side, side);
        var drawList = ImGui.GetWindowDrawList();

        HandleZoomPan(origin, rect);

        // Clipped to the frame. Zoomed in, the image is several times the frame, and the window's own
        // clip does not bound it because the frame is smaller than the window.
        drawList.PushClipRect(origin, origin + rect, true);

        var tex = DigMapWindow.MapTextureFor(mapId, out _);

        if (tex != null)
            drawList.AddImage(tex.Handle, origin + _pan * _zoom, origin + _pan * _zoom + rect * _zoom);
        else
            drawList.AddRectFilled(origin, origin + rect,
                                   ImGui.GetColorU32(new Vector4(0.10f, 0.09f, 0.12f, 1f)));

        float span = MathF.Max(1f, DigLandmarks.MapSpan);

        bool Plot(Vector3 world, out Vector2 at)
        {
            at = default;
            if (!DigLandmarks.TryMapCoords(world, out float mx, out float my)) return false;

            at = origin + _pan * _zoom
               + new Vector2((mx - 1f) / span * side, (my - 1f) / span * side) * _zoom;
            return true;
        }

        if (DigLandmarks.WalkableBox(out var lo, out var hi))
        {
            if (_showRegions) DrawRegions(drawList, Plot, lo, hi);
            if (_showClock)   DrawClock(drawList, Plot, lo, hi);
        }

        if (_showAnchors) DrawAnchors(drawList, Plot);
        if (_showYou)     DrawYou(drawList, Plot);

        drawList.PopClipRect();
        drawList.AddRect(origin, origin + rect, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.30f)));

        ImGui.Dummy(rect);
        DrawLegend();
    }

    private const float LegendH = 74f;

    // ── the three overlays ───────────────────────────────────────────────────

    private delegate bool PlotFn(Vector3 world, out Vector2 at);

    /// <summary>
    /// The 3×3 grid a region clue names, with each ninth labelled the way a clue would say it.
    ///
    /// <para>Labelled with the SHORT form — "NW", "N", "centre" — rather than the clue's own full
    /// phrase. The full phrases are up to sixty characters and nine of them would cover the map; the
    /// legend below carries the translation once instead of nine times.</para>
    /// </summary>
    private static void DrawRegions(ImDrawListPtr drawList, PlotFn plot, Vector2 lo, Vector2 hi)
    {
        uint line  = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.45f, 0.40f));
        uint label = ImGui.GetColorU32(new Vector4(1f, 0.90f, 0.60f, 0.85f));

        float w = hi.X - lo.X, d = hi.Y - lo.Y;

        // Thirds, drawn edge to edge. Two lines each way rather than a full grid of boxes: the frame
        // is the walkable box itself, which is drawn once below.
        for (int i = 1; i <= 2; i++)
        {
            float x = lo.X + w * i / 3f;
            float z = lo.Y + d * i / 3f;

            if (plot(new Vector3(x, 0f, lo.Y), out var a) && plot(new Vector3(x, 0f, hi.Y), out var b))
                drawList.AddLine(a, b, line, 1.5f);

            if (plot(new Vector3(lo.X, 0f, z), out var c) && plot(new Vector3(hi.X, 0f, z), out var e))
                drawList.AddLine(c, e, line, 1.5f);
        }

        if (plot(new Vector3(lo.X, 0f, lo.Y), out var boxA) &&
            plot(new Vector3(hi.X, 0f, hi.Y), out var boxB))
            drawList.AddRect(boxA, boxB, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.45f, 0.75f)), 0f, 0, 2f);

        string[] names =
        {
            "NW", "N", "NE",
            "W",  "middle", "E",
            "SW", "S", "SE",
        };

        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 3; col++)
        {
            float cx = lo.X + w * (col + 0.5f) / 3f;
            float cz = lo.Y + d * (row + 0.5f) / 3f;

            if (!plot(new Vector3(cx, 0f, cz), out var at)) continue;

            string t = names[row * 3 + col];
            drawList.AddText(at - new Vector2(t.Length * 3.2f, 6f), label, t);
        }
    }

    /// <summary>
    /// The twelve clock cones, and the dead zone in the middle where no bearing is given.
    ///
    /// <para><b>Boundaries sit on the half-hours and the hour label sits in the middle of its own
    /// cone</b>, which is what <c>round(bearing / 30)</c> means geometrically. Clipped to the box by
    /// ray-versus-rectangle rather than drawn as a circle: the search area is a rectangle, so a dial
    /// would claim area outside it and stop short inside it.</para>
    /// </summary>
    private static void DrawClock(ImDrawListPtr drawList, PlotFn plot, Vector2 lo, Vector2 hi)
    {
        float cx = (lo.X + hi.X) * 0.5f;
        float cz = (lo.Y + hi.Y) * 0.5f;

        var centre = new Vector3(cx, 0f, cz);
        if (!plot(centre, out var centreAt)) return;

        uint line  = ImGui.GetColorU32(new Vector4(0.55f, 0.80f, 1f, 0.45f));
        uint label = ImGui.GetColorU32(new Vector4(0.75f, 0.90f, 1f, 0.95f));

        float span = MathF.Max(1f, MathF.Max(hi.X - lo.X, hi.Y - lo.Y));

        // The same 6% the writer refuses to give a bearing inside. Drawn so a player can see that
        // "right around the middle of the map" is a real area and not a point.
        if (plot(new Vector3(cx + span * 0.06f, 0f, cz), out var edgeAt))
            drawList.AddCircle(centreAt, Vector2.Distance(centreAt, edgeAt), line, 32, 1.5f);

        bool TryEdge(float bearing, out Vector3 edge)
        {
            edge = centre;

            float rad = bearing * MathF.PI / 180f;

            // +X is east, north is -Z — the same convention DigGround.Compass states its evidence for.
            float dx = MathF.Sin(rad);
            float dz = -MathF.Cos(rad);

            float t = float.MaxValue;

            if (MathF.Abs(dx) > 0.0001f) t = MathF.Min(t, ((dx > 0f ? hi.X : lo.X) - cx) / dx);
            if (MathF.Abs(dz) > 0.0001f) t = MathF.Min(t, ((dz > 0f ? hi.Y : lo.Y) - cz) / dz);

            if (t <= 0f || t == float.MaxValue) return false;

            edge = new Vector3(cx + dx * t, 0f, cz + dz * t);
            return true;
        }

        for (int k = 0; k < 12; k++)
        {
            if (TryEdge(k * 30f + 15f, out var boundary) && plot(boundary, out var to))
                drawList.AddLine(centreAt, to, line, 1.5f);

            int hour = k == 0 ? 12 : k;

            if (TryEdge(k * 30f, out var mid) &&
                plot(Vector3.Lerp(centre, mid, 0.62f), out var at))
                drawList.AddText(at - new Vector2(6f, 7f), label, hour.ToString());
        }
    }

    /// <summary>
    /// Every named thing a clue is allowed to point at, drawn from the same sources the clue writer
    /// reads.
    ///
    /// <para><b>Called rather than re-listed, so the map cannot show a landmark the writer will not
    /// use or hide one it will.</b> That includes the ban list: a Striking Dummy is excluded here for
    /// the same reason it is excluded from a clue, and by the same call.</para>
    /// </summary>
    private static void DrawAnchors(ImDrawListPtr drawList, PlotFn plot)
    {
        var here = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;

        Layer(DigClueSources.AetherytesNear(here), new Vector4(1f, 0.80f, 0.30f, 0.95f), true);
        Layer(DigClueSources.Sections(),           new Vector4(0.65f, 0.55f, 1f, 0.95f), true);
        Layer(DigClueSources.Landmarks(),          new Vector4(0.30f, 0.85f, 0.95f, 0.85f), false);
        Layer(DigClueSources.MapNpcs(),            new Vector4(0.40f, 1f, 0.60f, 0.90f), false);
        Layer(DigClueSources.NearbyEnemies(here),  new Vector4(1f, 0.45f, 0.75f, 0.95f), true);

        void Layer(IReadOnlyList<DigClueSources.Anchor> anchors, Vector4 colour, bool named)
        {
            uint dot = ImGui.GetColorU32(colour);
            uint txt = ImGui.GetColorU32(colour with { W = 0.95f });

            foreach (var a in anchors)
            {
                if (!plot(a.World, out var at)) continue;

                drawList.AddCircleFilled(at, 3.5f, dot);

                // Only the sparse layers get labels. The landmark layer on a housing ward is a
                // hundred plot numbers, and labelling those made the debug map unreadable — the same
                // decision, for the same reason, and the dots still say where they are.
                if (named) drawList.AddText(at + new Vector2(5f, -6f), txt, a.Name);
            }
        }
    }

    private static void DrawYou(ImDrawListPtr drawList, PlotFn plot)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;
        if (!plot(player.Position, out var at)) return;

        uint white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f));

        drawList.AddCircleFilled(at, 5f, ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 0.85f)));
        drawList.AddCircleFilled(at, 3.5f, white);
        drawList.AddText(at + new Vector2(7f, -6f), white, "you");
    }

    private static void DrawLegend()
    {
        ImGui.Separator();

        ImGui.TextColored(new Vector4(1f, 0.90f, 0.60f, 0.9f), "Gold grid");
        ImGui.SameLine();
        ImGui.TextColored(DialogTheme.TextMuted,
            "— the map's ninths. \"the northern third\", \"the middle ninth\", and so on.");

        ImGui.TextColored(new Vector4(0.75f, 0.90f, 1f, 0.95f), "Blue spokes");
        ImGui.SameLine();
        ImGui.TextColored(DialogTheme.TextMuted,
            "— clock bearings from the middle. Inside the small circle, no bearing is given.");

        ImGui.TextColored(new Vector4(1f, 0.80f, 0.30f, 0.95f), "Gold dots");
        ImGui.SameLine(); ImGui.TextColored(DialogTheme.TextMuted, "aetherytes and shards.");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.65f, 0.55f, 1f, 0.95f), "Violet");
        ImGui.SameLine(); ImGui.TextColored(DialogTheme.TextMuted, "map sections.");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.30f, 0.85f, 0.95f, 0.95f), "Cyan");
        ImGui.SameLine(); ImGui.TextColored(DialogTheme.TextMuted, "map labels.");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.40f, 1f, 0.60f, 0.95f), "Green");
        ImGui.SameLine(); ImGui.TextColored(DialogTheme.TextMuted, "NPCs.");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.45f, 0.75f, 0.95f), "Pink");
        ImGui.SameLine(); ImGui.TextColored(DialogTheme.TextMuted, "enemy groups.");
    }

    // ── zoom and pan ─────────────────────────────────────────────────────────

    private void HandleZoomPan(Vector2 origin, Vector2 rect)
    {
        // AN INVISIBLE BUTTON, NOT A HOVER TEST. Draw-list graphics are not ImGui items, so dragging
        // over them moves the WINDOW — which is what happened to every drag handle on the debug map
        // before this. The button claims the rectangle so the drag belongs to the map.
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##activity_map_pan", rect);

        bool over = ImGui.IsItemHovered();

        float wheel = ImGui.GetIO().MouseWheel;

        if (over && MathF.Abs(wheel) > 0.001f)
        {
            float before = _zoom;
            _zoom = Math.Clamp(_zoom * (1f + wheel * 0.15f), 1f, 12f);

            // Keep the point under the cursor still. Without this, zooming walks the map away from
            // whatever you were looking at, which makes zoom useless past about 3x.
            var local = ImGui.GetMousePos() - origin;
            _pan = (_pan + local / before) - local / _zoom;
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) _panning = true;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _panning = false;

        if (_panning) _pan += ImGui.GetIO().MouseDelta / _zoom;

        // Reset the cursor so the caller's Dummy lands where it expects.
        ImGui.SetCursorScreenPos(origin);
    }
}
#endif
