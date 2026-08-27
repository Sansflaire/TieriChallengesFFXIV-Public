using System;
using System.Collections.Generic;

using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;

namespace TieriChallengesFFXIV;

/// <summary>
/// "Does the player hold item X?" answered by a dictionary lookup instead of a bag walk.
///
/// <para><b>Why this exists.</b> The obvious implementation of a carry-an-item condition reads the
/// four bag pages every tick — 140 slots, five times a second, forever, for a condition that is
/// false almost always. This class inverts that: Dalamud's <c>IGameInventory</c> raises an event
/// whenever the inventory actually changes, so the count map is rebuilt only in response to a real
/// change and every read in between is O(1).</para>
///
/// <para><b>Dirty flag, not incremental arithmetic.</b> The events could be applied as deltas
/// (+1 here, -1 there) for an O(1) update, but the six event kinds have genuinely fiddly container
/// semantics — a Moved is a no-op within the bags and a decrement when it leaves them, a Split is
/// two slots holding one stack, Merged the reverse. Getting one wrong desynchronises the map, and
/// a desynchronised map means a challenge that silently never fires, which is the worst failure
/// this plugin has. So an event only sets a flag; the next read rebuilds. That is one 140-slot
/// walk per actual inventory change, which is nothing, and it cannot drift.</para>
///
/// <para><b>Match on <see cref="GameInventoryItem.BaseItemId"/>, never <c>ItemId</c>.</b> The
/// latter returns the ENCODED form for an HQ item (offset by 1,000,000), so an HQ copy of an item
/// would never equal the plain row id an author picked out of the Item sheet. CraftQueue documents
/// the same trap.</para>
/// </summary>
internal sealed class InventoryWatcher : IDisposable
{
    /// <summary>
    /// The pages that count as "my inventory". Deliberately the four carried bags only — a
    /// challenge saying "bring a Dark Matter" means carried, not sitting in a retainer two zones
    /// away. Armoury and saddlebag can be added here if a challenge ever needs them.
    /// </summary>
    private static readonly GameInventoryType[] Watched =
    {
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    };

    /// <summary>BaseItemId → total quantity across <see cref="Watched"/>. NQ and HQ are summed.</summary>
    private readonly Dictionary<uint, int> _counts = new();

    private bool _dirty = true;
    private bool _attached;

    public void Attach()
    {
        if (_attached) return;

        try
        {
            // All six kinds, every one of them doing nothing but setting the flag. Subscribing to
            // the reinterpreted family (rather than the raw one) means a slot-to-slot move arrives
            // as a single Moved instead of a Remove+Add pair — but since every handler is the same
            // one-liner, that distinction costs nothing either way.
            Plugin.GameInventory.ItemAdded   += OnInventoryEvent;
            Plugin.GameInventory.ItemRemoved += OnInventoryEvent;
            Plugin.GameInventory.ItemChanged += OnInventoryEvent;
            Plugin.GameInventory.ItemMoved   += OnInventoryEvent;
            Plugin.GameInventory.ItemMerged  += OnInventoryEvent;
            Plugin.GameInventory.ItemSplit   += OnInventoryEvent;

            _attached = true;
        }
        catch (Exception ex)
        {
            // A failure here must not take the plugin down — it just means item conditions fall
            // back to rebuilding whenever something else invalidates the map.
            Diag.Error(ex, "[Inventory] failed to subscribe to inventory events");
        }
    }

    public void Dispose()
    {
        if (!_attached) return;

        try
        {
            Plugin.GameInventory.ItemAdded   -= OnInventoryEvent;
            Plugin.GameInventory.ItemRemoved -= OnInventoryEvent;
            Plugin.GameInventory.ItemChanged -= OnInventoryEvent;
            Plugin.GameInventory.ItemMoved   -= OnInventoryEvent;
            Plugin.GameInventory.ItemMerged  -= OnInventoryEvent;
            Plugin.GameInventory.ItemSplit   -= OnInventoryEvent;
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[Inventory] failed to unsubscribe");
        }

        _attached = false;
    }

    private void OnInventoryEvent(GameInventoryEvent type, InventoryEventArgs data) => _dirty = true;

    /// <summary>
    /// Force a rebuild on the next read. Called on login and on character switch — the events only
    /// describe CHANGES, so a fresh character starts from a map belonging to the previous one.
    /// </summary>
    public void Invalidate() => _dirty = true;

    /// <summary>
    /// How many of this item the player is carrying, NQ and HQ together.
    ///
    /// <para><b>Game thread only.</b> <c>GetInventoryItems</c> reads live game memory; calling it
    /// from a background task can tear or AV. Every caller here is on the framework tick or the
    /// draw loop, both of which are the main thread.</para>
    /// </summary>
    public int Count(uint baseItemId)
    {
        if (baseItemId == 0) return 0;

        if (_dirty) Rebuild();

        return _counts.TryGetValue(baseItemId, out var n) ? n : 0;
    }

    public bool Has(uint baseItemId, int atLeast = 1) => Count(baseItemId) >= Math.Max(1, atLeast);

    private void Rebuild()
    {
        // Cleared before the flag so a throw mid-walk leaves an empty map and a pending retry,
        // rather than a half-populated one that reads as authoritative.
        _counts.Clear();

        try
        {
            foreach (var page in Watched)
            {
                var slots = Plugin.GameInventory.GetInventoryItems(page);
                foreach (var slot in slots)
                {
                    if (slot.IsEmpty) continue;

                    uint id = slot.BaseItemId;   // NOT ItemId — that is HQ-encoded
                    if (id == 0) continue;

                    _counts[id] = _counts.TryGetValue(id, out var have)
                        ? have + slot.Quantity
                        : slot.Quantity;
                }
            }

            _dirty = false;
        }
        catch (Exception ex)
        {
            // Leave _dirty set so the next read tries again — a loading screen is a normal reason
            // for the containers not to be readable yet.
            Diag.Debug($"[Inventory] rebuild deferred: {ex.Message}");
        }
    }
}
