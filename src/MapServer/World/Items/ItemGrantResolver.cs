using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World.Items;

// SHAREDPOOL draw state (s_item_group_random total_given / s_item_group_entry given). Pinned rAthena keeps it in map-server
// process memory, never persisted and reset on restart, so it is per-process ephemeral state here too - not character state.
public sealed class ItemGroupPools
{
    public static ItemGroupPools Shared { get; } = new();

    private readonly Dictionary<(uint Group, uint SubGroup), uint[]> _given = [];
    private readonly Lock _gate = new();

    // itemdb.cpp:2981-3008: a position among the not-yet-given ones; once every position was given, all counters reset.
    internal PackGroupEntry? Draw(PackItemGroup group, PackSubGroup subGroup, Func<long, long, long> random)
    {
        lock (_gate)
        {
            if (!_given.TryGetValue((group.GroupId, subGroup.SubGroup), out var given))
                _given[(group.GroupId, subGroup.SubGroup)] = given = new uint[subGroup.Entries.Count];
            var remaining = subGroup.TotalRate - (uint)given.Sum(count => (long)count);
            var position = random(1, remaining);
            long current = 1;
            for (var i = 0; i < subGroup.Entries.Count; i++)
            {
                current += subGroup.Entries[i].Rate - given[i];
                if (current <= position) continue;
                given[i]++;
                if (given.Sum(count => (long)count) >= subGroup.TotalRate) Array.Clear(given);
                return subGroup.Entries[i];
            }
            return null;
        }
    }
}

// Turns the grant effects of a planned item script (getitem, getgroupitem) into inventory grant requests BEFORE the used item is
// consumed, so the consumption and every granted item are one durable operation (as the container path already is). All random
// choices are made here, through the injected rnd_value, so a fixed source makes the result deterministic.
public static class ItemGrantResolver
{
    private const uint Card0Forge = 0x00FF, Card0Create = 0x00FE; // itemdb.hpp:33-34
    private const int MaxRefine = 20;                                // status.hpp:34 (RENEWAL)
    private const int MaxEnchantGrade = 4;                           // mmo.hpp:124-135 (RENEWAL, ENCHANTGRADE_A)

    public sealed record Result(IReadOnlyList<InventoryPlanner.GrantRequest> Grants, string? Unsupported);

    public static Result Resolve(
        IEnumerable<ItemEffect> grantEffects, IReadOnlyDictionary<int, ItemDefinition> items, IReadOnlyDictionary<uint, PackItemGroup> groups,
        ItemGroupPools pools, IInventoryRules rules, uint charId, uint nowUnixSeconds, Func<long, long, long> random)
    {
        var grants = new List<InventoryPlanner.GrantRequest>();
        foreach (var effect in grantEffects)
        {
            if (effect.Command == "getitem")
            {
                // script.cpp:7670-7738: the item must exist; amount 0 gives nothing; the item is always created identified.
                if (!items.TryGetValue((int)effect.Arguments[0], out var item)) return new([], $"getitem:unknown-item:{effect.Arguments[0]}");
                // pet_create_egg (script.cpp:7727) turns a pet egg into a pet - the pet system is not modeled yet.
                if (item is PetEggItemDefinition) return new([], $"getitem:pet-egg:{item.Id}");
                var amount = (uint)(ushort)effect.Arguments[1];
                if (amount > 0) grants.Add(new(item, amount, InventoryItemRow.New((uint)item.Id, amount, identify: 1)));
                continue;
            }

            // getgroupitem: ItemGroupDatabase::pc_get_itemgroup (itemdb.cpp:3122-3143).
            if (!groups.TryGetValue((uint)effect.Arguments[0], out var group)) return new([], $"getgroupitem:unknown-group:{effect.Arguments[0]}");
            var identify = effect.Arguments.Count > 1 && effect.Arguments[1] != 0;
            // Checked for the whole group before any draw, so a rejected use never advances a shared pool.
            foreach (var entry in group.SubGroups.SelectMany(subGroup => subGroup.Entries))
            {
                if (!items.ContainsKey((int)entry.ItemId)) return new([], $"getgroupitem:{group.Name}:unknown-item:{entry.ItemId}");
                // shortcut: random option groups (S7) are not modeled; such a group stays unusable until they are.
                if (entry.RandomOptionGroup is not null) return new([], $"getgroupitem:{group.Name}:random-option-group:{entry.RandomOptionGroup}");
            }

            foreach (var subGroup in group.SubGroups)
            {
                IEnumerable<PackGroupEntry> chosen = subGroup.Algorithm switch
                {
                    PackGroupAlgorithm.Random => [.. Optional(DrawRandom(subGroup, random))],
                    PackGroupAlgorithm.SharedPool => [.. Optional(pools.Draw(group, subGroup, random))],
                    PackGroupAlgorithm.All => subGroup.Entries,
                    _ => [], // pc_get_itemgroup has no case for DROP: such a sub group gives nothing.
                };
                foreach (var entry in chosen) AddEntry(grants, entry, items[(int)entry.ItemId], identify, rules, charId, nowUnixSeconds, random);
            }
        }
        return new(grants, null);
    }

    private static IEnumerable<PackGroupEntry> Optional(PackGroupEntry? entry) => entry is null ? [] : [entry];

    // itemdb.cpp:2963-2979 GROUP_ALGORITHM_RANDOM: each entry owns `rate` consecutive positions; a uniform position in
    // [1, total_rate] picks the entry whose range contains it.
    private static PackGroupEntry? DrawRandom(PackSubGroup subGroup, Func<long, long, long> random)
    {
        var position = random(1, subGroup.TotalRate);
        long current = 1;
        foreach (var entry in subGroup.Entries)
        {
            current += entry.Rate;
            if (current > position) return entry;
        }
        return null;
    }

    // ItemGroupDatabase::pc_get_itemgroup_sub (itemdb.cpp:3046-3113).
    private static void AddEntry(List<InventoryPlanner.GrantRequest> grants, PackGroupEntry entry, ItemDefinition item, bool identify,
        IInventoryRules rules, uint charId, uint nowUnixSeconds, Func<long, long, long> random)
    {
        var isEquip = item is WeaponItemDefinition or ArmorItemDefinition or AmmoItemDefinition or ShadowGearItemDefinition; // itemdb_isequip2
        var template = InventoryItemRow.New(entry.ItemId, 1, identify || rules.IsCreatedIdentified(item) ? (byte)1 : (byte)0) with
        {
            Bound = entry.Bound,
            ExpireTime = entry.DurationMinutes == 0 ? 0 : nowUnixSeconds + entry.DurationMinutes * 60,
            UniqueId = entry.UniqueId ? InventoryItemRow.GenerateUniqueId : 0,
        };
        if (entry.Named)
            template = template with { Card0 = isEquip ? Card0Forge : Card0Create, Card2 = charId & 0xFFFF, Card3 = charId >> 16 };
        // shortcut: Announced entries (intif_broadcast_obtain_special_item) send no world broadcast - no inter-server announce
        // channel or captured packet exists yet.

        var stacked = item.Stackable && entry.Stacked;
        var units = stacked ? 1 : entry.Amount;
        for (var unit = 0u; unit < units; unit++)
        {
            var row = template;
            if (isEquip)
                row = row with
                {
                    Refine = (byte)(entry.RefineMinimum > 0 && entry.RefineMaximum > 0 ? random(entry.RefineMinimum, entry.RefineMaximum)
                        : entry.RefineMinimum > 0 ? random(entry.RefineMinimum, MaxRefine)
                        : entry.RefineMaximum > 0 ? random(1, entry.RefineMaximum) : 0),
                    EnchantGrade = (byte)(entry.GradeMinimum > 0 && entry.GradeMaximum > 0 ? random(entry.GradeMinimum, entry.GradeMaximum)
                        : entry.GradeMinimum > 0 ? random(entry.GradeMinimum, MaxEnchantGrade)
                        : entry.GradeMaximum > 0 ? random(0, entry.GradeMaximum) : 0),
                };
            var amount = stacked ? entry.Amount : 1u;
            grants.Add(new(item, amount, row with { Amount = amount }, Stacked: stacked));
        }
    }
}
