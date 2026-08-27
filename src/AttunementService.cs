using System;
using System.Collections.Generic;

using Lumina.Excel.Sheets;

namespace TieriChallengesFFXIV;

/// <summary>
/// Which zones the player can currently teleport to, and the sanctioned way to actually send
/// them there.
///
/// <para><b>Verified against shipping sibling code, not derived from a guess.</b> ClaudeAccessXIV
/// (the Brain) exposes exactly this at <c>GET /player/teleport-destinations</c> and
/// <c>POST /player/teleport</c> — see <c>ClaudeAccessXIV/src/HttpApiServer.cs</c> around those
/// two routes. Every struct member and method signature here — <c>Telepo.Instance()</c>,
/// <c>UpdateAetheryteList()</c>, <c>TeleportList[i].AetheryteId/.TerritoryId/.SubIndex</c>,
/// <c>Teleport(aetheryteId, subIndex)</c> — is copied from that already-compiled, already-tested
/// call site rather than reflected or reasoned about from the type name.</para>
///
/// <para><b>Why this reads Telepo directly instead of calling the Brain over HTTP.</b> The Brain
/// may not be running (most players don't have ClaudeAccessXIV installed at all), and a teleport
/// is exactly the kind of action that must not depend on port discovery succeeding. This plugin
/// already has the same <c>FFXIVClientStructs</c> reference the Brain does — see
/// <see cref="PlayerStateReader"/> for the same "read the struct directly" pattern used
/// throughout this codebase.</para>
///
/// <para><b>This is the sanctioned teleport mechanism</b>, not a position write — see
/// <c>devPlugins/CLAUDE.md</c> "Never Teleport the Player Character". <c>Telepo.Teleport</c> is
/// the exact call the in-game Teleport window's own click handler makes: gil cost, attunement
/// check, and the normal cast/animation all still apply.</para>
/// </summary>
internal static class AttunementService
{
    public enum TeleportOutcome
    {
        /// <summary>The client accepted the teleport request. Gil, cast time, etc. proceed normally.</summary>
        Dispatched,

        /// <summary>No aetheryte anywhere in the game targets this territory — not a player state issue.</summary>
        NoAetheryteInZone,

        /// <summary>An aetheryte exists here, but the player has not attuned to it (or any housing entry here).</summary>
        NotAttuned,

        /// <summary>Telepo reported the destination as attuned but refused the call anyway.</summary>
        Failed,
    }

    private readonly record struct Destination(uint AetheryteId, byte SubIndex);

    // ── Live attunement (changes as the player plays) ──────────────────────────

    private static readonly Dictionary<uint, Destination> ByTerritory        = new();
    private static readonly HashSet<uint>                 ReachedExpansions = new();

    /// <summary>
    /// Time-based rather than event-based: nothing in this plugin hooks "player attuned to an
    /// aetheryte" or "player bought a house", so there is no reliable signal to invalidate on.
    /// A couple of seconds of staleness after attuning is a fair trade against wiring a Dalamud
    /// event for what is, at most, a spoiler tag or a right-click that briefly says "not attuned"
    /// a moment after the player actually did.
    /// </summary>
    private const long RefreshIntervalMs = 2000;
    private static long _nextRefreshMs;

    /// <summary>
    /// Trist's rule: residential areas are never spoilered, for anyone (2026-08-24 — see
    /// <see cref="IsResidential"/>). Otherwise, a zone outside an expansion the player has REACHED
    /// is spoilered outright; inside a reached expansion, only zones the player has never been
    /// unlocked into are spoilered; everything else shows. Territory 0 ("not tied to a zone") is
    /// never spoilered.
    ///
    /// <para><b>"Reached" is attunement OR having physically visited</b> — not attunement alone.
    /// The first cut of this only checked <c>Telepo.TeleportList</c> (attuned aetherytes + OWNED
    /// housing), which meant a player who merely walked through a zone with no aetheryte nearby —
    /// or a housing ward before this became a blanket exception — could never clear it.
    /// <see cref="Configuration.VisitedTerritories"/>, recorded by <see cref="RecordVisit"/> the
    /// instant the player's current zone changes, is what makes "I have been here" independent of
    /// "I own something here" or "there was ever anything to attune to".</para>
    /// </summary>
    public static bool IsZoneSpoilered(Configuration cfg, uint territoryId)
    {
        if (territoryId == 0) return false;
        if (IsResidential(territoryId)) return false;
        EnsureLiveBuilt();

        if (IsReached(cfg, territoryId)) return false;
        return !ExpansionReached(cfg, ExpansionOf(territoryId));
    }

    private static bool IsReached(Configuration cfg, uint territoryId) =>
        ByTerritory.ContainsKey(territoryId) || VisitedSet(cfg).Contains(territoryId);

    private static bool ExpansionReached(Configuration cfg, uint expansion) =>
        ReachedExpansions.Contains(expansion) || VisitedExpansions(cfg).Contains(expansion);

    // ── Visited territories — persisted, independent of attunement ─────────────
    //
    // Mirrors Configuration.VisitedTerritories as two HashSets (by territory, and by the
    // expansion each visited territory belongs to) for O(1) lookups on what is otherwise a
    // per-row UI check. The persisted List<uint> stays the source of truth; these are rebuilt
    // from it if it looks like it changed under us (e.g. a fresh load).

    private static HashSet<uint>? _visitedTerritoryMirror;
    private static HashSet<uint>? _visitedExpansionMirror;
    private static int            _visitedMirrorCount = -1;

    private static HashSet<uint> VisitedSet(Configuration cfg)
    {
        SyncVisitedMirrors(cfg);
        return _visitedTerritoryMirror!;
    }

    private static HashSet<uint> VisitedExpansions(Configuration cfg)
    {
        SyncVisitedMirrors(cfg);
        return _visitedExpansionMirror!;
    }

    private static void SyncVisitedMirrors(Configuration cfg)
    {
        cfg.VisitedTerritories ??= new List<uint>();
        if (_visitedTerritoryMirror != null && _visitedMirrorCount == cfg.VisitedTerritories.Count)
            return;

        _visitedTerritoryMirror = new HashSet<uint>(cfg.VisitedTerritories);
        _visitedExpansionMirror = new HashSet<uint>();
        foreach (var tid in _visitedTerritoryMirror)
            _visitedExpansionMirror.Add(ExpansionOf(tid));
        _visitedMirrorCount = cfg.VisitedTerritories.Count;
    }

    /// <summary>
    /// Record the player's current zone as visited, permanently. Called from
    /// <see cref="ChallengeTracker"/> on every territory change — the one place in the plugin that
    /// already knows "the zone just changed" without adding a second hook for it. Returns true
    /// only the first time a given territory is recorded, which is the caller's cue to actually
    /// persist the config; every later visit to an already-known zone is a no-op HashSet lookup.
    /// </summary>
    public static bool RecordVisit(Configuration cfg, uint territoryId)
    {
        if (territoryId == 0) return false;
        if (!VisitedSet(cfg).Add(territoryId)) return false;

        cfg.VisitedTerritories.Add(territoryId);
        _visitedExpansionMirror!.Add(ExpansionOf(territoryId));
        _visitedMirrorCount = cfg.VisitedTerritories.Count;
        return true;
    }

    /// <remarks>
    /// <para><b>Built into locals and swapped in only on success, and the refresh clock only moves
    /// on success.</b> This used to clear both collections up front and push the clock forward
    /// before reading anything. Telepo is null during a loading screen and at the title screen —
    /// both perfectly normal — so a refresh landing in that window wiped the attunement census and
    /// then refused to rebuild it for the next two seconds. Everything read as un-attuned in the
    /// meantime, which through <see cref="IsZoneSpoilered"/> means every zone the player has not
    /// physically visited flips to "??? (unexplored)" and back again on every zone change. Losing a
    /// census you cannot currently rebuild is strictly worse than serving one that is two seconds
    /// stale, which is the entire premise of the interval.</para>
    /// </remarks>
    private static void EnsureLiveBuilt()
    {
        long now = Environment.TickCount64;
        if (now < _nextRefreshMs) return;

        try
        {
            unsafe
            {
                var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();

                // Not an error, and deliberately does NOT advance the clock: a loading screen is
                // the common case, and the next frame should be free to try again.
                if (telepo == null) return;

                // Must be called before TeleportList reflects current attunement/estate state —
                // same requirement the Brain's endpoint documents at its call site.
                telepo->UpdateAetheryteList();

                var byTerritory = new Dictionary<uint, Destination>();
                var reached     = new HashSet<uint>();

                int count = (int)telepo->TeleportList.Count;
                for (int i = 0; i < count; i++)
                {
                    var info = telepo->TeleportList[i];
                    if (info.TerritoryId == 0) continue;

                    // First entry wins when a territory has several (a housing ward with more
                    // than one plot the player owns, say) — any of them teleports into the same
                    // zone, which is all "attuned to this zone" needs to know.
                    if (!byTerritory.ContainsKey(info.TerritoryId))
                        byTerritory[info.TerritoryId] = new Destination(info.AetheryteId, info.SubIndex);

                    reached.Add(ExpansionOf(info.TerritoryId));
                }

                ByTerritory.Clear();
                foreach (var kv in byTerritory) ByTerritory[kv.Key] = kv.Value;

                ReachedExpansions.Clear();
                foreach (uint ex in reached) ReachedExpansions.Add(ex);

                _nextRefreshMs = now + RefreshIntervalMs;
            }
        }
        catch (Exception ex)
        {
            // Clock untouched here too — a transient read failure should not blind the mask for
            // the whole interval, and the previous census is still the best answer available.
            Diag.Error(ex, "[Attunement] could not read Telepo.TeleportList");
        }
    }

    // ── Static census: territory → expansion ────────────────────────────────────
    //
    // Independent of ZoneIndex on purpose. ZoneIndex's territory/expansion map only exists once
    // the Zone tab has actually been built at least once for the current Configuration, and a
    // spoiler check has to work from the Category tab too, before that has ever happened. The
    // game's zone list never changes at runtime, so — like the aetheryte census below — this is
    // built once per session and never invalidated.

    private static Dictionary<uint, uint>? _territoryExVersion;
    private static HashSet<uint>?          _residentialTerritories;

    private static uint ExpansionOf(uint territoryId)
    {
        EnsureTerritoryCensus();
        return _territoryExVersion!.TryGetValue(territoryId, out var found) ? found : 0;
    }

    /// <summary>
    /// Housing wards and interiors — <c>TerritoryIntendedUse</c> 13/14, the same pair
    /// <c>ZoneIndex.IsResidential</c> and ClaudeAccessXIV's <c>isHousing</c> flag use. Trist's
    /// call (2026-08-24): residential areas are unlocked for everyone regardless of attunement or
    /// visit history — nothing about a housing ward is a story spoiler the way an unreached MSQ
    /// zone is, and gating them behind ownership (the only "attunement" a ward actually has) was
    /// exactly the bug <see cref="RecordVisit"/> exists to route around for everything else.
    /// </summary>
    private static bool IsResidential(uint territoryId)
    {
        EnsureTerritoryCensus();
        return _residentialTerritories!.Contains(territoryId);
    }

    private static void EnsureTerritoryCensus()
    {
        if (_territoryExVersion != null) return;

        var exMap = new Dictionary<uint, uint>();
        var residential = new HashSet<uint>();
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet != null)
                foreach (var t in sheet)
                {
                    if (t.RowId == 0) continue;
                    exMap[t.RowId] = t.ExVersion.RowId;
                    if (t.TerritoryIntendedUse.RowId is 13 or 14) residential.Add(t.RowId);
                }
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[Attunement] could not census TerritoryType");
        }
        _territoryExVersion     = exMap;
        _residentialTerritories = residential;
    }

    // ── Static census: does ANY aetheryte in the game target this territory? ───
    //
    // Deliberately independent of attunement and of ZoneIndex's challenge-merged zone list — this
    // answers "could a fully-attuned player ever reach this zone by aetheryte", which is what
    // distinguishes "not attuned yet" from "no aetheryte exists here at all". The game's aetheryte
    // list never changes at runtime, so this is built once per session and never invalidated.

    private static HashSet<uint>? _anyAetheryte;

    private static bool HasAnyAetheryte(uint territoryId)
    {
        if (_anyAetheryte == null)
        {
            var set = new HashSet<uint>();
            try
            {
                var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
                if (sheet != null)
                    foreach (var row in sheet)
                    {
                        uint tid = row.Territory.RowId;
                        if (tid != 0) set.Add(tid);
                    }
            }
            catch (Exception ex)
            {
                Diag.Error(ex, "[Attunement] could not census the Aetheryte sheet");
            }
            _anyAetheryte = set;
        }
        return _anyAetheryte.Contains(territoryId);
    }

    // ── The action ───────────────────────────────────────────────────────────

    /// <summary>
    /// Attempt to teleport to any aetheryte serving <paramref name="territoryId"/>. "Any aetheryte
    /// works" was the explicit spec — this does not try to pick a cheapest or nearest option.
    /// </summary>
    public static TeleportOutcome TryTeleport(uint territoryId)
    {
        // Forces a fresh read regardless of the throttle: this is a deliberate, infrequent action
        // (a right-click), not a per-frame spoiler check across a whole list, so it can afford to
        // skip the 2s staleness window that only exists to keep those checks cheap.
        _nextRefreshMs = 0;
        EnsureLiveBuilt();

        if (!ByTerritory.TryGetValue(territoryId, out var dest))
            return HasAnyAetheryte(territoryId) ? TeleportOutcome.NotAttuned : TeleportOutcome.NoAetheryteInZone;

        try
        {
            unsafe
            {
                var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
                if (telepo == null) return TeleportOutcome.Failed;

                bool dispatched = telepo->Teleport(dest.AetheryteId, dest.SubIndex);
                return dispatched ? TeleportOutcome.Dispatched : TeleportOutcome.Failed;
            }
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[Attunement] Telepo.Teleport threw");
            return TeleportOutcome.Failed;
        }
    }
}
