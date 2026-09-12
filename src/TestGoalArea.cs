#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY — <b>Test 2 of the Test City window: a circular goal area</b> whose wall
/// pulses up from the ground and fades out, repeatedly, like a beacon you are meant to walk into.
///
/// <para><b>It is a circle where the dig site is a box, and that is the whole reason it is separate
/// code rather than a flag.</b> <see cref="DigVolumeRender.DrawGradientWalls"/> takes a square
/// lattice and its outer ring IS the footprint, which is what gives it terrain-snapping for free; a
/// circle has no lattice and a fixed height, where this one's height changes every frame. What IS
/// shared is reused outright: the ground ring and disc come from
/// <see cref="DigVolumeRender.DrawGroundRing"/> / <see cref="DigVolumeRender.DrawGroundDisc"/>, and
/// the banded-quad technique is documented once, in that file.</para>
///
/// <para><b>The ground is measured ONCE, at placement.</b> A pulse redraws its wall every frame, and
/// a downward raycast per ring vertex per frame would be thousands a second for a decoration — the
/// mistake <see cref="DigSiteService"/>'s lattice exists to avoid. The ring's own heights are
/// sampled when it is placed and the interior is interpolated radially from them, so the disc
/// follows a slope without a single ray after the first frame.</para>
///
/// <para><b>Escape does not clear it</b>, same as the city and same as a running dig test: Escape
/// releases what the plugin has taken and never destroys placed state.</para>
/// </summary>
internal sealed class TestGoalService
{
    /// <summary>Vertical bands per wall segment. This is what makes the gradient smooth — ImGui has
    /// no per-vertex gradient for an arbitrary quad, so each band is filled at its own alpha.</summary>
    private const int Bands = 14;

    private const int MinSegments = 8;
    private const int MaxSegments = 96;

    /// <summary>Lifts drawn geometry clear of the surface so it does not vanish into a dip.</summary>
    private const float Lift = 0.05f;

    // ── settings, live from the panel ────────────────────────────────────────

    public float Radius     = 8f;
    public float WallHeight = 6f;
    public int   Segments   = 48;

    /// <summary>Seconds for one pulse to rise and fade.</summary>
    public float PulseSeconds = 1.8f;

    /// <summary>Concurrent pulses, evenly staggered — one wall rising while the last is still fading
    /// is what makes it read as a beacon rather than a blink.</summary>
    public int PulseCount = 2;

    public float BaseAlpha = 0.50f;

    /// <summary>Shapes the vertical falloff. Above 1 the fade happens low and most of the wall is
    /// faint, which keeps the area readable without boxing the player in visually.</summary>
    public float FadeCurve = 1.5f;

    public bool ShowDisc = true;
    public bool ShowRing = true;

    /// <summary>Hide the parts of the wall the world stands in front of. Default OFF, unlike the
    /// city: this is a marker you are meant to find, and an objective you cannot see through a fence
    /// is a worse failure than one that shows through it.</summary>
    public bool Occlude;

    /// <summary>Turn green while the player is inside. The one thing that makes this a test of
    /// something rather than an animation.</summary>
    public bool TintOnEntry = true;

    public Vector3 Rgb      = new(0.45f, 0.78f, 0.95f);
    public Vector3 InsideRgb = new(0.44f, 0.86f, 0.62f);

    // ── state ────────────────────────────────────────────────────────────────

    private bool      _placed;
    private Vector3   _centre;
    private float     _centreY;
    private Vector3[] _ring = Array.Empty<Vector3>();
    private uint      _territory;
    private long      _placedAtMs;
    private bool      _wasInside;

    public bool IsPlaced => _placed && _ring.Length >= 3;

    /// <summary>Whether the player is standing in the area right now. Flat distance: a goal is a
    /// place on the ground, and a player on a ledge above it has not reached it by walking.</summary>
    public bool IsInside
    {
        get
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (!_placed || lp == null) return false;

            return DigGround.Flat(lp.Position, _centre) <= MathF.Max(0.1f, Radius);
        }
    }

    public int RingPoints => _ring.Length;

    /// <summary>Distance to the edge, for the panel readout. Negative inside.</summary>
    public float EdgeDistance
    {
        get
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (!_placed || lp == null) return 0f;

            return DigGround.Flat(lp.Position, _centre) - MathF.Max(0.1f, Radius);
        }
    }

    // ── actions ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Places the area on the player's position and measures its ring.
    ///
    /// <para>Measuring here rather than lazily on the first draw is deliberate: the raycasts must run
    /// on the main thread, and the draw loop is the main thread either way — but doing it at
    /// placement means the cost is paid at a moment the player caused, not on an arbitrary frame.</para>
    /// </summary>
    public string Place()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        try
        {
            _centre     = player.Position;
            _territory  = Plugin.ClientState.TerritoryType;
            _placedAtMs = Environment.TickCount64;
            _wasInside  = true;      // the player is standing in it; do not announce an entry
            _placed     = true;

            Measure();

            return $"goal area placed — {Radius:0.#}y radius, {_ring.Length} ring point(s).";
        }
        catch (Exception ex)
        {
            Diag.Error($"[Goal] place failed: {ex.Message}");
            Clear();
            return "placement failed — see the log.";
        }
    }

    public string Clear()
    {
        bool had = _placed;

        _placed = false;
        _ring   = Array.Empty<Vector3>();

        return had ? "goal area cleared." : "no goal area to clear.";
    }

    /// <summary>Re-measures an area already on the ground. For the radius and segment sliders: a
    /// changed radius has to move the ring in front of you, not on the next placement.</summary>
    public void Remeasure()
    {
        if (!_placed) return;

        try { Measure(); }
        catch (Exception ex) { Diag.Error($"[Goal] remeasure failed: {ex.Message}"); }
    }

    /// <summary>
    /// Notices entry and exit, and drops the area on a zone change — its ring is a set of heights in
    /// one territory, and carried into another it would draw a circle hanging in the air.
    /// </summary>
    public void Tick()
    {
        if (!_placed) return;

        try
        {
            if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            {
                Clear();
                return;
            }

            if (Plugin.ClientState.TerritoryType != _territory)
            {
                Clear();
                Plugin.ChatGui.Print("[Challenges] Goal area cleared — you left the zone.");
                return;
            }

            bool inside = IsInside;

            // Edge-detected, both ways. A per-frame "are you inside" would fire a cue every frame the
            // player stood in it, which is the same mistake as re-rolling window lights per frame.
            if (inside && !_wasInside)
            {
                try { Plugin.Sound.Play(SoundService.Cue.ObjectiveProgress); }
                catch (Exception ex) { Diag.Error($"[Goal] entry cue failed: {ex.Message}"); }

                Plugin.ChatGui.Print("[Challenges] Goal reached.");
            }

            _wasInside = inside;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Goal] tick failed: {ex.Message}");
            Clear();
        }
    }

    // ── measurement ──────────────────────────────────────────────────────────

    /// <summary>Ground height at each ring vertex, plus the centre. One ray per vertex, once.</summary>
    private void Measure()
    {
        int n = Math.Clamp(Segments, MinSegments, MaxSegments);
        float r = MathF.Max(0.1f, Radius);

        _centreY = DigGround.GroundAt(_centre, _centre.X, _centre.Z).Y;

        var ring = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            float a = MathF.Tau * i / n;

            float x = _centre.X + r * MathF.Cos(a);
            float z = _centre.Z + r * MathF.Sin(a);

            var g = DigGround.GroundAt(_centre, x, z);
            ring[i] = new Vector3(x, g.Y, z);
        }

        _ring = ring;

        Diag.Info($"[Goal] ring measured: {n} point(s) at {r:0.##}y radius.");
    }

    /// <summary>
    /// Drops a point onto the measured floor by interpolating radially between the centre sample and
    /// the ring — no ray, and guaranteed to agree with the wall standing on the same ring.
    ///
    /// <para>Returns null outside the circle, which is how the disc and ring drawers are told to
    /// break their outline rather than smear a vertex to a wrong height.</para>
    /// </summary>
    private Vector3? Snap(Vector3 p)
    {
        if (_ring.Length < 3) return null;

        float r = MathF.Max(0.1f, Radius);
        float d = DigGround.Flat(p, _centre);

        if (d > r * 1.02f) return null;

        // Bearing to the nearest two ring samples, then linear between them — the same bilinear
        // reasoning the dig site applies to a square lattice, in polar form.
        float ang = MathF.Atan2(p.Z - _centre.Z, p.X - _centre.X);
        if (ang < 0f) ang += MathF.Tau;

        float f  = ang / MathF.Tau * _ring.Length;
        int   i0 = (int)f % _ring.Length;
        int   i1 = (i0 + 1) % _ring.Length;
        float t  = f - (int)f;

        float edgeY = _ring[i0].Y * (1f - t) + _ring[i1].Y * t;
        float y     = _centreY + (edgeY - _centreY) * Math.Clamp(d / r, 0f, 1f);

        return new Vector3(p.X, y + Lift, p.Z);
    }

    // ── in-world ─────────────────────────────────────────────────────────────

    /// <summary>The floor markings, then one wall per concurrent pulse.</summary>
    public void DrawWorld()
    {
        if (!IsPlaced) return;

        try
        {
            var rgb = TintOnEntry && IsInside ? InsideRgb : Rgb;

            // Read ONCE per frame and handed down, not re-read per pulse: six pulses would be six
            // native reads a frame for a value that cannot change between them.
            Vector3? eye = null;
            if (Occlude && TryEye(out var e)) eye = e;

            // Floor first, wall over it — the wall is the boundary and should read as being in front
            // of the ground it encloses.
            if (ShowDisc) DigVolumeRender.DrawGroundDisc(_centre, Radius, rgb, 0.20f, Snap);
            if (ShowRing) DigVolumeRender.DrawGroundRing(_centre, Radius, rgb, 0.90f, 2.4f, Snap);

            double elapsed = (Environment.TickCount64 - _placedAtMs) / 1000.0;
            float  period  = MathF.Max(0.15f, PulseSeconds);
            int    pulses  = Math.Clamp(PulseCount, 1, 6);

            for (int p = 0; p < pulses; p++)
            {
                // Staggered by an even share of the period, so N pulses are evenly spaced in time
                // whatever N is — a fixed stagger would bunch them up as the count rose.
                float phase = (float)((elapsed / period + p / (float)pulses) % 1.0);

                DrawPulse(phase, rgb, eye);
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Goal] world draw failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One pulse: a wall standing on the measured ring, scaled up from the ground and fading as it
    /// goes.
    ///
    /// <para><b>Height eases out and the envelope fades late.</b> A linear rise reads as a loading
    /// bar; easing out makes it leap and settle. The fade is held at full for the first 55% so the
    /// wall is solid while it is growing — fading from the first frame makes the pulse look like it
    /// is failing rather than rising.</para>
    /// </summary>
    private void DrawPulse(float phase, Vector3 rgb, Vector3? eye)
    {
        float ease = 1f - (1f - phase) * (1f - phase);
        float h    = MathF.Max(0.02f, WallHeight) * ease;

        float env = phase < 0.55f ? 1f : MathF.Max(0f, (1f - phase) / 0.45f);
        if (env <= 0.01f) return;

        var list = ImGui.GetBackgroundDrawList();

        int n = _ring.Length;

        for (int i = 0; i < n; i++)
        {
            var a = _ring[i];
            var b = _ring[(i + 1) % n];

            // One ray per segment when occlusion is on, at the segment's mid-height — not the nine a
            // city face costs. A pulse wall is thin and tall, and a single sample per segment already
            // gives the blocky-but-honest edge this route can offer.
            if (eye.HasValue)
            {
                var mid = new Vector3((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f + h * 0.5f,
                                      (a.Z + b.Z) * 0.5f);

                if (TestCityOcclusion.Blocked(eye.Value, mid)) continue;
            }

            DrawSegment(list, a, b, h, rgb, env);
        }

        // The rising edge, stroked brighter. This is what makes "scaling up from the ground" legible
        // — without it a fading gradient reads as the whole wall dimming rather than as a front
        // travelling upward.
        StrokeRing(list, h, rgb, MathF.Min(1f, 0.85f * env));
    }

    /// <summary>One span of wall between two adjacent ring points, sliced vertically for the
    /// gradient. Quads whose corners will not project are skipped rather than clamped: a point behind
    /// the camera has no meaningful screen position, and stretching a quad to a garbage coordinate
    /// paints a triangle across the whole viewport.</summary>
    private void DrawSegment(ImDrawListPtr list, Vector3 a, Vector3 b, float height, Vector3 rgb,
                             float env)
    {
        var up = new Vector3(0f, height, 0f);

        for (int r = 0; r < Bands; r++)
        {
            float v0 = r / (float)Bands;
            float v1 = (r + 1) / (float)Bands;

            float mid   = (v0 + v1) * 0.5f;
            float alpha = Math.Clamp(BaseAlpha, 0f, 1f) * env
                        * MathF.Pow(1f - mid, MathF.Max(0.1f, FadeCurve));

            if (alpha <= 0.004f) continue;   // below this it costs a quad and shows nothing

            uint col = ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, alpha));

            if (!AreaOverlay.Project(a + up * v0, out var s0)) continue;
            if (!AreaOverlay.Project(b + up * v0, out var s1)) continue;
            if (!AreaOverlay.Project(b + up * v1, out var s2)) continue;
            if (!AreaOverlay.Project(a + up * v1, out var s3)) continue;

            // Wound consistently around the quad — AddQuadFilled needs a convex ring, and a
            // figure-of-eight winding renders as two slivers rather than erroring.
            list.AddQuadFilled(s0, s1, s2, s3, col);
        }
    }

    /// <summary>The ring at a given height above its own measured ground. Breaks on a projection
    /// failure rather than joining across the gap.</summary>
    private void StrokeRing(ImDrawListPtr list, float height, Vector3 rgb, float alpha)
    {
        uint col = ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, alpha));

        Vector2? prev = null;

        for (int i = 0; i <= _ring.Length; i++)
        {
            var p = _ring[i % _ring.Length] + new Vector3(0f, height, 0f);

            if (!AreaOverlay.Project(p, out var s)) { prev = null; continue; }

            if (prev.HasValue) list.AddLine(prev.Value, s, col, 2f);
            prev = s;
        }
    }

    /// <summary>The camera position, for occlusion only. Same field-read path and same per-hop null
    /// checks as the city's — see <see cref="TestCityService"/>.</summary>
    private static unsafe bool TryEye(out Vector3 eye)
    {
        eye = default;

        var manager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        if (manager == null) return false;

        var camera = manager->Camera;
        if (camera == null) return false;

        var p = camera->SceneCamera.Position;
        eye = new Vector3(p.X, p.Y, p.Z);

        return true;
    }
}
#endif
