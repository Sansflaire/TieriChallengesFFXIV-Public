#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Plugin.Ipc;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Asks vnavmesh whether a point is somewhere a character can actually stand.
///
/// <para><b>This is the reachability oracle the dig tests had been doing without, and the reason
/// they needed one is that a raycast is not one.</b> A downward ray finds the first solid surface
/// under a position, and a housing ward is surrounded by terrain that is perfectly solid and
/// completely unreachable — the sand outside the walls, the ground under the skybox, the far side of
/// a building. Every heuristic layered on top of that ray (height from the player, wall-versus-hill,
/// open space underneath) narrows the problem and none of them solves it, because none of them
/// knows what "reachable" means. A navmesh does: it IS the set of places a character can walk.</para>
///
/// <para><b>Optional by construction.</b> vnavmesh is a third-party plugin and may not be installed,
/// loaded, or finished building its mesh for the current zone. Every call is wrapped, a missing
/// provider simply reports "cannot answer", and placement falls back to the raycast heuristics —
/// with the lab saying plainly that it has done so, because a silent fallback here would look
/// exactly like a working gate that had gone wrong.</para>
/// </summary>
internal static class DigNavmesh
{
    /// <summary>
    /// <c>vnavmesh.Query.Mesh.NearestPointReachable</c> — <c>(position, halfExtentXZ, halfExtentY)</c>
    /// returning the nearest point on the <b>reachable</b> navmesh, or null when there is none.
    ///
    /// <para><b>THIS IS THE ONE THAT ANSWERS THE QUESTION, and <c>NearestPoint</c> is not.</b> Read
    /// from vnavmesh 1.2.3.14's own source rather than inferred:</para>
    /// <code>
    ///   RegisterFunc("Query.Mesh.NearestPoint",
    ///       (p, xz, y) => Query?.FindNearestPointOnMesh(p, xz, y));            // default filter
    ///   RegisterFunc("Query.Mesh.NearestPointReachable",
    ///       (p, xz, y) => Query?.FindNearestPointOnMesh(p, xz, y, false));     // allowUnreachable: false
    /// </code>
    /// <para>With <c>allowUnreachable: false</c> the query swaps its polygon filter for a
    /// <c>FloodFillAwareFilter</c>, which rejects any polygon carrying <c>Navmesh.FLAG_UNREACHABLE</c>
    /// — a flag a flood fill sets at mesh build time on islands that are disconnected from the
    /// walkable body of the zone.</para>
    ///
    /// <para><b>That flag is the entire bug.</b> The sand outside a housing ward's walls IS navmesh:
    /// it is real ground, it meshes, and <c>NearestPoint</c> returns it happily because the point
    /// genuinely is on the mesh. It is simply on an island no character can reach. So the gate was
    /// working exactly as written and still let spots land somewhere unreachable, which is why
    /// tightening the tolerance never helped and never could have.</para>
    /// </summary>
    private static ICallGateSubscriber<Vector3, float, float, Vector3?>? _reachable;

    /// <summary>
    /// <c>vnavmesh.Query.Mesh.NearestPoint</c> — the unfiltered version. Kept ONLY as a fallback for
    /// a vnavmesh too old to publish the reachable variant, and the lab says so when it is in use:
    /// it is a strictly weaker gate and must never be mistaken for the real one.
    /// </summary>
    private static ICallGateSubscriber<Vector3, float, float, Vector3?>? _nearest;

    /// <summary>
    /// <c>vnavmesh.Query.Mesh.IsPointOnMesh</c> — <c>(position, halfExtentY, allowUnreachable)</c>.
    /// <b>Note the parameter order: the float is the Y extent and there is no XZ extent at all</b>,
    /// which is not what the sibling queries take. Read from the source; guessing it as
    /// <c>(p, xz, y)</c> would have compiled and silently searched the wrong shaped box.
    /// </summary>
    private static ICallGateSubscriber<Vector3, float, bool, bool>? _onMesh;

    private static ICallGateSubscriber<bool>?  _isReady;

    /// <summary>
    /// <c>vnavmesh.Nav.BuildProgress</c>. <b>A float, verified</b> — <c>public float
    /// LoadTaskProgress</c>. It is 0..1 while a mesh is building and <b>negative when no build is
    /// running at all</b>, which includes both "finished" and "never started". Treating negative as
    /// a percentage would print "-100%" at the one moment a person is staring at it.
    /// </summary>
    private static ICallGateSubscriber<float>? _progress;
    private static bool _resolved;

    /// <summary>
    /// Whether vnavmesh has a FINISHED mesh for the current zone, and how far along it is if not.
    ///
    /// <para><b>Separate from <see cref="Available"/> on purpose.</b> Available answers "is the
    /// provider there"; these answer "has it finished thinking". A query during the build still
    /// returns useful answers for the parts already meshed, which is why placement does not gate on
    /// readiness — but a RUN should not start half-built, because the spots it could place would be
    /// biased toward whichever corner of the zone happened to be meshed first.</para>
    /// </summary>
    public static bool Ready
    {
        get
        {
            Resolve();
            try   { return _isReady?.InvokeFunc() == true; }
            catch { return false; }
        }
    }

    /// <summary>Build progress 0..1, or -1 when it cannot be read.</summary>
    public static float BuildProgress
    {
        get
        {
            Resolve();
            try   { return _progress?.InvokeFunc() ?? -1f; }
            catch { return -1f; }
        }
    }

    /// <summary>A line fit to show a person: ready, building with a percentage, or missing.</summary>
    public static string StatusLine()
    {
        if (!Available) return "vnavmesh is not installed or not loaded.";

        if (Ready)
            return HasReachableQuery
                ? "navmesh ready (reachability-aware)."
                : "navmesh ready, but this vnavmesh has no NearestPointReachable — the gate only "
                + "checks \"is it on the mesh\", which ACCEPTS disconnected islands. Update vnavmesh.";

        float p = BuildProgress;

        // 0..1 is a live build. NEGATIVE means no build task is running at all, which here can only
        // mean this zone has no mesh and nothing is making one — a different problem with a
        // different fix, and printing it as "-100%" would hide that behind a number.
        return p >= 0f
            ? $"Building NavMesh… {p * 100f:0}%"
            : "No navmesh for this zone and nothing is building one — use vnavmesh's Rebuild.";
    }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;

        try
        {
            // GetIpcSubscriber never throws even when the provider is absent; only InvokeFunc does.
            _isReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");

            _reachable = Plugin.PluginInterface
                .GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
            _nearest = Plugin.PluginInterface
                .GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
            _onMesh = Plugin.PluginInterface
                .GetIpcSubscriber<Vector3, float, bool, bool>("vnavmesh.Query.Mesh.IsPointOnMesh");

            _progress = Plugin.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] vnavmesh IPC resolve failed: {ex.Message}");
        }
    }

    /// <summary>Whether vnavmesh is loaded AND has a finished mesh for the current zone.</summary>
    public static bool Available
    {
        get
        {
            Resolve();

            var query = _reachable ?? _nearest;
            if (query == null) return false;

            // Probes the QUERY, not Nav.IsReady. A mesh that is still building answers this
            // perfectly well, and reporting "unavailable" during the build is what let unwalkable
            // spots through in the first place.
            try   { query.InvokeFunc(Vector3.Zero, 1f, 1f); return true; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Whether a character could stand at <paramref name="point"/>, within
    /// <paramref name="tolerance"/> yalms.
    ///
    /// <para><b>Returns null when it cannot answer</b> rather than guessing either way. A caller
    /// that treats "unknown" as "yes" gets the old behaviour, and one that treats it as "no" would
    /// refuse to place anything at all on a machine without vnavmesh — so the distinction has to
    /// survive as far as the caller.</para>
    ///
    /// <para>The tolerance is checked in the answer as well as in the query. NearestPoint searches a
    /// box around the position and returns the closest mesh point inside it, so a point just outside
    /// the walkable area still gets an answer — the one on the nearest pavement. Measuring the gap
    /// is what turns "there is mesh nearby" into "this spot is on it".</para>
    /// </summary>
    public static bool? IsWalkable(Vector3 point, float tolerance)
    {
        var r = TrySnapToReachable(point, tolerance, out _);
        return r;
    }

    /// <summary>
    /// Whether a character can REACH <paramref name="point"/>, and where exactly the reachable mesh
    /// sits if so.
    ///
    /// <para><b>Snapping and testing are one call because they are one question.</b> The query has to
    /// find the nearest reachable mesh point in order to answer at all, so throwing that point away
    /// and keeping only the yes/no discards the most useful half of the answer. Burying the spot at
    /// the snapped position puts it on ground the navmesh agrees is walkable, rather than up to a
    /// tolerance away from it — which is the difference between a dig radius centred on a path and
    /// one centred on the kerb beside it.</para>
    ///
    /// <para><b>Returns null when it cannot answer</b> rather than guessing either way. A caller that
    /// treats "unknown" as "yes" gets the old raycast behaviour; one that treats it as "no" refuses
    /// to place anything without vnavmesh. Both are legitimate and the distinction has to survive as
    /// far as the caller, which is what <c>RoamRequireNavmesh</c> decides.</para>
    /// </summary>
    public static bool? TrySnapToReachable(Vector3 point, float tolerance, out Vector3 snapped)
    {
        snapped = point;

        Resolve();

        // The reachable query is preferred absolutely. NearestPoint is a weaker test that accepts
        // disconnected islands, so it is a fallback for an old vnavmesh and never a co-equal.
        var query = _reachable ?? _nearest;
        if (query == null) return null;

        try
        {
            // NOT gated on Nav.IsReady, and that was a real bug. IsReady is false while the zone's
            // mesh is still building, which turned every query during that window into "cannot
            // answer" — and the caller's fallback accepted the spot. So the one moment the gate was
            // most needed, just after zoning in, was the moment it was switched off. Querying
            // anyway either works or throws, and both are answers.
            var hit = query.InvokeFunc(point, tolerance, tolerance);

            // A query that ran and found nothing is a real NO, not an absence of information.
            if (hit is not { } s) return false;

            if (DigGround.Flat(point, s) > tolerance ||
                MathF.Abs(point.Y - s.Y) > tolerance) return false;

            snapped = s;
            return true;
        }
        catch
        {
            // Provider genuinely absent — the only way InvokeFunc throws. Not worth logging on
            // every placement attempt.
            return null;
        }
    }

    /// <summary>
    /// The bounding box, in WORLD coordinates, of everywhere in this zone a character can actually
    /// walk — measured by sampling the navmesh.
    ///
    /// <para><b>This is the right square for "north" and "east" to be measured against, and the
    /// alternatives are not.</b> The map's coordinate range is the whole theoretical image square,
    /// most of which a subdivision does not occupy. Landmark positions are better but describe where
    /// the game puts LABELS, which cluster around features and miss open ground entirely. The
    /// navmesh is the walkable area by definition, so its extent is the extent of anywhere the
    /// player can be — which is what a quadrant is supposed to divide.</para>
    ///
    /// <para><b>Sampled rather than asked for, because vnavmesh publishes no bounds query.</b> Its
    /// IPC surface has the mesh queries and the bitmap builders, none of which returns an extent. So
    /// this throws a grid at the zone and keeps the reachable hits — using the SAME reachable filter
    /// placement uses, so the box is the box spots can actually land in. Around 900 queries, once
    /// per territory, cached; each is a Detour nearest-poly lookup, which is the same call placement
    /// already makes a hundred times per run.</para>
    ///
    /// <para>Returns false when too few samples land, which means either no mesh or a zone so
    /// enclosed that a grid this coarse misses it. The caller then falls back rather than trusting a
    /// box built from four hits in one corner.</para>
    /// </summary>
    public static bool TryWalkableBounds(Vector2 searchLo, Vector2 searchHi,
                                         out Vector2 lo, out Vector2 hi)
    {
        lo = hi = default;

        if (_boundsTerritory == Plugin.ClientState.TerritoryType && _boundsValid)
        {
            lo = _boundsLo;
            hi = _boundsHi;
            return true;
        }

        _boundsTerritory = Plugin.ClientState.TerritoryType;
        _boundsValid     = false;

        if (!Available) return false;

        const int Steps = 30;

        float stepX = (searchHi.X - searchLo.X) / Steps;
        float stepZ = (searchHi.Y - searchLo.Y) / Steps;

        if (stepX <= 0f || stepZ <= 0f) return false;

        // Generous enough that a sample landing between walkable polygons still finds the mesh.
        // Too tight and a coarse grid reports holes that are really just gaps between probes.
        float tolerance = MathF.Max(5f, MathF.Max(stepX, stepZ) * 0.6f);

        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        int   hits = 0;

        // Y is swept as well as X and Z: a flat probe at one height misses a zone whose walkable
        // surface is far above or below the sampling plane, and "no hits" would then read as "no
        // mesh". Three heights spanning the map's vertical guess is enough to catch that.
        float baseY = Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f;

        for (int ix = 0; ix <= Steps; ix++)
        {
            for (int iz = 0; iz <= Steps; iz++)
            {
                float x = searchLo.X + stepX * ix;
                float z = searchLo.Y + stepZ * iz;

                bool found = false;
                Vector3 snapped = default;

                foreach (float dy in YSweep)
                {
                    if (TrySnapToReachable(new Vector3(x, baseY + dy, z), tolerance, out snapped) != true)
                        continue;

                    found = true;
                    break;
                }

                if (!found) continue;

                hits++;
                if (snapped.X < minX) minX = snapped.X;
                if (snapped.X > maxX) maxX = snapped.X;
                if (snapped.Z < minZ) minZ = snapped.Z;
                if (snapped.Z > maxZ) maxZ = snapped.Z;
            }
        }

        if (hits < 12 || maxX - minX < 1f || maxZ - minZ < 1f) return false;

        _boundsLo    = new Vector2(minX, minZ);
        _boundsHi    = new Vector2(maxX, maxZ);
        _boundsValid = true;
        _boundsHits  = hits;

        Diag.Info($"[Dig] walkable bounds from {hits} navmesh samples: "
                + $"x {minX:0} to {maxX:0}, z {minZ:0} to {maxZ:0}");

        lo = _boundsLo;
        hi = _boundsHi;
        return true;
    }

    /// <summary>Heights probed at each grid point, relative to the player. See the sweep note.</summary>
    private static readonly float[] YSweep = { 0f, 60f, -60f, 200f, -200f };

    private static uint    _boundsTerritory = uint.MaxValue;
    private static bool    _boundsValid;
    private static Vector2 _boundsLo, _boundsHi;
    private static int     _boundsHits;

    /// <summary>How many grid samples produced the cached box, for the lab readout.</summary>
    public static int WalkableSampleCount => _boundsHits;

    /// <summary>Forces the next bounds request to re-sample. Called on a zone change.</summary>
    public static void InvalidateBounds()
    {
        _boundsTerritory = uint.MaxValue;
        _boundsValid     = false;
        _boundsHits      = 0;
    }

    /// <summary>
    /// Whether the reachable-aware query is the one actually in use. False means an older vnavmesh
    /// is installed and the gate has silently degraded to "is it on the mesh at all", which accepts
    /// disconnected islands — the exact failure the reachable query exists to stop. The lab prints
    /// this, because a weaker gate that still says "navmesh ready" is indistinguishable from a
    /// working one right up until a spot lands somewhere you cannot walk.
    /// </summary>
    public static bool HasReachableQuery
    {
        get { Resolve(); return _reachable != null && Available; }
    }
}
#endif
