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
    public static ChallengeArea? AreaOf(ChallengeTracker tracker, CustomChallenge c)
    {
        if (c == null) return null;

        // A race points at its start line. The finish is the spoiler — half the challenge is
        // working out the route to it.
        if (c.Kind == ChallengeKind.RaceTimer)
            return c.RaceStart;

        // A chain points at whatever its CURRENT step needs, never at a later one. Chain step
        // progress always persists, so the disk is authoritative for one either way.
        var step = ChallengeCatalog.CurrentStep(c);
        if (step != null)
            return NextStopArea(tracker, step.Requirements, step.Id, step.Mode, persist: true);

        if (c.Requirements is { Count: > 0 })
            return NextStopArea(tracker, c.Requirements, c.Id, c.Mode, persist: !c.SessionOnly);

        // Legacy kinds keep their volumes in Areas.
        if (c.Areas is { Count: > 0 }) return c.Areas[0];

        return null;
    }

    /// <summary>
    /// The first stop that is not satisfied yet.
    /// </summary>
    /// <remarks>
    /// Asked of the TRACKER, not the progress store. A SessionOnly adventure never writes to the
    /// store, so reading it directly meant such a challenge always pinned its first stop — the one
    /// place a pin is useless, and precisely the case this method exists to avoid.
    /// </remarks>
    private static ChallengeArea? NextStopArea(
        ChallengeTracker tracker,
        System.Collections.Generic.List<AreaRequirement> reqs, string key, AreaMode mode,
        bool persist)
    {
        if (reqs == null || reqs.Count == 0) return null;

        var done = tracker.SatisfiedStops(key, persist);

        for (int i = 0; i < reqs.Count; i++)
        {
            // Ordered sets record a prefix, so the count IS the index of the next one.
            bool satisfied = mode == AreaMode.InOrder ? i < done.Count : done.Contains(i);
            if (!satisfied && reqs[i].Area != null) return reqs[i].Area;
        }

        return reqs[0].Area;
    }

    /// <summary>
    /// The map to flag on.
    ///
    /// <para><b>The agent's own CurrentMapId wins over the sheet.</b> <c>TerritoryType.Map</c> gives
    /// ONE map per territory, but a territory can present several — a residential district has its
    /// ward map and its subdivision map, and which one is live depends on where the player is
    /// standing, not on the sheet. Since this only ever runs while the player is in the territory,
    /// the agent already knows the right answer. The sheet is the fallback for the impossible case
    /// where it does not.</para>
    /// </summary>
    private static uint MapIdFor(AgentMap* agent, ushort territory, ChallengeArea? area)
    {
        // (1) The map recorded when the author stood on the spot. The only source that is right
        //     even when the challenge and the player are on DIFFERENT sub-maps of one territory.
        if (area != null && area.MapId != 0) return area.MapId;

        // (2) The map the game currently has live. Right whenever the player and the challenge
        //     share a sub-map, which covers every area authored before MapId was captured.
        if (agent->CurrentTerritoryId == territory && agent->CurrentMapId != 0)
            return agent->CurrentMapId;

        // (3) The sheet. LAST, not first — TerritoryType.Map names one map per territory, and for
        //     a housing district that is the ward, never the subdivision. Using it first is what
        //     put the flag off the edge of the map in 0.81.36.0.
        return PlayerStateReader.MapIdFor(territory);
    }

    /// <summary>
    /// Dev diagnostic: everything that decides where a flag lands, for the CURRENT zone.
    /// Logged once on load so a mis-placed pin can be diagnosed from the log rather than by
    /// guessing at coordinate spaces.
    /// </summary>
    public static void LogZoneDiagnostics()
    {
        try
        {
            ushort territory = (ushort)Plugin.ClientState.TerritoryType;
            var lp = Plugin.ObjectTable.LocalPlayer;
            var agent = AgentMap.Instance();

            uint sheetMap = PlayerStateReader.MapIdFor(territory);
            var (size, offX, offY, mapKey) = PlayerStateReader.MapGeometry(sheetMap);

            Diag.Info(
                $"[MapDiag] territory={territory} sheetMap={sheetMap} key='{mapKey}' "
              + $"sizeFactor={size} offsetX={offX} offsetY={offY} "
              + $"player=({lp?.Position.X ?? 0:0.###}, {lp?.Position.Z ?? 0:0.###})");

            if (agent != null)
            {
                var (aSize, aOffX, aOffY, aKey) = PlayerStateReader.MapGeometry(agent->CurrentMapId);
                Diag.Info(
                    $"[MapDiag] agent curTerr={agent->CurrentTerritoryId} curMap={agent->CurrentMapId} "
                  + $"key='{aKey}' sizeFactor={aSize} offsetX={aOffX} offsetY={aOffY} "
                  + $"curSizeFloat={agent->CurrentMapSizeFactorFloat:0.##} "
                  + $"selMap={agent->SelectedMapId} selSub={agent->SelectedMapSub} "
                  + $"selTerr={agent->SelectedTerritoryId}");
            }
            else
            {
                Diag.Info("[MapDiag] AgentMap unavailable.");
            }
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[MapDiag] failed");
        }
    }

    /// <summary>
    /// Flag the challenge's location and open the map on it.
    ///
    /// <para>Returns false when it could not be done — no location authored, the map agent is not
    /// available, or the territory has no map row. Callers surface that rather than leaving a click
    /// that silently did nothing.</para>
    /// </summary>
    public static bool Pin(ChallengeTracker tracker, CustomChallenge c)
    {
        try
        {
            var area = AreaOf(tracker, c);
            if (area == null) return false;

            Vector3 where = area.Center;

            ushort territory = ChallengeCatalog.EffectiveTerritory(c);
            if (territory == 0) return false;

            var agent = AgentMap.Instance();
            if (agent == null) return false;

            uint mapId = MapIdFor(agent, territory, area);
            if (mapId == 0) return false;

            Diag.Info(
                $"[MapPin] \"{c.Title}\" pos=({where.X:0.###}, {where.Z:0.###}) "
              + $"territory={territory} mapId={mapId} (area={area.MapId} "
              + $"agentCur={agent->CurrentMapId} sheet={PlayerStateReader.MapIdFor(territory)})");

            agent->SetFlagMapMarker(territory, mapId, where);

            // Opening the map is what makes the click obviously land. The flag alone shows on the
            // minimap with a distance, but at minimap scale it reads as nothing having happened.
            agent->OpenMap(mapId, territory, null, MapType.FlagMarker);

            return true;
        }
        catch (Exception ex)
        {
            // Never propagate into the draw loop — a failed pin is a failed convenience.
            Diag.Error(ex, "[MapPin] failed to set flag");
            return false;
        }
    }
}
