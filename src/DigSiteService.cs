#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Test 2 — Area Surveillance.</b> A square site is drawn into the world
/// as gradient walls. Somewhere inside it are <see cref="DigTuning.SitePieces"/> buried pieces; dig
/// close enough to one and you turn it up. Collect them all and you get the relic, which ends the
/// test and clears the site.
///
/// <para><b>The site is the whole hint.</b> There is no Sense here and no proximity warming — that
/// is Test 1's job. This one is about sweeping a bounded area methodically, so telling the player
/// they are getting warmer would remove the only thing being tested. The HUD shows the count and
/// the clock, nothing else.</para>
///
/// <para><b>Pieces are spaced apart on purpose.</b> Two buried within a dig radius of each other
/// would both come up on one dig, which reads as a bug even though it is not.</para>
/// </summary>
internal sealed class DigSiteService : IDigTest
{
    private enum Phase { Off, Placing, Digging, Won }

    private const long WonHoldMs = 15_000;

    /// <summary>
    /// Attempts allowed per piece. Generous, because later pieces have to satisfy the spacing rule
    /// against every earlier one and the last one in a cramped site can take a lot of throws.
    /// </summary>
    private const int AttemptsPerPiece = 400;

    private readonly Random        _rng    = new();
    private readonly List<Vector3> _pieces = new();
    private readonly List<Vector3> _found  = new();

    /// <summary>Roughly one ground sample every this many yalms, whatever the grid density.</summary>
    private const float TargetSpacing = 2f;

    /// <summary>Spokes and spacing for the step test. Eight directions catch any wall that crosses
    /// the circle; only a wall clipping a corner between spokes can slip past.</summary>
    private const int   StepRays       = 8;
    private const float StepSpacing    = 0.5f;
    private const int   MaxStepSamples = 16;

    /// <summary>
    /// Candidates that may be ray-tested in one frame. Each costs about
    /// <see cref="StepRays"/> × samples rays, so this is the knob that keeps placement off the
    /// frame budget.
    /// </summary>
    private const int RayTestsPerFrame = 20;

    /// <summary>Frames placement may take before giving up. ~0.7 s at 60 fps.</summary>
    private const int MaxPlaceFrames = 40;

    /// <summary>Ceiling on fine steps per visual cell, and on the lattice as a whole.</summary>
    private const int MaxSubdivisions   = 12;
    private const int MaxLatticePoints  = 45;

    /// <summary>Ceiling on grid cells, matching the lab slider's own upper bound.</summary>
    private const int MaxGridCells = 40;

    private Phase          _phase;
    private ChallengeArea? _site;

    /// <summary>
    /// Ground heights across the site, measured ONCE when it is marked out. A site never moves, so
    /// there is no reason to pay for a raycast per grid vertex per frame — see
    /// <see cref="DigVolumeRender.DrawGroundGrid"/>.
    /// </summary>
    private Vector3[,] _grid = new Vector3[0, 0];
    private int        _gridStride = 1;

    /// <summary>Frames spent placing so far — placement is spread, see <see cref="BuryPieces"/>.</summary>
    private int _placeTicks;
    private uint           _territory;
    private long           _startedAtMs;
    private long           _endedAtMs;
    private int            _digs;

    public string Name => "Area Surveillance";

    public bool IsActive => _phase != Phase.Off;

    public DigBand Band => _phase switch
    {
        Phase.Won     => DigBand.Green,
        Phase.Digging => InSite ? DigBand.Yellow : DigBand.Red,
        _             => DigBand.Cold,
    };

    public float? RadarCloseness => null;

    /// <summary>Total pieces buried, for the HUD and the lab readout.</summary>
    public int PieceTotal => _pieces.Count + _found.Count;

    public int PieceFound => _found.Count;

    public int Digs => _digs;

    /// <summary>Whether the player is currently standing inside the site they may search.</summary>
    public bool InSite
    {
        get
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            return _site != null && lp != null && _site.Contains(lp.Position);
        }
    }

    public double ElapsedSeconds => _phase switch
    {
        Phase.Digging => (Environment.TickCount64 - _startedAtMs) / 1000.0,
        Phase.Won     => (_endedAtMs - _startedAtMs) / 1000.0,
        _             => 0.0,
    };

    public string Headline => _phase switch
    {
        Phase.Won     => "YOU GOT THE RELIC!",
        Phase.Placing => "SURVEYING…",
        Phase.Digging => $"PIECES  {_found.Count} / {PieceTotal}",
        _             => "SURVEILLANCE",
    };

    public string Subtitle => _phase switch
    {
        Phase.Won     => $"{_digs} dig(s) to assemble it.",
        Phase.Placing => "Marking out the site…",
        Phase.Digging => InSite
                             ? "Dig anywhere inside the marked site."
                             : "Get back inside the marked site.",
        _             => string.Empty,
    };

    // ── the player's actions ─────────────────────────────────────────────────

    /// <summary>
    /// Marks out a site centred on the player and arms placement. As with the hunt, the burying
    /// itself happens in <see cref="Tick"/> so the raycasts are certain to run on the main thread.
    /// </summary>
    public string Start()
    {
        if (!PropService.CanPerform(out string why)) return "cannot start — " + why;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        float side = DigTuning.SiteSize;

        _site = new ChallengeArea
        {
            Name  = "Surveillance site",
            Shape = AreaShape.Box,
            SizeX = side,
            SizeZ = side,

            // Tall enough that standing on a rise inside the site still counts as being in it —
            // Contains is a fully 3D test, and a flat slab would exclude the player the moment the
            // ground was not perfectly level.
            SizeY = 40f,
            Scale = 1f,
        };
        _site.SetCenter(player.Position);

        _pieces.Clear();
        _found.Clear();
        _digs       = 0;
        _placeTicks = 0;
        _grid       = new Vector3[0, 0];
        _territory  = Plugin.ClientState.TerritoryType;
        _phase      = Phase.Placing;

        return $"marking out a {side:0}×{side:0} site…";
    }

    /// <summary>
    /// Digs. Only turns up a piece when the player is inside the site AND within
    /// <see cref="DigTuning.SitePieceRadius"/> of one — the site boundary is a rule, so a dig that
    /// straddled it from outside would make the drawn walls a lie.
    /// </summary>
    public string Dig()
    {
        string animation = Plugin.Props.Dig();

        if (_phase != Phase.Digging) return animation;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return animation;

        _digs++;

        if (_site == null || !_site.Contains(player.Position))
            return animation + " You are outside the site.";

        int hit = -1;
        for (int i = 0; i < _pieces.Count; i++)
        {
            if (DigGround.Flat(player.Position, _pieces[i]) <= DigTuning.SitePieceRadius) { hit = i; break; }
        }

        if (hit < 0) return animation + " Nothing but dirt here.";

        // Judged now, banked on completion — see IDigTest.DigFinished. The index is safe to hold
        // because nothing can change the list while a dig runs.
        _pendingPiece = hit;
        return animation + " You strike something…";
    }

    public void DigFinished(bool completed)
    {
        int hit = _pendingPiece;
        _pendingPiece = -1;

        if (hit < 0 || _phase != Phase.Digging) return;
        if (hit >= _pieces.Count)               return;   // list changed under us; drop it quietly

        if (!completed)
        {
            Plugin.ChatGui.Print("[Challenges] You stopped digging too soon — the piece stays buried.");
            return;
        }

        _found.Add(_pieces[hit]);
        _pieces.RemoveAt(hit);

        if (_pieces.Count > 0)
        {
            try { Plugin.Sound.Play(SoundService.Cue.ObjectiveProgress); }
            catch (Exception ex) { Diag.Error($"[Site] piece cue failed: {ex.Message}"); }

            Plugin.ChatGui.Print($"[Challenges] A piece! {_found.Count} of {PieceTotal}.");
            return;
        }

        _endedAtMs = Environment.TickCount64;
        _phase     = Phase.Won;

        try { Plugin.Sound.Play(SoundService.Cue.ChallengeComplete); }
        catch (Exception ex) { Diag.Error($"[Site] relic cue failed: {ex.Message}"); }

        string time = CompletionStore.FormatRaceTime(ElapsedSeconds);
        Diag.Info($"[Site] relic assembled in {time} over {_digs} dig(s).");

        Plugin.ChatGui.Print($"[Challenges] You got the relic! {time}, {_digs} dig(s).");
    }

    /// <summary>Index into <c>_pieces</c> the running dig would turn up, or -1.</summary>
    private int _pendingPiece = -1;

    public string Stop()
    {
        if (_phase == Phase.Off) return "no survey is running.";

        _phase = Phase.Off;
        _site  = null;
        _grid  = new Vector3[0, 0];
        _pieces.Clear();
        _found.Clear();
        return "survey abandoned, site cleared.";
    }

    // ── per-frame ────────────────────────────────────────────────────────────

    public void Tick()
    {
        if (_phase == Phase.Off) return;

        try
        {
            if (_phase == Phase.Won)
            {
                if (Environment.TickCount64 - _endedAtMs >= WonHoldMs) Stop();
                return;
            }

            if (Plugin.ObjectTable.LocalPlayer == null || !Plugin.ClientState.IsLoggedIn) { Stop(); return; }

            if (Plugin.ClientState.TerritoryType != _territory)
            {
                Stop();
                Plugin.ChatGui.Print("[Challenges] Survey abandoned — you left the zone.");
                return;
            }

            if (_phase == Phase.Placing) BuryPieces();
        }
        catch (Exception ex)
        {
            Diag.Error($"[Site] tick failed: {ex.Message}");
            _phase = Phase.Off;
        }
    }

    /// <summary>
    /// Buries every piece in one frame. Unlike the hunt this is not spread across ticks: the site
    /// is already drawn and the player is looking at it, so a visible pause before the test becomes
    /// playable would be worse than one frame of raycasts.
    /// </summary>
    private void BuryPieces()
    {
        if (_site == null) { _phase = Phase.Off; return; }

        // The ground is measured BEFORE anything is buried, because placement needs the shape of
        // the surface around a candidate, not just that a point exists there.
        if (_grid.GetLength(0) < 2) SampleGrid();

        int want = Math.Max(1, DigTuning.SitePieces);

        // ONE piece per frame, with a per-frame budget on the expensive test.
        //
        // The step test raycasts — it has to, because the lattice is far too coarse to tell a wall
        // from a ramp — so burying five pieces in a single frame could mean tens of thousands of
        // rays in one update. Spread over frames it is imperceptible, and placement already has a
        // "Surveying…" state on screen for exactly this.
        int rayTested = 0;
        bool placed = false;

        for (int attempt = 0; attempt < AttemptsPerPiece && !placed; attempt++)
        {
            if (!DigGround.TryPointInBox(_site, _rng, out var p, 1)) continue;
            if (TooClose(p)) continue;
            if (TooSteep(p)) continue;                       // lattice, free

            if (rayTested >= RayTestsPerFrame) break;        // resume next frame
            rayTested++;

            if (HasStep(p)) continue;                        // raycasts, the wall detector

            _pieces.Add(p);
            placed = true;
        }

        if (placed && _pieces.Count < want && _placeTicks < MaxPlaceFrames)
        {
            _placeTicks++;
            return;                                          // come back for the next piece
        }

        // Not placed this frame: keep trying until the frame budget runs out. Stopping at what
        // actually fitted is deliberate — a small site with wide spacing genuinely cannot hold the
        // requested count, and burying four instead of five beats hanging.
        if (!placed && _pieces.Count < want && ++_placeTicks < MaxPlaceFrames)
            return;

        if (_pieces.Count == 0)
        {
            _phase = Phase.Off;
            _site  = null;
            Plugin.ChatGui.PrintError(
                "[Challenges] Could not bury anything in this site — too much broken ground. "
              + "Try somewhere flatter, or raise the step/drop limits in the lab.");
            return;
        }

        _phase       = Phase.Digging;
        _startedAtMs = Environment.TickCount64;

        Diag.Info($"[Site] buried {_pieces.Count} piece(s) of {want} requested.");

        string shortfall = _pieces.Count < want
            ? $" (only {_pieces.Count} of {want} would fit — site too small, spacing too wide, "
            + "or too much of the ground is walls and ledges)"
            : string.Empty;

        Plugin.ChatGui.Print(
            $"[Challenges] {_pieces.Count} piece(s) buried inside the site. Dig to find them.{shortfall}");
    }

    /// <summary>
    /// Re-measures the ground for a site that is already running. Only for the lab's grid slider:
    /// changing the cell count has to be visible on the site in front of you, not on the next one.
    /// Harmless when nothing is running.
    /// </summary>
    public void RebuildGrid()
    {
        if (_site == null || _phase == Phase.Off) return;
        SampleGrid();
    }

    /// <summary>
    /// Measures the ground across the site. Once per site — the lattice is what both the floor grid
    /// and the walls are drawn from, so this is the only place terrain is sampled and the two can
    /// never disagree about where the ground is.
    /// </summary>
    private void SampleGrid()
    {
        if (_site == null) { _grid = new Vector3[0, 0]; return; }

        float side  = MathF.Max(1f, _site.SizeX * _site.Scale);
        int   cells = Math.Clamp(DigTuning.SiteGridCells, 2, MaxGridCells);

        // Subdivide each visual cell until the lattice is about one sample every TargetSpacing.
        //
        // This DECOUPLES how accurately the ground is measured from how many stripes are drawn.
        // Tying them meant a 2-cell grid sampled the ground five times across the whole site, and
        // every marker that reads the lattice — the piece rings, the walls — quietly became
        // inaccurate as a side effect of a purely visual setting. Capped so a dense grid cannot
        // square the raycast count in the frame the site is marked out.
        int wanted = (int)MathF.Ceiling(side / TargetSpacing);
        _gridStride = Math.Clamp((int)MathF.Round(wanted / (float)cells), 1, MaxSubdivisions);

        while (cells * _gridStride + 1 > MaxLatticePoints && _gridStride > 1) _gridStride--;

        int n    = cells * _gridStride + 1;
        var grid = new Vector3[n, n];

        float hx  = side * 0.5f;
        float hz  = MathF.Max(1f, _site.SizeZ * _site.Scale) * 0.5f;
        float cos = MathF.Cos(_site.RotationY);
        float sin = MathF.Sin(_site.RotationY);
        var   c   = _site.Center;

        for (int i = 0; i < n; i++)
        {
            float lx = -hx + 2f * hx * i / (n - 1);

            for (int j = 0; j < n; j++)
            {
                float lz = -hz + 2f * hz * j / (n - 1);

                float wx = c.X + lx * cos - lz * sin;
                float wz = c.Z + lx * sin + lz * cos;

                // Lifted clear of the surface so the line does not disappear into a dip in the mesh.
                var g = DigGround.GroundAt(c, wx, wz);
                grid[i, j] = new Vector3(g.X, g.Y + 0.06f, g.Z);
            }
        }

        _grid = grid;
        Diag.Info($"[Site] ground grid sampled: {cells} cell(s) across, {n * n} point(s).");
    }

    /// <summary>
    /// Drops a world point onto the site's measured floor, by interpolating the ground lattice
    /// rather than firing a ray.
    ///
    /// <para><b>Why interpolate instead of raycasting.</b> A marker ring is ~56 vertices and its
    /// disc over 200; five pieces drawn every frame would be thousands of rays a second, which is
    /// the cost the lattice exists to avoid. The lattice is already measured, so this is free.</para>
    ///
    /// <para><b>And it is arguably more correct than a fresh ray.</b> The circle is drawn on top of
    /// the floor grid, which is built from these same samples — reading them means the marker sits
    /// exactly on the surface the player can see, instead of on a slightly different surface that
    /// happens to be the true one. Two overlays disagreeing about where the ground is looks like a
    /// bug even when both are right.</para>
    ///
    /// <para>Outside the site, or before the lattice exists, the point is returned unchanged.</para>
    /// </summary>
    private Vector3 SnapToFloor(Vector3 p)
    {
        int n = _grid.GetLength(0);
        if (_site == null || n < 2) return p;

        float hx = MathF.Max(0.01f, _site.SizeX * _site.Scale) * 0.5f;
        float hz = MathF.Max(0.01f, _site.SizeZ * _site.Scale) * 0.5f;

        // Into the box's own frame — the inverse yaw, matching ChallengeArea.Contains.
        float dx  = p.X - _site.X;
        float dz  = p.Z - _site.Z;
        float cos = MathF.Cos(-_site.RotationY);
        float sin = MathF.Sin(-_site.RotationY);
        float lx  = dx * cos - dz * sin;
        float lz  = dx * sin + dz * cos;

        // Lattice index space. Clamped rather than rejected: a dig radius straddling the boundary
        // should flatten against the edge, not tear a hole in the ring.
        float fi = Math.Clamp((lx + hx) / (2f * hx), 0f, 1f) * (n - 1);
        float fj = Math.Clamp((lz + hz) / (2f * hz), 0f, 1f) * (n - 1);

        int i0 = Math.Clamp((int)fi, 0, n - 2);
        int j0 = Math.Clamp((int)fj, 0, n - 2);
        float ti = fi - i0;
        float tj = fj - j0;

        float y00 = _grid[i0,     j0    ].Y;
        float y10 = _grid[i0 + 1, j0    ].Y;
        float y01 = _grid[i0,     j0 + 1].Y;
        float y11 = _grid[i0 + 1, j0 + 1].Y;

        float y = (y00 * (1f - ti) + y10 * ti) * (1f - tj)
                + (y01 * (1f - ti) + y11 * ti) * tj;

        return new Vector3(p.X, y, p.Z);
    }

    private bool TooClose(Vector3 p)
    {
        float min = DigTuning.SitePieceSpacing;
        foreach (var existing in _pieces)
            if (DigGround.Flat(p, existing) < min) return true;
        return false;
    }

    /// <summary>
    /// Whether the ground across a candidate's dig radius rises or falls more than
    /// <see cref="DigTuning.SitePieceMaxDrop"/> — i.e. whether the spot straddles a wall, a ledge or
    /// a cliff.
    ///
    /// <para><b>This is a playability rule before it is a cosmetic one.</b> A piece against a wall
    /// has part of its radius somewhere the player physically cannot stand, so the area they can
    /// actually dig from is smaller than the rule says and smaller than the marker shows. The
    /// vertical smear up the wall face was the symptom; the unreachable dig area was the bug.</para>
    ///
    /// <para>Sampled from the lattice, so it costs nothing — which is what lets it run on every
    /// candidate rather than only on the ones that already passed.</para>
    /// </summary>
    private bool TooSteep(Vector3 p)
    {
        float max = MathF.Max(0.05f, DigTuning.SitePieceMaxDrop);
        float r   = MathF.Max(0.1f, DigTuning.SitePieceRadius);
        float baseY = SnapToFloor(p).Y;

        // Eight compass points at the rim and at half radius. The inner set matters: a narrow ledge
        // crossing the middle of the circle leaves the rim perfectly level.
        for (int i = 0; i < 8; i++)
        {
            float a = MathF.Tau * i / 8f;
            float cos = MathF.Cos(a), sin = MathF.Sin(a);

            for (int k = 1; k <= 2; k++)
            {
                float rr = r * k * 0.5f;
                var   q  = new Vector3(p.X + rr * cos, p.Y, p.Z + rr * sin);

                if (MathF.Abs(SnapToFloor(q).Y - baseY) > max) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the ground under a candidate contains a <b>step</b> — a wall, a kerb, the lip of a
    /// trough — as opposed to a slope.
    ///
    /// <para><b>Total height cannot tell those apart, which is why this exists.</b> A 2-yalm rise
    /// across a 4-yalm radius is a 26° hillside and fine to dig on; a 1-yalm garden wall is a
    /// *smaller* total rise and completely unusable. The difference is not how far the ground moves
    /// but how suddenly, so this walks outward in short increments and looks at the change between
    /// neighbours.</para>
    ///
    /// <para><b>It has to raycast, and the lattice cannot substitute.</b> Lattice samples are about
    /// two yalms apart and are read back with bilinear interpolation, which turns a one-yalm wall
    /// into a gentle ramp — the smoothing that makes the grid look good is exactly what destroys
    /// the evidence here. Hence <see cref="RayTestsPerFrame"/> and one piece per frame.</para>
    /// </summary>
    private bool HasStep(Vector3 centre)
    {
        float r       = MathF.Max(0.5f, DigTuning.SitePieceRadius);
        float maxStep = MathF.Max(0.02f, DigTuning.SitePieceMaxStep);

        int steps = Math.Clamp((int)MathF.Ceiling(r / StepSpacing), 2, MaxStepSamples);
        float centreY = DigGround.GroundAt(centre, centre.X, centre.Z).Y;

        for (int d = 0; d < StepRays; d++)
        {
            float a   = MathF.Tau * d / StepRays;
            float cos = MathF.Cos(a), sin = MathF.Sin(a);

            float prev = centreY;

            for (int k = 1; k <= steps; k++)
            {
                float rr = r * k / steps;
                float y  = DigGround.GroundAt(centre, centre.X + rr * cos, centre.Z + rr * sin).Y;

                if (MathF.Abs(y - prev) > maxStep) return true;
                prev = y;
            }
        }

        return false;
    }

    /// <summary>
    /// A floor sampler for one marker that <b>vetoes</b> points too far above or below the marker's
    /// own ground — see <see cref="DigVolumeRender.DrawGroundRing"/>. Placement already refuses
    /// steep spots, so this is the second line: it keeps an existing marker, or one whose ground
    /// changed under it, from drawing a vertical sheet up a wall.
    /// </summary>
    private Func<Vector3, Vector3?> FloorVeto(Vector3 centre)
    {
        float baseY = SnapToFloor(centre).Y;
        float max   = MathF.Max(0.05f, DigTuning.SitePieceMaxDrop);

        return p =>
        {
            var g = SnapToFloor(p);
            return MathF.Abs(g.Y - baseY) > max ? null : g;
        };
    }

    // ── in-world ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The site itself as gradient walls, plus a disc on each piece already recovered. Unfound
    /// pieces are never drawn — that would be the entire test given away.
    /// </summary>
    public void DrawWorld()
    {
        if (_site == null || _phase is Phase.Off) return;

        // The picked colour, always — including the won state. Swapping to green on success would
        // make the colour picker lie about what is being tested, and the headline already says the
        // relic is assembled.
        var rgb = DigTuning.SiteColor;

        // Ground first, walls over it — the walls are the boundary and should read as being in
        // front of the floor they enclose.
        if (DigTuning.SiteShowGrid)
            DigVolumeRender.DrawGroundGrid(_grid, _gridStride, rgb);

        DigVolumeRender.DrawGradientWalls(_grid, DigTuning.SiteWallHeight, rgb);

        // Recovered pieces: green, always shown. Nothing is given away by marking ground you have
        // already dug.
        // Each marker gets its own sampler, because the veto is relative to that marker's ground.
        var recovered = new Vector3(0.44f, 0.86f, 0.62f);
        foreach (var p in _found)
        {
            var snap = FloorVeto(p);
            DigVolumeRender.DrawGroundDisc(p, DigTuning.SitePieceRadius, recovered, 0.34f, snap);
            DigVolumeRender.DrawGroundRing(p, DigTuning.SitePieceRadius, recovered, 0.75f, 2f, snap);
        }

        // Still-buried pieces: the answer key, off by default of the player's choosing rather than
        // of the code's. Drawn at the EXACT dig radius, which is the only way to tell a near miss
        // apart from a mis-tuned radius — from inside the game the two look identical.
        if (!DigTuning.SiteRevealPieces) return;

        foreach (var p in _pieces)
        {
            var snap = FloorVeto(p);
            DigVolumeRender.DrawGroundDisc(p, DigTuning.SitePieceRadius, DigTuning.SiteDebugColor, 0.22f, snap);
            DigVolumeRender.DrawGroundRing(p, DigTuning.SitePieceRadius, DigTuning.SiteDebugColor, 0.95f, 2f, snap);
        }
    }
}
#endif
