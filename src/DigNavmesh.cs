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
    /// <c>vnavmesh.Query.Mesh.NearestPoint</c> — <c>(position, halfExtentXZ, halfExtentY)</c>
    /// returning the nearest point ON the navmesh, or null when there is none within the extents.
    ///
    /// <para>Signature taken from JumpSolver's IPC table, which is this machine's verified record of
    /// vnavmesh's surface — not inferred from the name.</para>
    /// </summary>
    private static ICallGateSubscriber<Vector3, float, float, Vector3?>? _nearest;
    private static ICallGateSubscriber<bool>?                            _isReady;
    private static bool _resolved;

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;

        try
        {
            // GetIpcSubscriber never throws even when the provider is absent; only InvokeFunc does.
            _isReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            _nearest = Plugin.PluginInterface
                             .GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
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

            try   { return _isReady?.InvokeFunc() == true && _nearest != null; }
            catch { return false; }   // provider absent — the only way InvokeFunc fails
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
        Resolve();

        if (_nearest == null) return null;

        try
        {
            if (_isReady?.InvokeFunc() != true) return null;

            var snapped = _nearest.InvokeFunc(point, tolerance, tolerance);
            if (snapped is not { } s) return false;

            return DigGround.Flat(point, s) <= tolerance
                && MathF.Abs(point.Y - s.Y) <= tolerance;
        }
        catch
        {
            // vnavmesh not loaded. Not an error worth logging every placement attempt.
            return null;
        }
    }
}
#endif
