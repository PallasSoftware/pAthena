using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.World;

// Result of evaluating one MobDropEntry against the pinned common-drop roll (mob.cpp:3322-3351,
// the `for (entry : md->db->dropitem)` loop in mob_dead, the SAME loop that runs AFTER quest/
// script-granted drops). Item is the resolved definition (never just an id) so the caller can
// build a WorldGroundItemContent without a second lookup.
public readonly record struct MobDropOutcome(ItemDefinition Item, uint Amount, bool StealProtected);

// Generic, data-driven resolver over a killed mob's own generated MobDefinition.Drops - never
// mob-id-specific. Deliberately mirrors QuestDropResolver's own shape/simplicity (same rnd_chance-
// style /10000 roll, same pure/synchronous no-I/O contract) rather than introducing a second
// pattern for "roll a rate against a killed mob."
//
// Documented scope simplification (see mob.cpp:2818-2871 mob_getdroprate): pinned source applies
// mob-size drop-rate scaling, LUK-based bonuses, PK-mode/class/race/VIP/ITEMBOOST multipliers, and
// a drop_rate_cap ceiling before rolling. None of those systems (LUK item-drop bonuses, VIP, PK
// mode, per-class/race drop bonuses) exist in this project yet, so this resolver rolls ONLY the
// entry's own base Rate out of 10000 - exactly like QuestDropResolver's own base-rate-only roll.
// Upgrade once those systems exist; do not silently fabricate a multiplier here.
public sealed class MobDropResolver(IReadOnlyDictionary<string, ItemDefinition> itemsByAegisName, Func<double> randomSource)
{
    public MobDropResolver(IReadOnlyDictionary<string, ItemDefinition> itemsByAegisName) : this(itemsByAegisName, System.Random.Shared.NextDouble) { }

    // mob_dead's own loop: entry->nameid == 0 (never emitted by the generator - Drops is only
    // populated for a named Item) and an unresolvable item are both skipped silently in pinned
    // source (`continue`), never a hard failure - a single bad/unmapped drop entry must not prevent
    // every OTHER entry on the same mob from rolling. A PetEgg item is excluded here too: pinned
    // source special-cases it into pet_create_egg (the pet system is not modeled yet - see
    // ItemGrantResolver's own identical PetEgg exclusion for getitem).
    public IReadOnlyList<MobDropOutcome> ResolveDrops(IReadOnlyList<MobDropEntry> drops)
    {
        List<MobDropOutcome>? outcomes = null;
        foreach (var entry in drops)
        {
            if (!itemsByAegisName.TryGetValue(entry.Item, out var item) || item is PetEggItemDefinition) continue;
            // mob.cpp:3332-3333: `if (rnd() % 10000 >= drop_rate) continue;` - rnd()%10000 is a
            // uniform value in [0, 9999], so the success probability is exactly rate/10000.
            if (randomSource() * 10000 >= entry.Rate) continue;
            (outcomes ??= []).Add(new MobDropOutcome(item, Amount: 1, entry.StealProtected));
        }
        return outcomes ?? (IReadOnlyList<MobDropOutcome>)[];
    }
}
