using System;
using System.Numerics;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TieriChallengesFFXIV;

/// <summary>
/// Drops the game's own map flag on a challenge's location.
///
/// <para><b>There is nothing to attach a pin to.</b> The flag is not an object a challenge owns —
/// it is a single global slot in <c>AgentMap</c>, addressed by (territory, map, world position).
/// <c>SetFlagMapMarker</c> zeroes <c>FlagMarkerCount</c> on entry, so setting one REPLACES whatever
/// was there. "One pin at a time" is therefore the game's behaviour, not something this plugin has
/// to enforce, and there is no lifetime to manage, nothing to persist, and nothing to clean up when
/// a challenge completes or the plugin unloads.</para>
///
/// <para><b>Same zone only, by construction.</b> The only caller is the "you are in this zone"
/// marker on a challenge row, and that marker is only built when the player's current territory
/// matches the challenge's — so the pin inherits the constraint from its one entry point rather
/// than re-checking it. Do NOT add a second call site without that gate: <c>SetFlagMapMarker</c>
/// will happily flag a map the player is nowhere near, which is a cross-zone waypoint and not what
/// this feature is.</para>
///
/// <para><b>World coordinates go in raw.</b> The <c>Vector3</c> overload takes a game-space position
/// and does the map conversion itself (it reads X and Z, rounds to three decimals). Our
/// <see cref="ChallengeArea.Center"/> is already in that space, so no <c>SizeFactor</c>/<c>Offset</c>
/// arithmetic is involved anywhere in this plugin. Verified against the decompiled
/// <c>AgentMap.SetFlagMapMarker</c> rather than assumed — an earlier draft of this comment had it
/// taking map coordinates, which would have put every pin in the wrong place.</para>
/// </summary>
internal static unsafe class MapPinService
{
    /// <summary>
    /// Where a challenge should be pinned: the next thing the player actually has to do.
    ///
    /// <para>Not simply "the first area" — a half-finished adventure would keep pointing at a stop
    /// already done, which is the one place a pin is useless. Falls back to the first stop when
    /// everything is somehow satisfied, so the call never returns nothing for a well-formed
    /// challenge.</para>
    /// </summary>
    public static Vector3? LocationOf(Configuration cfg, CustomChallenge c)
    {
        if (c == null) return null;

        // A race points at its start line. The finish is the spoiler — half the challenge is
        // working out the route to it.
        if (c.Kind == ChallengeKind.RaceTimer)
            return c.RaceStart?.Center;

        // A chain points at whatever its CURRENT step needs, never at a later one.
        var step = ChallengeCatalog.CurrentStep(c);
        if (step != null)
            return NextStopCenter(step.Requirements, step.Id, step.Mode);

        if (c.Requirements is { Count: > 0 })
            return NextStopCenter(c.Requirements, c.Id, c.Mode);

        // Legacy kinds keep their volumes in Areas.
        if (c.Areas is { Count: > 0 }) return c.Areas[0].Center;

        return null;
    }

    private static Vector3? NextStopCenter(
        System.Collections.Generic.List<AreaRequirement> reqs, string key, AreaMode mode)
    {
        if (reqs == null || reqs.Count == 0) return null;

        var done = Plugin.Progress.Stops(key);

        for (int i = 0; i < reqs.Count; i++)
        {
            // Ordered sets record a prefix, so the count IS the index of the next one.
            bool satisfied = mode == AreaMode.InOrder ? i < done.Count : done.Contains(i);
            if (!satisfied && reqs[i].Area != null) return reqs[i].Area.Center;
        }

        return reqs[0].Area?.Center;
    }

    /// <summary>
    /// Flag the challenge's location and open the map on it.
    ///
    /// <para>Returns false when it could not be done — no location authored, the map agent is not
    /// available, or the territory has no map row. Callers surface that rather than leaving a click
    /// that silently did nothing.</para>
    /// </summary>
    public static bool Pin(Configuration cfg, CustomChallenge c)
    {
        try
        {
            var where = LocationOf(cfg, c);
            if (where == null) return false;

            ushort territory = ChallengeCatalog.EffectiveTerritory(c);
            if (territory == 0) return false;

            uint mapId = PlayerStateReader.MapIdFor(territory);
            if (mapId == 0) return false;

            var agent = AgentMap.Instance();
            if (agent == null) return false;

            agent->SetFlagMapMarker(territory, mapId, where.Value);

            // Opening the map is what makes the click obviously land. The flag alone shows on the
            // minimap with a distance, but at minimap scale it reads as nothing having happened.
            agent->OpenMap(mapId, territory, null, MapType.FlagMarker);

            Plugin.Log.Debug(
                $"[MapPin] flagged \"{c.Title}\" at {where.Value.X:0.#}, {where.Value.Z:0.#} "
              + $"(territory {territory}, map {mapId}).");
            return true;
        }
        catch (Exception ex)
        {
            // Never propagate into the draw loop — a failed pin is a failed convenience.
            Plugin.Log.Error(ex, "[MapPin] failed to set flag");
            return false;
        }
    }
}
