#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using CameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace TieriChallengesFFXIV;

/// <summary>Which wall a building's door is cut into. Named sides rather than an angle, because a
/// city block is axis-aligned by construction and an angle would invite rotating one.</summary>
internal enum CitySide { NegX, PosX, NegZ, PosZ }

/// <summary>
/// One building. <b>Its footprint is stored in TILE INDICES, never in world coordinates</b> — that
/// is the whole mechanism behind "buildings line up with the grid". The renderer reads the corner's
/// world XZ back out of the same lattice the floor grid is drawn from, so the two cannot disagree
/// about where a tile edge is; there is no second copy of the arithmetic to drift.
/// </summary>
internal sealed class CityBuilding
{
    /// <summary>Lower corner, in lattice index space.</summary>
    public int TileX;
    public int TileZ;

    /// <summary>Footprint in whole tiles. Always at least 1.</summary>
    public int TilesX;
    public int TilesZ;

    /// <summary>
    /// Ground the building stands on — the <b>highest</b> sample under its footprint, so it never
    /// floats. <see cref="SkirtY"/> is the lowest, and the walls are drawn down to there so a
    /// building on a slope has no gap under its downhill corner.
    /// </summary>
    public float BaseY;
    public float SkirtY;

    /// <summary>Height above <see cref="BaseY"/>. Quantised to a whole number of floors.</summary>
    public float Height;

    public int Floors;

    public Vector3 Rgb;

    /// <summary>Which wall the door is in — always the one facing the middle of the city.</summary>
    public CitySide DoorSide;

    /// <summary>
    /// Seeds the window-lighting pattern. Fixed at placement rather than drawn per frame: lights
    /// re-rolled every frame would strobe, which reads as a rendering fault rather than as a city.
    /// </summary>
    public int Seed;
}

/// <summary>Everything the renderer needs to know about how the city should LOOK, in one lump, so
/// the drawing code takes no dependency on the service that owns the settings.</summary>
internal struct CityLook
{
    public float   WallAlpha;
    public float   FloorHeight;
    public int     LitPercent;
    public bool    ShowWindows;
    public bool    ShowRoofs;
    public bool    ShowEdges;
    public Vector3 LitRgb;
    public Vector3 DarkRgb;
}

/// <summary>
/// DEVELOPER BUILD ONLY — <b>Test City</b>. Lays a 1:1 tile grid on the ground under the player and
/// stands coloured buildings on it, each with windows and a door at its base.
///
/// <para><b>What it is for.</b> Sansflaire asked for it to try something. It is the dig lab's Test 2 walls
/// taken to their conclusion: the same measured-once ground lattice, the same background draw list,
/// but closed boxes with detail on their faces instead of a fence. Nothing about it is player-facing
/// and nothing about it ships — see the gate on this file.</para>
///
/// <para><b>The lattice is measured ONCE, when the city is built.</b> Draping a grid over terrain is
/// a downward raycast per vertex, and a 32-tile grid is 1,089 of them; per frame that would be tens
/// of thousands a second. A city does not move once it is placed, so this is the only place terrain
/// is sampled and both the floor and every building base read back out of the same array. Same rule
/// and same reason as <see cref="DigSiteService"/>.</para>
///
/// <para><b>Escape does not clear the city, deliberately.</b> The house rule is that Escape releases
/// what the plugin started and <i>never destroys</i> — and a placed city is state somebody spent a
/// build on, exactly like a running dig test, which Escape also leaves alone. The window is closed
/// by its own control or by <c>/tchal city</c>.</para>
/// </summary>
internal sealed class TestCityService
{
    // ── limits ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ceiling on tiles across. 48 tiles is a 49×49 lattice — 2,401 raycasts in the frame the city
    /// is built, which is the same order as the dig site's own cap and is paid once.
    /// </summary>
    private const int MaxGridTiles = 48;
    private const int MinGridTiles = 4;

    private const float MinTileSize = 0.5f;
    private const float MaxTileSize = 8f;

    /// <summary>Backstop on the building count. A tight pitch on a wide grid could otherwise ask for
    /// hundreds, and every one of them costs panels every frame.</summary>
    private const int MaxBuildings = 64;

    /// <summary>Lifts the drawn grid clear of the surface so it does not vanish into a dip.</summary>
    private const float GridLift = 0.06f;

    // ── settings, live-editable from the lab window ──────────────────────────
    //
    // DELIBERATELY NOT PERSISTED, and not in Configuration either. DigTuning exists because dig
    // ranges are a feel worth keeping across a rebuild; this is a first cut of an experiment, and a
    // persisted shape is a migration path forever (§3). If it earns one it gets its own file, the
    // way dig-tuning.json did — not a corner of the player's config.

    /// <summary>Side of one tile, in yalms. 1.0 is the literal 1:1 tile that was asked for.</summary>
    public float TileSize = 1f;

    /// <summary>Tiles across the whole grid, centred on the player.</summary>
    public int GridTiles = 32;

    /// <summary>Footprint of one building, in tiles. Square.</summary>
    public int BuildingTiles = 5;

    /// <summary>Clear tiles between one building and the next — the streets.</summary>
    public int StreetTiles = 3;

    /// <summary>Tiles kept clear around the player, so the city surrounds rather than swallows.</summary>
    public int ClearTiles = 4;

    public float MinHeight = 9f;
    public float MaxHeight = 28f;

    /// <summary>Storey height. Windows are laid out one row per floor, so this sets their spacing.</summary>
    public float FloorHeight = 3.2f;

    public float WallAlpha   = 0.88f;
    public int   LitPercent  = 55;
    public bool  ShowGrid    = true;
    public bool  ShowWindows = true;
    public bool  ShowRoofs   = true;
    public bool  ShowEdges   = true;

    public Vector3 GridRgb = new(0.62f, 0.70f, 0.86f);
    public Vector3 LitRgb  = new(1.00f, 0.88f, 0.52f);
    public Vector3 DarkRgb = new(0.10f, 0.12f, 0.18f);

    // ── state ────────────────────────────────────────────────────────────────

    private readonly Random            _rng       = new();
    private readonly List<CityBuilding> _buildings = new();

    private Vector3[,] _grid = new Vector3[0, 0];
    private int        _tiles;
    private float      _builtTileSize;
    private uint       _territory;

    /// <summary>Player's tile at build time, for the readout and the clear zone.</summary>
    private int _playerI;
    private int _playerJ;

    /// <summary>
    /// Panels (walls and roofs) actually submitted on the last frame.
    ///
    /// <para><b>This counter is the point, not decoration.</b> Face culling that comes out backwards
    /// draws precisely the hidden faces, and a city whose every face was culled is indistinguishable
    /// from a city that was never built. Buildings &gt; 0 with panels at 0 says the cull is inverted;
    /// both at 0 says placement found nowhere to build.</para>
    /// </summary>
    public int PanelsDrawn { get; private set; }

    /// <summary>Whether the eye used for culling and ordering came from the game camera, or from the
    /// player-head fallback. Surfaced because a wrong eye looks like broken geometry.</summary>
    public bool EyeFromCamera { get; private set; }

    public bool IsBuilt => _grid.GetLength(0) >= 2;

    public int BuildingCount => _buildings.Count;

    public int TilesAcross => _tiles;

    public Vector3 Origin => IsBuilt ? _grid[0, 0] : Vector3.Zero;

    // ── actions ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Measures the ground under the player, then stands buildings on it. One frame's work: unlike a
    /// dig site nothing is hidden here, so there is nothing to spread over ticks to keep secret and
    /// no reason for the player to wait.
    /// </summary>
    public string Build()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        try
        {
            Clear();

            _territory = Plugin.ClientState.TerritoryType;

            SampleGrid(player.Position);
            PlaceBuildings();

            return $"city built — {_buildings.Count} building(s) on a {_tiles}×{_tiles} grid "
                 + $"of {_builtTileSize:0.##}y tiles.";
        }
        catch (Exception ex)
        {
            Diag.Error($"[City] build failed: {ex.Message}");
            Clear();
            return "build failed — see the log.";
        }
    }

    public string Clear()
    {
        bool had = IsBuilt;

        _grid = new Vector3[0, 0];
        _tiles = 0;
        _buildings.Clear();
        PanelsDrawn = 0;

        return had ? "city cleared." : "no city to clear.";
    }

    /// <summary>
    /// Re-measures and re-places without re-rolling the look. Used by the window's sliders: changing
    /// the tile size or the grid extent has to be visible on the city in front of you, not on the
    /// next one.
    /// </summary>
    public void Rebuild()
    {
        if (!IsBuilt) return;
        Build();
    }

    /// <summary>
    /// Drops the city when the measured ground stops being the ground we are standing on. The
    /// lattice is a set of heights in one territory; carried into another it would draw a city
    /// hanging in the air at the old zone's altitudes.
    /// </summary>
    public void Tick()
    {
        if (!IsBuilt) return;

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
                Plugin.ChatGui.Print("[Challenges] Test City cleared — you left the zone.");
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[City] tick failed: {ex.Message}");
            Clear();
        }
    }

    // ── placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// Measures ground height at every tile corner.
    ///
    /// <para><b>The lattice is snapped to a world-aligned multiple of the tile size</b>, not centred
    /// exactly on the player. Two builds from slightly different standing positions then land on the
    /// same tiles, which is what makes "that building is on that tile" a statement you can check
    /// twice rather than a coincidence of where you happened to stop walking.</para>
    /// </summary>
    private void SampleGrid(Vector3 centre)
    {
        int   tiles = Math.Clamp(GridTiles, MinGridTiles, MaxGridTiles);
        float t     = Math.Clamp(TileSize, MinTileSize, MaxTileSize);

        float ox = MathF.Floor(centre.X / t) * t - (tiles / 2) * t;
        float oz = MathF.Floor(centre.Z / t) * t - (tiles / 2) * t;

        int n    = tiles + 1;
        var grid = new Vector3[n, n];

        for (int i = 0; i < n; i++)
        {
            float wx = ox + i * t;

            for (int j = 0; j < n; j++)
            {
                float wz = oz + j * t;

                // GroundAt never fails — a ray that finds nothing keeps the reference height. That
                // is exactly right for drawing (a hole in the grid is worse than a vertex a little
                // off) and would be wrong for placing anything the player has to reach.
                var g = DigGround.GroundAt(centre, wx, wz);
                grid[i, j] = new Vector3(wx, g.Y + GridLift, wz);
            }
        }

        _grid          = grid;
        _tiles         = tiles;
        _builtTileSize = t;

        _playerI = Math.Clamp((int)MathF.Floor((centre.X - ox) / t), 0, tiles - 1);
        _playerJ = Math.Clamp((int)MathF.Floor((centre.Z - oz) / t), 0, tiles - 1);

        Diag.Info($"[City] ground sampled: {tiles}×{tiles} tiles of {t:0.##}y, {n * n} point(s).");
    }

    /// <summary>
    /// Fills the grid with blocks on a fixed pitch, skipping the ones that would land on top of the
    /// player. A regular pitch rather than scattered placement on purpose: the thing being looked at
    /// is whether buildings sit on tile boundaries, and a grid of them makes a misalignment of half a
    /// tile obvious across the whole scene instead of arguable on one box.
    /// </summary>
    private void PlaceBuildings()
    {
        int w      = Math.Clamp(BuildingTiles, 1, 12);
        int street = Math.Clamp(StreetTiles, 1, 8);
        int clear  = Math.Clamp(ClearTiles, 0, 20);
        int pitch  = w + street;

        int  idx = 0;
        bool full = false;

        for (int i = 0; i + w <= _tiles && !full; i += pitch)
        {
            for (int j = 0; j + w <= _tiles; j += pitch)
            {
                // The player's own ground, plus a margin, stays empty — "surrounding my character"
                // rather than "on top of my character".
                bool overPlayer = _playerI >= i - clear && _playerI < i + w + clear
                               && _playerJ >= j - clear && _playerJ < j + w + clear;
                if (overPlayer) continue;

                if (_buildings.Count >= MaxBuildings) { full = true; break; }

                _buildings.Add(MakeBuilding(i, j, w, idx++));
            }
        }

        if (full)
            Diag.Info($"[City] stopped at the {MaxBuildings}-building cap — widen the streets or "
                    + "shrink the grid for a full block layout.");
    }

    private CityBuilding MakeBuilding(int i, int j, int w, int idx)
    {
        // Height quantised to whole floors, so the window rows divide the wall exactly and the top
        // row is not a sliver. The slider range is honoured to within one storey.
        float wanted = MinHeight + (float)_rng.NextDouble() * MathF.Max(0f, MaxHeight - MinHeight);
        int   floors = Math.Clamp((int)MathF.Round(wanted / MathF.Max(1f, FloorHeight)), 1, 40);
        float height = floors * MathF.Max(1f, FloorHeight);

        Footprint(i, j, w, out float lowest, out float highest);

        // Door faces the middle of the city: a building east of centre opens westward. Streets are
        // the gaps between blocks, so this is also the side a street is on.
        float dx = (i + w * 0.5f) - _tiles * 0.5f;
        float dz = (j + w * 0.5f) - _tiles * 0.5f;

        CitySide door = MathF.Abs(dx) >= MathF.Abs(dz)
            ? (dx > 0f ? CitySide.NegX : CitySide.PosX)
            : (dz > 0f ? CitySide.NegZ : CitySide.PosZ);

        return new CityBuilding
        {
            TileX    = i,
            TileZ    = j,
            TilesX   = w,
            TilesZ   = w,
            BaseY    = highest,
            SkirtY   = lowest,
            Height   = height,
            Floors   = floors,
            Rgb      = Palette[idx % Palette.Length],
            DoorSide = door,
            Seed     = _rng.Next(),
        };
    }

    /// <summary>Lowest and highest measured ground under a footprint, corners included.</summary>
    private void Footprint(int i, int j, int w, out float lowest, out float highest)
    {
        lowest  = float.MaxValue;
        highest = float.MinValue;

        int n = _grid.GetLength(0);

        for (int a = i; a <= i + w && a < n; a++)
        {
            for (int b = j; b <= j + w && b < n; b++)
            {
                float y = _grid[a, b].Y;
                if (y < lowest)  lowest  = y;
                if (y > highest) highest = y;
            }
        }

        if (lowest > highest) { lowest = 0f; highest = 0f; }
    }

    /// <summary>
    /// Distinct hues rather than shades of one. Cycled in order instead of drawn at random, because
    /// random picks put two of the same colour next to each other often enough to look like a bug.
    /// </summary>
    private static readonly Vector3[] Palette =
    {
        new(0.86f, 0.38f, 0.36f),   // brick
        new(0.38f, 0.58f, 0.88f),   // slate blue
        new(0.46f, 0.80f, 0.55f),   // green
        new(0.92f, 0.74f, 0.34f),   // ochre
        new(0.74f, 0.50f, 0.86f),   // violet
        new(0.36f, 0.80f, 0.82f),   // teal
        new(0.90f, 0.56f, 0.30f),   // orange
        new(0.80f, 0.81f, 0.88f),   // pale stone
    };

    // ── in-world ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The floor grid, then the buildings over it.
    ///
    /// <para>The grid goes through <see cref="DigVolumeRender.DrawGroundGrid"/> at stride 1 rather
    /// than getting its own drawer: at that stride it is a checker and a line on every single tile,
    /// which is exactly the 1:1 grid wanted, and reusing it means there is no second opinion about
    /// where a tile edge lies.</para>
    /// </summary>
    public void DrawWorld()
    {
        if (!IsBuilt) return;

        try
        {
            if (ShowGrid) DigVolumeRender.DrawGroundGrid(_grid, 1, GridRgb);

            EyeFromCamera = TryEye(out var eye);
            if (!EyeFromCamera)
            {
                // No camera to read. Ordering and culling both need an eye, and a fallback is
                // honest only if it is visible — hence EyeFromCamera in the lab readout. Roughly
                // head height above the player is close enough to keep the scene coherent.
                var lp = Plugin.ObjectTable.LocalPlayer;
                if (lp == null) { PanelsDrawn = 0; return; }
                eye = lp.Position + new Vector3(0f, 8f, 0f);
            }

            var look = new CityLook
            {
                WallAlpha   = Math.Clamp(WallAlpha, 0.05f, 1f),
                FloorHeight = MathF.Max(1f, FloorHeight),
                LitPercent  = Math.Clamp(LitPercent, 0, 100),
                ShowWindows = ShowWindows,
                ShowRoofs   = ShowRoofs,
                ShowEdges   = ShowEdges,
                LitRgb      = LitRgb,
                DarkRgb     = DarkRgb,
            };

            PanelsDrawn = TestCityRender.Draw(_grid, _buildings, eye, look);
        }
        catch (Exception ex)
        {
            Diag.Error($"[City] world draw failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The game camera's world position.
    ///
    /// <para><b>Field reads only, each hop null-checked — no native call.</b>
    /// <c>CameraManager.Instance()</c> then the <c>Camera</c> pointer then
    /// <c>SceneCamera.Position</c>; the path is the one ClaudeAccessXIV serves <c>/scene/camera</c>
    /// from, and <c>Graphics.Scene.Camera</c> inherits <c>Position</c> from
    /// <c>Graphics.Scene.Object</c> at +0x50. A precondition per hop rather than a try/catch around
    /// the lot: a C# catch cannot survive a bad native dereference (BROKEN.md 012), so the null
    /// checks ARE the safety and the catch is only for the managed arithmetic after it.</para>
    /// </summary>
    private static unsafe bool TryEye(out Vector3 eye)
    {
        eye = default;

        var manager = CameraManager.Instance();
        if (manager == null) return false;

        var camera = manager->Camera;
        if (camera == null) return false;

        var p = camera->SceneCamera.Position;
        eye = new Vector3(p.X, p.Y, p.Z);

        return true;
    }
}
#endif
