using System;
using System.Collections.Generic;

using Lumina.Excel;
using LSheets = Lumina.Excel.Sheets;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

// Aliased because the plugin already has a `Framework` in scope (Dalamud's IFramework, held as
// Plugin.Framework) and an unqualified `Framework.Instance()` reads as that one.
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace TieriChallengesFFXIV;

/// <summary>One equipped slot, reduced to what this plugin actually needs.</summary>
public readonly struct EquippedSlot
{
    /// <summary>The physically equipped item.</summary>
    public readonly uint ItemId;

    /// <summary>Glamour override, 0 when none.</summary>
    public readonly uint GlamourId;

    public EquippedSlot(uint itemId, uint glamourId)
    {
        ItemId    = itemId;
        GlamourId = glamourId;
    }

    /// <summary>
    /// What the player VISIBLY wears — glamour wins when set. Outfit matching uses this,
    /// because "am I wearing the Ala Mhigan outfit" is a question about appearance, not about
    /// which item is physically in the slot.
    /// </summary>
    public uint VisibleId => GlamourId != 0 ? GlamourId : ItemId;

    public bool IsEmpty => ItemId == 0;
}

/// <summary>
/// Live reads of player state that the game exposes only through FFXIVClientStructs.
///
/// Every method is defensive: null-checks the player, wraps struct access, and returns a
/// neutral value rather than throwing. These are called from the draw loop and the framework
/// tick, where an exception is a crashed game.
///
/// Sheets are cached in statics after first use — Lumina sheet lookups are cheap but not free,
/// and the outfit index in particular is expensive enough to build exactly once.
/// </summary>
internal static unsafe class PlayerStateReader
{
    private static ExcelSheet<LSheets.Emote>?     _emoteSheet;
    private static ExcelSheet<LSheets.Mount>?     _mountSheet;
    private static ExcelSheet<LSheets.Companion>? _companionSheet;
    private static ExcelSheet<LSheets.Item>?      _itemSheet;

    /// <summary>ActionTimeline row id → Emote row id. Built once; ~1k entries.</summary>
    private static Dictionary<uint, uint>? _timelineToEmote;

    /// <summary>MirageStoreSetItem row id → its non-empty slot item ids.</summary>
    private static List<(uint SetId, string Name, uint[] Slots)>? _outfitIndex;

    private static Character* LocalChara()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return null;
        return (Character*)lp.Address;
    }

    // ── Animation / emote ────────────────────────────────────────────────────

    /// <summary>
    /// Friendly name of what the player is currently doing — "Dance", "Laugh", "Play Dead".
    ///
    /// Resolution order, mirroring how ClaudeAccessXIV's /player/animation does it:
    ///   1. EmoteController.EmoteId — authoritative while an emote is actually running.
    ///   2. Base ActionTimeline id mapped back through the Emote sheet, which catches
    ///      persistent loops (idle poses, sitting, dozing) where EmoteId has been cleared.
    /// Falls back to the raw timeline key so the readout is never blank.
    /// </summary>
    public static string DescribeAnimation()
    {
        try
        {
            var chara = LocalChara();
            if (chara == null) return "not logged in";

            uint emoteId = chara->EmoteController.EmoteId;
            if (emoteId != 0)
            {
                string name = EmoteName(emoteId);
                if (!string.IsNullOrEmpty(name)) return name;
            }

            ushort baseTimeline = chara->Timeline.TimelineSequencer.TimelineIds[0];
            if (baseTimeline != 0)
            {
                uint mapped = MapTimelineToEmote(baseTimeline);
                if (mapped != 0)
                {
                    string name = EmoteName(mapped);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                return $"timeline {baseTimeline}";
            }

            return "idle";
        }
        catch
        {
            return "unavailable";
        }
    }

    /// <summary>Current emote row id, 0 when none is running. Used by the EmoteAtArea challenge.</summary>
    public static uint CurrentEmoteId()
    {
        try
        {
            var chara = LocalChara();
            if (chara == null) return 0;

            uint id = chara->EmoteController.EmoteId;
            if (id != 0) return id;

            ushort baseTimeline = chara->Timeline.TimelineSequencer.TimelineIds[0];
            return baseTimeline != 0 ? MapTimelineToEmote(baseTimeline) : 0u;
        }
        catch
        {
            return 0;
        }
    }

    public static string EmoteName(uint emoteId)
    {
        try
        {
            _emoteSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Emote>();
            var row = _emoteSheet?.GetRowOrDefault(emoteId);
            return row?.Name.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>All emotes that have a name, for the creator's picker.</summary>
    public static List<(uint Id, string Name)> AllEmotes()
    {
        var list = new List<(uint, string)>();
        try
        {
            _emoteSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Emote>();
            if (_emoteSheet == null) return list;

            foreach (var row in _emoteSheet)
            {
                string name = row.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name)) list.Add((row.RowId, name));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Each Emote row references up to 7 ActionTimelines; invert that into timeline → emote so a
    /// running animation can be named. Built once, then a dictionary hit.
    /// </summary>
    private static uint MapTimelineToEmote(ushort timelineId)
    {
        if (_timelineToEmote == null)
        {
            var map = new Dictionary<uint, uint>();
            try
            {
                _emoteSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Emote>();
                if (_emoteSheet != null)
                {
                    foreach (var row in _emoteSheet)
                    {
                        if (string.IsNullOrWhiteSpace(row.Name.ToString())) continue;
                        foreach (var at in row.ActionTimeline)
                        {
                            uint atId = at.RowId;
                            // First writer wins: earlier emote rows are the canonical owners of
                            // shared timelines (e.g. the generic sit loops).
                            if (atId != 0 && !map.ContainsKey(atId)) map[atId] = row.RowId;
                        }
                    }
                }
            }
            catch { }
            _timelineToEmote = map;
        }

        return _timelineToEmote.TryGetValue(timelineId, out var id) ? id : 0u;
    }

    // ── Zone ─────────────────────────────────────────────────────────────────

    private static ExcelSheet<LSheets.TerritoryType>? _territorySheet;

    /// <summary>
    /// The Map row id for a territory, or 0. Needed by the flag marker, which addresses a location
    /// by (territory, map, position) — the territory alone is not enough, because several
    /// territories can share a map and a map is what the flag is drawn on.
    /// </summary>
    public static uint MapIdFor(ushort territory)
    {
        if (territory == 0) return 0;
        try
        {
            _territorySheet ??= Plugin.DataManager.GetExcelSheet<LSheets.TerritoryType>();
            return _territorySheet?.GetRowOrDefault(territory)?.Map.RowId ?? 0u;
        }
        catch { return 0; }
    }

    /// <summary>
    /// The map the game currently considers live, or 0.
    ///
    /// <para>Authoritative where <c>TerritoryType.Map</c> is not: a territory can present several
    /// maps (a housing ward and its subdivision), and only the agent knows which one the player is
    /// actually standing on.</para>
    /// </summary>
    public static uint CurrentMapId()
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
            return agent == null ? 0u : agent->CurrentMapId;
        }
        catch { return 0; }
    }

    private static ExcelSheet<LSheets.Map>? _mapSheet;

    /// <summary>
    /// The geometry a map coordinate conversion depends on: scale factor, the X/Y offsets applied
    /// before scaling, and the map's asset key ("s1d1/00") which identifies it unambiguously in
    /// a log.
    /// </summary>
    public static (ushort SizeFactor, short OffsetX, short OffsetY, string Key) MapGeometry(uint mapId)
    {
        if (mapId == 0) return (0, 0, 0, string.Empty);
        try
        {
            _mapSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Map>();
            var row = _mapSheet?.GetRowOrDefault(mapId);
            if (row == null) return (0, 0, 0, string.Empty);

            return (row.Value.SizeFactor, row.Value.OffsetX, row.Value.OffsetY,
                    row.Value.Id.ToString());
        }
        catch { return (0, 0, 0, string.Empty); }
    }

    public static string ZoneName(ushort territory)
    {
        if (territory == 0) return string.Empty;
        try
        {
            _territorySheet ??= Plugin.DataManager.GetExcelSheet<LSheets.TerritoryType>();
            return _territorySheet?.GetRowOrDefault(territory)?
                       .PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    // ── Mount ────────────────────────────────────────────────────────────────

    /// <summary>Current mount row id, 0 when not mounted.</summary>
    public static uint CurrentMountId()
    {
        try
        {
            var chara = LocalChara();
            if (chara == null) return 0;
            return chara->Mount.MountId;
        }
        catch { return 0; }
    }

    public static string MountName(uint mountId)
    {
        if (mountId == 0) return string.Empty;
        try
        {
            _mountSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Mount>();
            var row = _mountSheet?.GetRowOrDefault(mountId);
            return row?.Singular.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    public static string DescribeMount()
    {
        uint id = CurrentMountId();
        if (id == 0) return "not mounted";
        string name = MountName(id);
        return string.IsNullOrEmpty(name) ? $"mount {id}" : $"{name} (#{id})";
    }

    public static List<(uint Id, string Name)> AllMounts()
    {
        var list = new List<(uint, string)>();
        try
        {
            _mountSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Mount>();
            if (_mountSheet == null) return list;

            foreach (var row in _mountSheet)
            {
                string name = row.Singular.ToString();
                if (!string.IsNullOrWhiteSpace(name)) list.Add((row.RowId, name));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        }
        catch { }
        return list;
    }

    // ── Minion (companion) ───────────────────────────────────────────────────

    /// <summary>
    /// Base id of the minion currently summoned, 0 when none. Same read
    /// <see cref="DescribeMinion"/> uses — <c>CompanionObject</c> is obsolete in current
    /// FFXIVClientStructs and <c>ChildObject</c> is its replacement.
    /// </summary>
    public static uint CurrentMinionId()
    {
        try
        {
            var chara = LocalChara();
            if (chara == null) return 0;

            var companion = chara->ChildObject;
            return companion == null ? 0u : companion->BaseId;
        }
        catch { return 0; }
    }

    public static string MinionName(uint minionId)
    {
        if (minionId == 0) return string.Empty;
        try
        {
            _companionSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Companion>();
            return _companionSheet?.GetRowOrDefault(minionId)?.Singular.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>Every named minion, for the creator's picker.</summary>
    public static List<(uint Id, string Name)> AllMinions()
    {
        var list = new List<(uint, string)>();
        try
        {
            _companionSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Companion>();
            if (_companionSheet == null) return list;

            foreach (var row in _companionSheet)
            {
                string name = row.Singular.ToString();
                if (!string.IsNullOrWhiteSpace(name)) list.Add((row.RowId, name));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        }
        catch { }
        return list;
    }

    // ── Target ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>DataId</c> of whatever the player currently targets, 0 when nothing is targeted.
    ///
    /// <para><b>BaseId, not EntityId.</b> EntityId identifies one live spawn and is different every
    /// time the NPC repopulates, so a challenge keyed on it would work exactly once. BaseId is the
    /// NPC's row identity and is the same for every instance of "Mother Miounne" everywhere.
    /// (Dalamud renamed this property from <c>DataId</c>; the persisted field is still called
    /// TargetDataId because that is the name the rest of the FFXIV ecosystem uses, and renaming a
    /// serialised property gains nothing but a migration.)</para>
    /// </summary>
    public static uint CurrentTargetDataId()
    {
        try
        {
            return Plugin.TargetManager.Target?.BaseId ?? 0u;
        }
        catch { return 0; }
    }

    /// <summary>Display name of the current target, for capture-what-I-am-looking-at in the creator.</summary>
    public static string CurrentTargetName()
    {
        try
        {
            return Plugin.TargetManager.Target?.Name.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    // ── Job / level ──────────────────────────────────────────────────────────

    private static ExcelSheet<LSheets.ClassJob>? _classJobSheet;

    public static uint CurrentJobId()
    {
        try { return Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0u; }
        catch { return 0; }
    }

    public static int CurrentLevel()
    {
        try { return Plugin.ObjectTable.LocalPlayer?.Level ?? 0; }
        catch { return 0; }
    }

    public static string JobName(uint jobId)
    {
        if (jobId == 0) return string.Empty;
        try
        {
            _classJobSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.ClassJob>();
            return _classJobSheet?.GetRowOrDefault(jobId)?.Name.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    public static List<(uint Id, string Name)> AllJobs()
    {
        var list = new List<(uint, string)>();
        try
        {
            _classJobSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.ClassJob>();
            if (_classJobSheet == null) return list;

            foreach (var row in _classJobSheet)
            {
                if (row.RowId == 0) continue;   // row 0 is "adventurer", not a real job
                string name = row.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name)) list.Add((row.RowId, name));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        }
        catch { }
        return list;
    }

    // ── Eorzean clock ────────────────────────────────────────────────────────

    /// <summary>
    /// Current Eorzean hour, 0–23, or -1 when the clock cannot be read.
    ///
    /// <para><c>ClientTime.EorzeaTime</c> is seconds since the Eorzean epoch. The double modulo is
    /// not redundant: the value is signed, and C#'s <c>%</c> keeps the sign of the dividend, so a
    /// negative timestamp (which does occur briefly during a zone transition) would otherwise
    /// produce a negative hour and silently fail every time-of-day window.</para>
    /// </summary>
    public static int EorzeaHour()
    {
        try
        {
            var fw = CSFramework.Instance();
            if (fw == null) return -1;

            long ts      = fw->ClientTime.EorzeaTime;
            long seconds = ((ts % 86400) + 86400) % 86400;
            return (int)(seconds / 3600);
        }
        catch { return -1; }
    }

    public static string DescribeEorzeaTime()
    {
        int h = EorzeaHour();
        return h < 0 ? "unavailable" : $"{h:00}:00 Eorzean";
    }

    public static string DescribeMinion()
    {
        try
        {
            var chara = LocalChara();
            if (chara == null) return "—";

            // CompanionObject is obsolete in current FFXIVClientStructs; ChildObject is the
            // replacement and covers minions, chocobo companions and the like.
            var companion = chara->ChildObject;
            if (companion == null) return "none out";

            uint baseId = companion->BaseId;
            if (baseId == 0) return "none out";

            _companionSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Companion>();
            string name = _companionSheet?.GetRowOrDefault(baseId)?.Singular.ToString() ?? string.Empty;
            return string.IsNullOrEmpty(name) ? $"minion {baseId}" : $"{name} (#{baseId})";
        }
        catch { return "unavailable"; }
    }

    // ── Equipment ────────────────────────────────────────────────────────────

    /// <summary>Slot order matches InventoryType.EquippedItems: 0 MainHand … 12 Ring(R), 13 soul crystal.</summary>
    public static readonly string[] SlotNames =
    {
        "Main Hand", "Off Hand", "Head", "Body", "Hands", "Waist", "Legs", "Feet",
        "Earrings", "Necklace", "Bracelets", "Ring (L)", "Ring (R)", "Soul Crystal",
    };

    public const int EquipSlotCount = 14;

    /// <summary>
    /// Reads the EquippedItems container. Returns an empty array when the container is not
    /// available (loading screen, logged out) — callers must tolerate that rather than assume 14.
    /// </summary>
    public static EquippedSlot[] ReadEquipment()
    {
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null) return Array.Empty<EquippedSlot>();

            var container = inv->GetInventoryContainer(InventoryType.EquippedItems);
            if (container == null) return Array.Empty<EquippedSlot>();

            var result = new EquippedSlot[EquipSlotCount];
            for (int i = 0; i < EquipSlotCount; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) { result[i] = new EquippedSlot(0, 0); continue; }
                result[i] = new EquippedSlot(slot->ItemId, slot->GlamourId);
            }
            return result;
        }
        catch
        {
            return Array.Empty<EquippedSlot>();
        }
    }

    public static string ItemName(uint itemId)
    {
        if (itemId == 0) return string.Empty;
        try
        {
            _itemSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Item>();
            return _itemSheet?.GetRowOrDefault(itemId)?.Name.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Items whose name contains <paramref name="query"/>, capped at <paramref name="limit"/>.
    ///
    /// <para>A search rather than an <c>AllItems()</c> list on purpose: the Item sheet is ~45,000
    /// rows, and materialising all of them just so a picker can filter them would allocate a
    /// multi-megabyte list on every keystroke. This walks the sheet and stops early instead, so the
    /// cost is bounded by the cap and not by the sheet.</para>
    ///
    /// <para>An empty query returns nothing rather than everything — "show me all 45,000 items" is
    /// never what the author meant, and rendering it would hitch the window.</para>
    /// </summary>
    public static List<(uint Id, string Name)> SearchItems(string query, int limit = 200)
    {
        var list = new List<(uint, string)>();
        if (string.IsNullOrWhiteSpace(query)) return list;

        try
        {
            _itemSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Item>();
            if (_itemSheet == null) return list;

            foreach (var row in _itemSheet)
            {
                string name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                list.Add((row.RowId, name));
                if (list.Count >= limit) break;
            }
        }
        catch { }
        return list;
    }

    /// <summary>True when the given item id is equipped in any slot, glamour included.</summary>
    public static bool IsWearing(uint itemId)
    {
        if (itemId == 0) return false;
        var eq = ReadEquipment();
        for (int i = 0; i < eq.Length; i++)
            if (eq[i].ItemId == itemId || eq[i].GlamourId == itemId) return true;
        return false;
    }

    // ── Outfits (MirageStoreSetItem) ─────────────────────────────────────────

    /// <summary>
    /// The game's own outfit definitions — the same rows the Glamour Dresser bundles items
    /// into. Eleven slots in a fixed order that SKIPS Waist:
    /// MainHand, OffHand, Head, Body, Hands, Legs, Feet, Earrings, Necklace, Bracelets, Ring.
    /// (Documented in the suite's master CLAUDE.md; the bit positions of
    /// GlamourDresserItemSetUnlockBits use the same order.)
    /// </summary>
    private static List<(uint SetId, string Name, uint[] Slots)> OutfitIndex()
    {
        if (_outfitIndex != null) return _outfitIndex;

        var list = new List<(uint, string, uint[])>();
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<LSheets.MirageStoreSetItem>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    var slots = new[]
                    {
                        row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
                        row.Hands.RowId,    row.Legs.RowId,    row.Feet.RowId,
                        row.Earrings.RowId, row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
                    };

                    int filled = 0;
                    foreach (var s in slots) if (s != 0) filled++;
                    if (filled == 0) continue;

                    // The set's display name is the container item's own name.
                    string name = ItemName(row.RowId);
                    if (string.IsNullOrWhiteSpace(name)) name = $"Outfit #{row.RowId}";

                    list.Add((row.RowId, name, slots));
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to build outfit index");
        }

        _outfitIndex = list;
        return list;
    }

    /// <summary>Every outfit the game defines, for the creator's picker.</summary>
    public static List<(uint Id, string Name)> AllOutfits()
    {
        var list = new List<(uint, string)>();
        foreach (var (id, name, _) in OutfitIndex()) list.Add((id, name));
        list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return list;
    }

    /// <summary>Slot indices in EquippedItems order that correspond to the 11 outfit slots.</summary>
    private static readonly int[] OutfitSlotToEquipSlot =
    {
        0,  // MainHand
        1,  // OffHand
        2,  // Head
        3,  // Body
        4,  // Hands
        6,  // Legs   (5 is Waist, which outfits never define)
        7,  // Feet
        8,  // Earrings
        9,  // Necklace
        10, // Bracelets
        11, // Ring — outfits define one ring; we accept it on either hand
    };

    /// <summary>
    /// Is the player currently wearing every defined slot of this outfit? Slots the outfit
    /// leaves empty are not required. The ring is accepted on either hand because outfits do
    /// not distinguish left from right.
    /// </summary>
    public static bool IsWearingOutfit(uint outfitSetId, EquippedSlot[] equipment)
    {
        if (outfitSetId == 0 || equipment.Length < EquipSlotCount) return false;

        foreach (var (setId, _, slots) in OutfitIndex())
        {
            if (setId != outfitSetId) continue;

            for (int i = 0; i < slots.Length; i++)
            {
                uint want = slots[i];
                if (want == 0) continue;

                int equipIndex = OutfitSlotToEquipSlot[i];

                if (i == 10) // ring — either hand satisfies it
                {
                    if (equipment[11].VisibleId != want && equipment[12].VisibleId != want)
                        return false;
                }
                else if (equipment[equipIndex].VisibleId != want)
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Name of the complete outfit the player is wearing, or empty. Used by the dev status
    /// readout. Walks the whole index, so it is called at most a few times a second — never
    /// per frame.
    /// </summary>
    public static string DescribeOutfit()
    {
        try
        {
            var eq = ReadEquipment();
            if (eq.Length < EquipSlotCount) return "unavailable";

            foreach (var (setId, name, _) in OutfitIndex())
            {
                if (IsWearingOutfit(setId, eq)) return name;
            }
            return "no full outfit";
        }
        catch { return "unavailable"; }
    }
}
