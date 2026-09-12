#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using CameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace TieriChallengesFFXIV;

/// <summary>Which wall a building's door is cut into. Named sides rather than an angle, because a
/// city block is axis-aligned by construction and an angle would invite rotating one.</summary>
internal enum CitySide { NegX = 0, PosX = 1, NegZ = 2, PosZ = 3 }

/// <summary>
/// One building. <b>Its footprint is stored in TILE INDICES, never in world coordinates</b> — the
/// renderer turns those into world corners through <see cref="TestCityService.TileCorner"/>, which
/// the grid drawer also calls. That shared function is what makes "buildings line up with the tiles"
/// structural rather than two sums that agree until one is edited.
/// </summary>
internal sealed class CityBuilding
{
    /// <summary>Lower corner, in tile indices.</summary>
    public int TileX;
    public int TileZ;

    /// <summary>Footprint in whole tiles.</summary>
    public int TilesX;
    public int TilesZ;

    /// <summary>
    /// Ground the building stands on — the <b>highest</b> interpolated height under its footprint, so
    /// it never floats. <see cref="SkirtY"/> is the lowest, and walls are drawn down to there so a
    /// building on a slope shows no daylight under its downhill corner.
    /// </summary>
    public float BaseY;
    public float SkirtY;

    /// <summary>Height above <see cref="BaseY"/>, quantised to a whole number of floors.</summary>
    public float Height;

    public int Floors;

    public Vector3 Rgb;

    /// <summary>Which wall the door is in — always the one facing the middle of the city.</summary>
    public CitySide DoorSide;

    /// <summary>Seeds the window-lighting pattern. Fixed at placement: re-rolled per frame it would
    /// strobe, which reads as a rendering fault rather than as a city.</summary>
    public int Seed;

    /// <summary>
    /// Packed per-face occlusion, 9 bits per face, 5 faces — see <see cref="TestCityOcclusion"/>.
    /// Starts fully visible so a fresh city draws whole and then resolves over the next few frames,
    /// rather than flashing empty.
    /// </summary>
    public ulong Occlusion = TestCityOcclusion.AllFacesVisible();

    /// <summary>Distance from the eye, refreshed each frame — the render set is nearest-first.</summary>
    public float EyeDistance;
}

/// <summary>
/// How the city hides behind the world.
///
/// <para><b>All three are kept selectable on purpose.</b> The depth path is strictly better and is
/// the default — but it is also the one that can fail on a machine or after a patch, and when it
/// does the city must still draw. Keeping the raycast path alive is also the only way to compare the
/// two by flipping between them, which is how "the raycasts aren't working great" got diagnosed in
/// the first place.</para>
/// </summary>
internal enum CityOcclusion
{
    /// <summary>Draw everything. Useful for telling an occlusion bug from a geometry bug.</summary>
    None = 0,

    /// <summary>Collision raycasts, 3×3 per face, cached. Only the collision mesh occludes.</summary>
    Raycast = 1,

    /// <summary>Real D3D11 geometry, per-pixel tested against the game's depth buffer.</summary>
    DepthBuffer = 2,
}

/// <summary>Everything the renderer needs to know about how the city should LOOK, in one lump, so the
/// drawing code takes no dependency on the service that owns the settings.</summary>
internal struct CityLook
{
    public float   WallAlpha;
    public int     LitPercent;
    public bool    ShowWindows;
    public bool    ShowRoofs;
    public bool    ShowEdges;
    public bool    Occlude;
    public Vector3 LitRgb;
    public Vector3 DarkRgb;
}

/// <summary>
/// DEVELOPER BUILD ONLY — <b>Test City</b>. Lays a tile grid on the ground under the player and
/// stands coloured buildings on it, each with windows and a door at its base.
///
/// <para><b>Ground measurement is DECOUPLED from tile density, and that is what makes a huge, fine
/// grid possible at all.</b> One raycast per tile corner is fine at 32 tiles and ruinous at 256: a
/// 257×257 lattice is 66,049 rays in the frame the city is built. So the ground is measured on its
/// own spacing — <see cref="MeasureSpacing"/> yalms, auto-widened so the sample count can never
/// exceed <see cref="MaxSamplesPerSide"/> — and every height in the city comes from
/// <see cref="HeightAt"/> interpolating it. Rays now scale with the city's SIZE, not its tile count,
/// and are bounded whatever the sliders say. This is the same decoupling
/// <see cref="DigSiteService.SampleGrid"/> performs in reverse, for the same reason.</para>
///
/// <para><b>The consequence is worth knowing rather than discovering.</b> At 2-yalm sampling a
/// quarter-yalm tile grid is draped smoothly over the terrain instead of following every bump —
/// correct to within the sampling, and visibly approximate on broken ground. Tighten
/// <see cref="MeasureSpacing"/> for a small city; there is no way to have both fine measurement and
/// a vast extent inside one frame's ray budget.</para>
///
/// <para><b>Escape does not clear the city, deliberately.</b> The house rule is that Escape releases
/// what the plugin started and <i>never destroys</i> — a placed city is state somebody spent a build
/// on, exactly like a running dig test, which Escape also leaves alone.</para>
/// </summary>
internal sealed class TestCityService : IDisposable
{
    // ── limits ───────────────────────────────────────────────────────────────

    /// <summary>Tiles across. Raised from 48 at Sansflaire's request; it is no longer what bounds the
    /// raycast count, so the ceiling is now about draw budget rather than build cost.</summary>
    private const int MaxGridTiles = 400;
    private const int MinGridTiles = 4;

    /// <summary>Tile size. The floor came down from 0.5 for the same request.</summary>
    private const float MinTileSize = 0.05f;
    private const float MaxTileSize = 8f;

    /// <summary>
    /// Hard ceiling on the ground sample lattice per side. 65 is 4,225 rays in the build frame —
    /// the same order as the dig site's own cap, and paid once rather than per frame.
    /// </summary>
    private const int MaxSamplesPerSide = 65;

    /// <summary>Backstop on placement. Far above what is drawn — see <see cref="MaxRendered"/>.</summary>
    private const int MaxBuildings = 400;

    /// <summary>
    /// Buildings drawn per frame, nearest first.
    ///
    /// <para><b>This, not the building count, is what keeps a 400-tile city affordable.</b> Each
    /// building is up to five panels and each panel is a projection plus an occlusion lookup, so the
    /// per-frame cost has to be bounded by something that does not grow with the city. Drawing the
    /// nearest N is the standard answer and it is honest: the ones dropped are the far ones.</para>
    /// </summary>
    private const int MaxRendered = 64;

    /// <summary>
    /// Faces re-tested for occlusion per frame, round-robin.
    ///
    /// <para>Nine rays each, so 40 faces is ~360 rays a frame — sustainable, where testing every
    /// visible face every frame would be over 2,000. A full sweep of the drawn set takes a handful of
    /// frames, which is only visible while the camera is moving.</para>
    /// </summary>
    private const int OcclusionFacesPerFrame = 40;

    // ── settings, live-editable from the panel ───────────────────────────────
    //
    // DELIBERATELY NOT PERSISTED, and not in Configuration either. DigTuning earned its own JSON
    // because a dig range is a feel worth keeping across a rebuild; an experiment has not, and a
    // persisted shape is a migration path forever (§3).

    /// <summary>Side of one tile, in yalms.</summary>
    public float TileSize = 1f;

    /// <summary>Tiles across the whole city, centred on the player.</summary>
    public int GridTiles = 32;

    /// <summary>Spacing of the ground samples, in yalms. Auto-widened if the extent demands it.</summary>
    public float MeasureSpacing = 2f;

    /// <summary>Footprint of one building, in tiles. Square.</summary>
    public int BuildingTiles = 5;

    /// <summary>Clear tiles between one building and the next — the streets.</summary>
    public int StreetTiles = 3;

    /// <summary>Tiles kept clear around the player, so the city surrounds rather than swallows.</summary>
    public int ClearTiles = 4;

    public float MinHeight = 9f;
    public float MaxHeight = 28f;

    /// <summary>Storey height. Windows are one row per floor, so this sets their spacing.</summary>
    public float FloorHeight = 3.2f;

    /// <summary>Buildings beyond this are not drawn at all. 0 disables the distance cut (the nearest
    /// <see cref="MaxRendered"/> still applies — that one is not optional).</summary>
    public float DrawDistance = 220f;

    public float WallAlpha   = 0.88f;
    public int   LitPercent  = 55;
    public bool  ShowGrid    = true;
    public bool  ShowWindows = true;
    public bool  ShowRoofs   = true;
    public bool  ShowEdges   = true;

    /// <summary>How the city hides behind the world. Depth buffer by default — it is the whole point
    /// of 0.84.54.9 — falling back to the painted path automatically if D3D cannot start.</summary>
    public CityOcclusion Mode = CityOcclusion.DepthBuffer;

    /// <summary>
    /// Depth-compare bias, in the game's own clip space.
    ///
    /// <para>A knob rather than a constant, for the same reason every dig range is: a building
    /// stands ON the terrain, so its base pixels sit at almost exactly the terrain's depth, and how
    /// much slack that needs before it stops speckling is a thing you find by looking at it. Reverse-Z
    /// is non-linear, so the value that is right up close is not the value that is right at 200
    /// yalms; there is no single correct number to hard-code.</para>
    /// </summary>
    public float DepthBias = 0.00002f;

    // Grid drawing. Extent and fineness are separate knobs from the city's own — see TestCityGrid.
    public int   FineTiles  = 32;
    public int   MajorEvery = 8;
    public bool  ShowChecker = true;

    public Vector3 GridRgb = new(0.62f, 0.70f, 0.86f);
    public Vector3 LitRgb  = new(1.00f, 0.88f, 0.52f);
    public Vector3 DarkRgb = new(0.10f, 0.12f, 0.18f);

    // ── state ────────────────────────────────────────────────────────────────

    private readonly Random             _rng       = new();
    private readonly List<CityBuilding> _buildings = new();
    private readonly List<CityBuilding> _rendered  = new();

    /// <summary>Measured ground, on <see cref="_sampleSpacing"/> — NOT the tile grid.</summary>
    private Vector3[,] _samples = new Vector3[0, 0];
    private float      _sampleSpacing;

    /// <summary>World XZ of tile corner (0, 0), and the built tile size. Held separately from the
    /// slider so editing it mid-frame cannot move a city that is being drawn.</summary>
    private float _originX;
    private float _originZ;
    private float _builtTileSize;
    private int   _tiles;

    private uint _territory;

    private int _playerI;
    private int _playerJ;

    /// <summary>Round-robin cursor for the occlusion budget, over <c>_buildings</c>.</summary>
    private int _occlCursor;

    /// <summary>
    /// The D3D11 renderer, built on first use rather than in the constructor.
    ///
    /// <para>Deliberately lazy: the constructor runs off the main thread during plugin load, where
    /// the game's device singleton may not be up — and standing up D3D resources for a feature
    /// nobody has opened would be work done on every login for nothing.</para>
    /// </summary>
    private TestCityD3D? _d3d;

    private readonly CityMeshBuilder _mesh = new();

    /// <summary>
    /// Which route ran last frame. Reported rather than assumed: a silent fallback from the depth
    /// path to the painted one would look like the depth path working badly.
    ///
    /// <para><b>It means the depth route RAN, never that the city appeared.</b> This property said
    /// "ACTIVE — real geometry, depth-tested per pixel" on the panel for a whole release while a zero
    /// colour-write mask meant not one pixel ever reached the surface, and that green line is the
    /// single reason the fault survived so long: nothing between here and the screen is measured, so
    /// nothing here may claim the screen. Pair it with <see cref="DepthSubmitted"/> and say what was
    /// actually observed — §4A pre-flight question 2, failed in the field.</para>
    /// </summary>
    public bool UsingDepthRenderer { get; private set; }

    /// <summary>Whether the depth route issued a real draw call last frame, as opposed to running
    /// and finding nothing to hand over. Still only submission — see <see cref="UsingDepthRenderer"/>.</summary>
    public bool DepthSubmitted => _d3d?.DrawSubmitted ?? false;

    public string DepthStatus =>
        _d3d == null            ? "not started"
      : !_d3d.IsReady           ? $"unavailable — {_d3d.LastError}"
      : !_d3d.DrawSubmitted     ? "ran, but no draw call was issued — the mesh was empty"
      : !_d3d.DepthCaptured     ? $"submitted, NO depth capture — {_d3d.LastError}"
      : $"submitted {_d3d.TrianglesLastFrame} tri · {_d3d.LinesLastFrame} line · depth {_d3d.DepthInfo}";

    public bool DepthAvailable => _d3d?.IsReady ?? false;
    public bool DepthCaptured  => _d3d?.DepthCaptured ?? false;
    public int  MeshVertices   => _d3d?.VerticesLastFrame ?? 0;

    // ── diagnostics, all default OFF and all dev-only ────────────────────────
    //
    // THREE INDEPENDENT SWITCHES, ONE PER CANDIDATE. The depth route submits a draw and nothing
    // appears, which leaves exactly three possibilities — the transform is wrong, the depth
    // comparison rejects everything, or the surface never reaches the screen. Each switch answers
    // ONE of those and says nothing about the others, which is the point: a single "debug mode"
    // that changes three things at once cannot tell you which one mattered.

    /// <summary>Runs the depth route with the scene-depth comparison bypassed. NOT the same as
    /// selecting "Off", which switches to the painted renderer entirely and tests none of this —
    /// the trap that wasted a round of testing.</summary>
    public bool DebugBypassSceneDepth;

    /// <summary>Clears the offscreen target to magenta, draws no geometry, and blits it fully
    /// opaque. A magenta screen proves the target, the SRV and the ImGui presentation route all
    /// work, independently of any transform or depth test. A black screen means the surface never
    /// arrives, and no amount of geometry debugging would have helped.</summary>
    public bool DebugClearOnly;

    /// <summary>
    /// Where our own ViewProj puts a known world point, next to where Dalamud's
    /// <c>WorldToScreen</c> puts the same point.
    ///
    /// <para><b>Pure CPU arithmetic against a known-good second opinion, and it needs no GPU at
    /// all.</b> The painted renderer has always worked, and it projects through
    /// <c>WorldToScreen</c> — so that function is a trusted oracle for this exact question. If our
    /// matrix agrees with it the transform is exonerated in one glance; if it disagrees, nothing
    /// about the depth buffer or the blit is worth investigating yet. This is the cheapest
    /// discriminator available and it should have been the first thing built.</para>
    /// </summary>
    public string DebugProjection { get; private set; } = "not sampled";

    /// <summary>Dumps one frame of the depth route's real state to the Dalamud log. Set by a panel
    /// button; clears itself. Reading the log directly beats asking a person to retype numbers.</summary>
    public bool DebugLogNextFrame;

    /// <summary>Which of the game's matrices transforms the city. Auto rejects any whose third
    /// column is zero, because such a matrix cannot produce depth for any vertex.</summary>
    public CityMatrixSource MatrixSource = CityMatrixSource.Auto;

    /// <summary>Which candidate the last frame actually used. On the panel because the wrong choice
    /// is otherwise invisible — the city just does not appear.</summary>
    public string MatrixChosen { get; private set; } = "not selected";

    private void SampleProjection(Matrix4x4 viewProj)
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) { DebugProjection = "no local player"; return; }

        // Chest height rather than the feet: a point on the ground can legitimately sit just off
        // the bottom of the screen, and "off screen" would then be indistinguishable from "wrong".
        var world = lp.Position + new Vector3(0f, 1f, 0f);

        var clip = Vector4.Transform(new Vector4(world, 1f), viewProj);

        var io    = ImGui.GetIO();
        float vpW = io.DisplaySize.X, vpH = io.DisplaySize.Y;

        string ours;
        if (MathF.Abs(clip.W) < 1e-6f)
        {
            ours = "w≈0 (degenerate)";
        }
        else
        {
            // NDC → pixels. Y flips because clip-space +Y is up and pixel +Y is down.
            float nx = clip.X / clip.W, ny = clip.Y / clip.W, nz = clip.Z / clip.W;
            ours = $"ndc({nx,7:F3},{ny,7:F3},{nz,7:F4}) → px({(nx + 1f) * 0.5f * vpW,7:F1},{(1f - ny) * 0.5f * vpH,7:F1})";
        }

        string oracle = AreaOverlay.Project(world, out var screen)
                      ? $"px({screen.X,7:F1},{screen.Y,7:F1})"
                      : "behind camera";

        DebugProjection = $"ours   {ours}\n"
                        + $"clip   ({clip.X:F2}, {clip.Y:F2}, {clip.Z:F4}, {clip.W:F3})\n"
                        + $"oracle {oracle}   (Dalamud WorldToScreen — the painted path's projector)\n"
                        + $"vp     {vpW:F0}×{vpH:F0}";
    }

    /// <summary>
    /// Panels submitted on the last frame.
    ///
    /// <para><b>This counter is the point, not decoration.</b> Face culling that comes out backwards
    /// draws precisely the hidden faces, and a city whose every face was culled is indistinguishable
    /// from a city that was never built. Buildings &gt; 0 with panels 0 says the cull is inverted;
    /// both 0 says placement found nowhere to build.</para>
    /// </summary>
    public int PanelsDrawn { get; private set; }

    /// <summary>Occlusion rays fired on the last frame — the budget, made visible.</summary>
    public int RaysLastFrame { get; private set; }

    /// <summary>Buildings actually drawn, against <see cref="BuildingCount"/> placed.</summary>
    public int RenderedCount => _rendered.Count;

    /// <summary>Whether the eye used for culling and ordering came from the game camera or from the
    /// player-head fallback. Surfaced because a wrong eye looks exactly like broken geometry.</summary>
    public bool EyeFromCamera { get; private set; }

    public bool IsBuilt => _samples.GetLength(0) >= 2 && _tiles > 0;

    public int   BuildingCount => _buildings.Count;
    public int   TilesAcross   => _tiles;
    public float BuiltTileSize => _builtTileSize;
    public float SampleSpacing => _sampleSpacing;
    public int   SamplesPerSide => _samples.GetLength(0);

    public float Extent => _tiles * _builtTileSize;

    public int PlayerTileX => _playerI;
    public int PlayerTileZ => _playerJ;

    // ── geometry, shared by the grid and the buildings ───────────────────────

    /// <summary>
    /// World XZ of a tile corner. <b>The single source of tile geometry</b> — the grid drawer and the
    /// building renderer both come through here, so there is one copy of
    /// <c>origin + index * tileSize</c> in the whole feature and nothing to drift against.
    /// </summary>
    public Vector2 TileCorner(int i, int j) =>
        new(_originX + i * _builtTileSize, _originZ + j * _builtTileSize);

    /// <summary>
    /// Measured ground height at a world XZ, bilinear over the sample lattice.
    ///
    /// <para>Clamped to the lattice rather than refusing outside it: a building's edge or a grid line
    /// at the very boundary should flatten against the edge sample, not fall through to zero.</para>
    /// </summary>
    public float HeightAt(float x, float z)
    {
        int n = _samples.GetLength(0);
        if (n < 2) return 0f;

        float span = MathF.Max(0.0001f, (n - 1) * _sampleSpacing);

        float fi = Math.Clamp((x - _samples[0, 0].X) / span, 0f, 1f) * (n - 1);
        float fj = Math.Clamp((z - _samples[0, 0].Z) / span, 0f, 1f) * (n - 1);

        int i0 = Math.Clamp((int)fi, 0, n - 2);
        int j0 = Math.Clamp((int)fj, 0, n - 2);

        float ti = fi - i0;
        float tj = fj - j0;

        float y00 = _samples[i0,     j0    ].Y;
        float y10 = _samples[i0 + 1, j0    ].Y;
        float y01 = _samples[i0,     j0 + 1].Y;
        float y11 = _samples[i0 + 1, j0 + 1].Y;

        return (y00 * (1f - ti) + y10 * ti) * (1f - tj)
             + (y01 * (1f - ti) + y11 * ti) * tj;
    }

    /// <summary>A tile corner lifted to the measured ground — what both drawers actually want.</summary>
    public Vector3 TileCornerGround(int i, int j)
    {
        var c = TileCorner(i, j);
        return new Vector3(c.X, HeightAt(c.X, c.Y), c.Y);
    }

    /// <summary>
    /// The tile a world XZ falls in — the exact inverse of <see cref="TileCorner"/>, and kept beside
    /// it so the two cannot be changed independently. Not clamped: the caller decides what being
    /// outside the city means.
    /// </summary>
    public (int I, int J) TileIndexAt(float x, float z) =>
        ((int)MathF.Floor((x - _originX) / MathF.Max(0.0001f, _builtTileSize)),
         (int)MathF.Floor((z - _originZ) / MathF.Max(0.0001f, _builtTileSize)));

    // ── actions ──────────────────────────────────────────────────────────────

    /// <summary>Measures the ground under the player, then stands buildings on it.</summary>
    public string Build()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        try
        {
            Clear();

            _territory = Plugin.ClientState.TerritoryType;

            SampleGround(player.Position);
            PlaceBuildings();

            return $"city built — {_buildings.Count} building(s), {_tiles}×{_tiles} tiles of "
                 + $"{_builtTileSize:0.###}y ({Extent:0}y across), ground sampled every "
                 + $"{_sampleSpacing:0.##}y.";
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

        _samples = new Vector3[0, 0];
        _tiles   = 0;
        _buildings.Clear();
        _rendered.Clear();
        PanelsDrawn   = 0;
        RaysLastFrame = 0;
        _occlCursor   = 0;

        return had ? "city cleared." : "no city to clear.";
    }

    /// <summary>Re-measures and re-places. Used by the panel's sliders: changing the tile size or the
    /// extent has to be visible on the city in front of you, not on the next one.</summary>
    public void Rebuild()
    {
        if (!IsBuilt) return;
        Build();
    }

    /// <summary>
    /// Drops the city when the measured ground stops being the ground we are standing on. The
    /// samples are heights in one territory; carried into another they would draw a city hanging in
    /// the air at the old zone's altitudes.
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

    // ── measurement ──────────────────────────────────────────────────────────

    /// <summary>
    /// Measures ground height on its own spacing, and fixes the tile origin.
    ///
    /// <para><b>The lattice is snapped to a world-aligned multiple of the tile size</b>, not centred
    /// exactly on the player. Two builds from slightly different standing positions then land on the
    /// same tiles, which is what makes "that building is on that tile" checkable twice rather than a
    /// coincidence of where you happened to stop walking.</para>
    /// </summary>
    private void SampleGround(Vector3 centre)
    {
        int   tiles = Math.Clamp(GridTiles, MinGridTiles, MaxGridTiles);
        float t     = Math.Clamp(TileSize, MinTileSize, MaxTileSize);

        _originX = MathF.Floor(centre.X / t) * t - (tiles / 2) * t;
        _originZ = MathF.Floor(centre.Z / t) * t - (tiles / 2) * t;
        _builtTileSize = t;
        _tiles         = tiles;

        float extent = tiles * t;

        // Samples wanted at the requested spacing, then CLAMPED — and the spacing recomputed from
        // whatever count survived. That inversion is the whole safety: the slider expresses a wish
        // and the cap decides, so no combination of extent and spacing can ask for more rays than
        // MaxSamplesPerSide squared.
        int wanted = (int)MathF.Ceiling(extent / MathF.Max(0.1f, MeasureSpacing)) + 1;
        int n      = Math.Clamp(wanted, 2, MaxSamplesPerSide);

        _sampleSpacing = extent / (n - 1);

        var samples = new Vector3[n, n];

        for (int i = 0; i < n; i++)
        {
            float wx = _originX + i * _sampleSpacing;

            for (int j = 0; j < n; j++)
            {
                float wz = _originZ + j * _sampleSpacing;

                // GroundAt never fails — a ray that finds nothing keeps the reference height. Right
                // for drawing (a hole in the grid is worse than a vertex slightly off) and wrong for
                // placing anything the player has to reach, which is not what this is.
                var g = DigGround.GroundAt(centre, wx, wz);
                samples[i, j] = new Vector3(wx, g.Y, wz);
            }
        }

        _samples = samples;

        _playerI = Math.Clamp((int)MathF.Floor((centre.X - _originX) / t), 0, tiles - 1);
        _playerJ = Math.Clamp((int)MathF.Floor((centre.Z - _originZ) / t), 0, tiles - 1);

        Diag.Info($"[City] ground sampled: {n}×{n} point(s) every {_sampleSpacing:0.##}y over "
                + $"{extent:0}y; tile grid {tiles}×{tiles} at {t:0.###}y.");
    }

    // ── placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the grid with blocks on a fixed pitch, skipping the ones that would land on the player.
    /// A regular pitch is deliberate: the thing being looked at is whether buildings sit on tile
    /// boundaries, and a grid of them makes a half-tile misalignment obvious across the whole scene
    /// instead of arguable on one box.
    /// </summary>
    private void PlaceBuildings()
    {
        int w      = Math.Clamp(BuildingTiles, 1, 64);
        int street = Math.Clamp(StreetTiles, 1, 64);
        int clear  = Math.Clamp(ClearTiles, 0, 200);
        int pitch  = w + street;

        int  idx  = 0;
        bool full = false;

        for (int i = 0; i + w <= _tiles && !full; i += pitch)
        {
            for (int j = 0; j + w <= _tiles; j += pitch)
            {
                bool overPlayer = _playerI >= i - clear && _playerI < i + w + clear
                               && _playerJ >= j - clear && _playerJ < j + w + clear;
                if (overPlayer) continue;

                if (_buildings.Count >= MaxBuildings) { full = true; break; }

                _buildings.Add(MakeBuilding(i, j, w, idx++));
            }
        }

        if (full)
            Diag.Info($"[City] stopped at the {MaxBuildings}-building cap — widen the streets, "
                    + "enlarge the footprint, or shrink the grid for a full block layout.");
    }

    private CityBuilding MakeBuilding(int i, int j, int w, int idx)
    {
        // Quantised to whole floors so the window rows divide the wall exactly and the top row is
        // not a sliver. The slider range is honoured to within one storey.
        float wanted = MinHeight + (float)_rng.NextDouble() * MathF.Max(0f, MaxHeight - MinHeight);
        int   floors = Math.Clamp((int)MathF.Round(wanted / MathF.Max(1f, FloorHeight)), 1, 60);
        float height = floors * MathF.Max(1f, FloorHeight);

        Footprint(i, j, w, out float lowest, out float highest);

        // Door faces the middle of the city: a building east of centre opens westward. Streets are
        // the gaps between blocks, so that is also the side a street is on.
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

    /// <summary>
    /// Lowest and highest ground under a footprint, from the interpolated surface.
    ///
    /// <para>Nine probes — corners, edge midpoints and the centre — rather than every tile corner.
    /// The surface between samples is a bilinear patch, so more probes on a small footprint cannot
    /// reveal anything the nine miss; and they cost nothing, since none of them is a ray.</para>
    /// </summary>
    private void Footprint(int i, int j, int w, out float lowest, out float highest)
    {
        lowest  = float.MaxValue;
        highest = float.MinValue;

        for (int a = 0; a <= 2; a++)
        {
            for (int b = 0; b <= 2; b++)
            {
                var c = TileCorner(i + a * w / 2, j + b * w / 2);
                float y = HeightAt(c.X, c.Y);

                if (y < lowest)  lowest  = y;
                if (y > highest) highest = y;
            }
        }

        if (lowest > highest) { lowest = 0f; highest = 0f; }
    }

    /// <summary>Distinct hues rather than shades of one, cycled in order instead of drawn at random —
    /// random picks put two of the same colour side by side often enough to look like a bug.</summary>
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
    /// Draws the city, by whichever route <see cref="Mode"/> asks for.
    ///
    /// <para><b>The depth route is tried first and falls through on ANY failure</b>, in the same
    /// frame, to the painted one. A dev experiment must never be the reason the plugin stops
    /// drawing — and a silent fallback would be worse than a crash, so the panel reports which route
    /// actually ran.</para>
    /// </summary>
    public void DrawWorld()
    {
        if (!IsBuilt) return;

        try
        {
            EyeFromCamera = TryEye(out var eye);
            if (!EyeFromCamera)
            {
                // No camera to read. Ordering, culling and occlusion all need an eye, and a fallback
                // is only honest if it is visible — hence EyeFromCamera in the panel. Roughly head
                // height above the player keeps the scene coherent.
                var lp = Plugin.ObjectTable.LocalPlayer;
                if (lp == null) { PanelsDrawn = 0; return; }
                eye = lp.Position + new Vector3(0f, 8f, 0f);
            }

            SelectRendered(eye);

            var look = new CityLook
            {
                WallAlpha   = Math.Clamp(WallAlpha, 0.05f, 1f),
                LitPercent  = Math.Clamp(LitPercent, 0, 100),
                ShowWindows = ShowWindows,
                ShowRoofs   = ShowRoofs,
                ShowEdges   = ShowEdges,
                Occlude     = Mode == CityOcclusion.Raycast,
                LitRgb      = LitRgb,
                DarkRgb     = DarkRgb,
            };

            if (Mode == CityOcclusion.DepthBuffer && DrawViaDepth(eye, look)) return;

            // ── painted fallback ─────────────────────────────────────────────
            UsingDepthRenderer = false;

            if (ShowGrid) TestCityGrid.Draw(this, eye, GridRgb, FineTiles, MajorEvery, ShowChecker);

            RefreshOcclusion(eye);

            PanelsDrawn = TestCityRender.Draw(this, _rendered, eye, look);
        }
        catch (Exception ex)
        {
            Diag.Error($"[City] world draw failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The D3D11 route: build the mesh, hand it to the GPU, let the depth buffer sort it out.
    /// Returns false if anything was missing, so the caller can paint instead this frame.
    /// </summary>
    private bool DrawViaDepth(Vector3 eye, in CityLook look)
    {
        _d3d ??= new TestCityD3D();

        if (!_d3d.TryInitialise()) return false;
        if (!TestCityD3D.TryViewProj(MatrixSource, out var viewProj, out var chosen)) return false;

        MatrixChosen = chosen;

        SampleProjection(viewProj);

        _d3d.DebugClearOnly = DebugClearOnly;

        if (DebugLogNextFrame)
        {
            DebugLogNextFrame     = false;
            _d3d.DebugLogNextFrame = true;

            Diag.Info("[City] ===== one-frame depth-route trace =====");
            Diag.Info($"[City] TRACE mode={Mode} built={IsBuilt} buildings={_buildings.Count} "
                    + $"rendered={_rendered.Count} showGrid={ShowGrid} "
                    + $"bypassSceneDepth={DebugBypassSceneDepth} clearOnly={DebugClearOnly}");
            Diag.Info($"[City] TRACE eyeFromCamera={EyeFromCamera} eye={eye}");
            foreach (var line in DebugProjection.Split('\n'))
                Diag.Info($"[City] TRACE proj {line}");
            Diag.Info($"[City] TRACE viewProj row0 {viewProj.M11,10:F4} {viewProj.M12,10:F4} {viewProj.M13,10:F4} {viewProj.M14,10:F4}");
            Diag.Info($"[City] TRACE viewProj row1 {viewProj.M21,10:F4} {viewProj.M22,10:F4} {viewProj.M23,10:F4} {viewProj.M24,10:F4}");
            Diag.Info($"[City] TRACE viewProj row2 {viewProj.M31,10:F4} {viewProj.M32,10:F4} {viewProj.M33,10:F4} {viewProj.M34,10:F4}");
            Diag.Info($"[City] TRACE viewProj row3 {viewProj.M41,10:F4} {viewProj.M42,10:F4} {viewProj.M43,10:F4} {viewProj.M44,10:F4}");

            // EVERY candidate's Z column, so the choice is evidence rather than a preference. A
            // matrix whose third column is all zeros gives clip.z == 0 for every vertex in the
            // world, which under reverse-Z fails both our own depth test and the scene comparison.
            Diag.Info($"[City] TRACE matrixSource={MatrixSource} chosen='{MatrixChosen}'");
            foreach (var (name, m) in TestCityD3D.MatrixCandidates())
                Diag.Info($"[City] TRACE candidate Zcol=({m.M13,9:F4},{m.M23,9:F4},{m.M33,9:F4},{m.M43,9:F4}) "
                        + $"Wcol=({m.M14,9:F4},{m.M24,9:F4},{m.M34,9:F4},{m.M44,9:F4})  {name}");
        }

        _mesh.Clear();

        if (ShowGrid)
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            _mesh.AddGrid(this, lp?.Position ?? eye, GridRgb, FineTiles, MajorEvery, ShowChecker);
        }

        foreach (var b in _rendered) _mesh.AddBuilding(this, b, look);

        var vertices = _mesh.ToArray();

        if (!_d3d.Render(vertices, _mesh.TriangleVertexCount, viewProj,
                         Mode != CityOcclusion.None && !DebugBypassSceneDepth,
                         DepthBias, look.WallAlpha))
            return false;

        UsingDepthRenderer = true;
        PanelsDrawn        = _d3d.TrianglesLastFrame;
        RaysLastFrame      = 0;

        if (_d3d.DebugLogNextFrame)
        {
            _d3d.DebugLogNextFrame = false;
            Diag.Info($"[City] TRACE geometry path: meshVerts={vertices.Length} "
                    + $"triVerts={_mesh.TriangleVertexCount} submitted={_d3d.DrawSubmitted} "
                    + $"tri={_d3d.TrianglesLastFrame} line={_d3d.LinesLastFrame} "
                    + $"depthCaptured={_d3d.DepthCaptured} depth={_d3d.DepthInfo} "
                    + $"lastError='{_d3d.LastError}'");
        }

        return true;
    }

    /// <summary>
    /// Picks the nearest buildings within the draw distance. Rebuilt every frame rather than cached:
    /// the camera moves constantly, and sorting a few hundred floats is nothing next to the panels it
    /// decides not to draw.
    /// </summary>
    private void SelectRendered(Vector3 eye)
    {
        _rendered.Clear();

        float limit = DrawDistance > 0f ? DrawDistance : float.MaxValue;

        foreach (var b in _buildings)
        {
            var c = TileCorner(b.TileX + b.TilesX / 2, b.TileZ + b.TilesZ / 2);

            // Flat distance on purpose: a tall building's height should not push it out of the draw
            // set, and the eye is usually well above the ground anyway.
            b.EyeDistance = MathF.Sqrt((c.X - eye.X) * (c.X - eye.X) + (c.Y - eye.Z) * (c.Y - eye.Z));

            if (b.EyeDistance <= limit) _rendered.Add(b);
        }

        _rendered.Sort(static (x, y) => x.EyeDistance.CompareTo(y.EyeDistance));

        if (_rendered.Count > MaxRendered) _rendered.RemoveRange(MaxRendered, _rendered.Count - MaxRendered);
    }

    /// <summary>
    /// Re-tests occlusion for a budgeted slice of buildings, round-robin.
    ///
    /// <para>The cursor walks <c>_buildings</c> — the placed set, in a fixed order — rather than the
    /// render set, which is re-sorted every frame and would make the cursor meaningless. Buildings
    /// outside the render set are skipped without a ray, so the budget is spent where it shows.</para>
    /// </summary>
    private void RefreshOcclusion(Vector3 eye)
    {
        RaysLastFrame = 0;

        if (_buildings.Count == 0) return;

        if (Mode != CityOcclusion.Raycast)
        {
            // Reset to fully visible as the toggle goes off, or a stale mask would keep hiding parts
            // of a building with occlusion apparently disabled — which looks like a rendering bug and
            // is exactly the kind of thing that costs an hour.
            foreach (var b in _buildings) b.Occlusion = TestCityOcclusion.AllFacesVisible();
            return;
        }

        int faces = 0;
        int steps = 0;

        while (faces < OcclusionFacesPerFrame && steps < _buildings.Count)
        {
            var b = _buildings[_occlCursor % _buildings.Count];
            _occlCursor = (_occlCursor + 1) % _buildings.Count;
            steps++;

            if (!_rendered.Contains(b)) continue;

            faces += TestCityRender.TestOcclusion(this, b, eye);
        }

        RaysLastFrame = faces * TestCityOcclusion.CellCount;
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
    /// checks ARE the safety.</para>
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

    /// <summary>
    /// Releases the D3D resources. <b>Managed COM releases only — no game code</b>, because unload
    /// runs on every rebuild at a moment the player did not choose (BROKEN.md 012).
    /// </summary>
    public void Dispose()
    {
        _d3d?.Dispose();
        _d3d = null;
    }
}
#endif
