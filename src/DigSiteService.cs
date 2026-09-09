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

    /// <summary>
    /// Fine steps per coarse (dig-sized) grid cell. 2 keeps the terrain-following honest without
    /// squaring the number of raycasts — the lattice is sampled once, but "once" still happens in a
    /// single frame and a few thousand rays would be felt.
    /// </summary>
    private const int GridSubdivisions = 2;

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
    private int        _gridStride = GridSubdivisions;
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
        _digs      = 0;
        _territory = Plugin.ClientState.TerritoryType;
        _phase     = Phase.Placing;

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

        _found.Add(_pieces[hit]);
        _pieces.RemoveAt(hit);

        if (_pieces.Count > 0)
        {
            try { Plugin.Sound.Play(SoundService.Cue.ObjectiveProgress); }
            catch (Exception ex) { Diag.Error($"[Site] piece cue failed: {ex.Message}"); }

            return animation + $" A piece! {_found.Count} of {PieceTotal}.";
        }

        _endedAtMs = Environment.TickCount64;
        _phase     = Phase.Won;

        try { Plugin.Sound.Play(SoundService.Cue.ChallengeComplete); }
        catch (Exception ex) { Diag.Error($"[Site] relic cue failed: {ex.Message}"); }

        string time = CompletionStore.FormatRaceTime(ElapsedSeconds);
        Diag.Info($"[Site] relic assembled in {time} over {_digs} dig(s).");

        return $"You got the relic! {time}, {_digs} dig(s).";
    }

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

        int want = Math.Max(1, DigTuning.SitePieces);

        for (int n = 0; n < want; n++)
        {
            bool placed = false;

            for (int attempt = 0; attempt < AttemptsPerPiece && !placed; attempt++)
            {
                if (!DigGround.TryPointInBox(_site, _rng, out var p, 1)) continue;
                if (TooClose(p)) continue;

                _pieces.Add(p);
                placed = true;
            }

            // Stop at what actually fitted rather than looping forever. A small site with a large
            // spacing simply cannot hold the requested count, and quietly burying four instead of
            // five is far better than hanging the frame — the count shown is the count buried.
            if (!placed) break;
        }

        if (_pieces.Count == 0)
        {
            _phase = Phase.Off;
            _site  = null;
            Plugin.ChatGui.PrintError(
                "[Challenges] Could not bury anything in this site — try somewhere more open.");
            return;
        }

        SampleGrid();

        _phase       = Phase.Digging;
        _startedAtMs = Environment.TickCount64;

        Diag.Info($"[Site] buried {_pieces.Count} piece(s) of {want} requested.");

        string shortfall = _pieces.Count < want
            ? $" (only {_pieces.Count} of {want} would fit — the site is too small or spacing too wide)"
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

        // Fine subdivision only pays for terrain shape WITHIN a cell, so a dense grid does not
        // need it — and without this the lattice would grow as the square of the cell count and a
        // 40-cell site would fire several thousand raycasts in the frame the site is marked out.
        _gridStride = cells <= 16 ? GridSubdivisions : 1;

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

    private bool TooClose(Vector3 p)
    {
        float min = DigTuning.SitePieceSpacing;
        foreach (var existing in _pieces)
            if (DigGround.Flat(p, existing) < min) return true;
        return false;
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
        var recovered = new Vector3(0.44f, 0.86f, 0.62f);
        foreach (var p in _found)
        {
            DigVolumeRender.DrawGroundDisc(p, DigTuning.SitePieceRadius, recovered);
            DigVolumeRender.DrawGroundRing(p, DigTuning.SitePieceRadius, recovered, 0.75f);
        }

        // Still-buried pieces: the answer key, off by default of the player's choosing rather than
        // of the code's. Drawn at the EXACT dig radius, which is the only way to tell a near miss
        // apart from a mis-tuned radius — from inside the game the two look identical.
        if (!DigTuning.SiteRevealPieces) return;

        foreach (var p in _pieces)
        {
            DigVolumeRender.DrawGroundDisc(p, DigTuning.SitePieceRadius, DigTuning.SiteDebugColor, 0.22f);
            DigVolumeRender.DrawGroundRing(p, DigTuning.SitePieceRadius, DigTuning.SiteDebugColor);
        }
    }
}
#endif
