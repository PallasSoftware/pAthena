using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.World;

// Pinned BUILDIN_FUNC(repair)/BUILDIN_FUNC(repairall) (script.cpp:9452-9500): a broken item has
// attribute == 1 exactly (pc.cpp:1890's own `== 1` check - never "any nonzero value"), excluding a
// hatched pet egg (itemdb_ishatched_egg) from ever being treated as repairable - PetEggItemDefinition
// is never broken/repaired in this project's domain model, matching ItemGrantResolver/MobDropResolver's
// own identical PetEgg exclusion. Pure resolver only: the actual repair UI (repair list/reply packets)
// and its Feature 18 skill-cast trigger are not modeled here - see ai/iro-2026-wire.md's "Outstanding
// iRO captures" for the uncaptured client protocol. This primitive exists so Feature 18's eventual
// skill-execution call site has a durable, idempotent mutation to invoke once it resolves which
// item the player picked, without Feature 16 inventing a packet to get there.
public static class ItemRepairResolver
{
    public readonly record struct RepairableItem(uint DurableId, int ItemId, uint Amount);

    // Every currently-broken, repairable row in source order - matching pinned repairall's own
    // inventory scan order, so a caller presenting an ordered "pick which broken item" list (once
    // that protocol exists) can map a 1-based index the same way pinned repair(num) does.
    public static IReadOnlyList<RepairableItem> FindBroken(CharacterInventorySnapshot snapshot, IReadOnlyDictionary<int, ItemDefinition> items)
    {
        List<RepairableItem>? broken = null;
        foreach (var item in snapshot.Items)
        {
            if (!item.Details.IsBroken) continue;
            if (items.TryGetValue(item.ItemId, out var definition) && definition is PetEggItemDefinition) continue;
            (broken ??= []).Add(new RepairableItem(item.DurableId, item.ItemId, item.Amount));
        }
        return broken ?? (IReadOnlyList<RepairableItem>)[];
    }
}
