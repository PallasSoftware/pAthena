using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.Gameplay.Rules.Kro;

// Kro inventory and weight mechanics: pinned rAthena Renewal pc_additem / status_calc_weight / pc_updateweightstatus with the battle
// config this deployment runs (conf/battle: natural_heal_weight_rate 70, major_overweight_rate 90). It currently reproduces the pinned
// behavior exactly; Kro-specific deviations belong here, never in a shared helper, and are introduced only with verified evidence.
// Reference-verified by VF-20..VF-22 (RathenaReferenceFixtureTests).
public sealed class KroInventoryRules : IInventoryRules
{
    private const uint MaxAmount = 30_000;               // mmo.hpp MAX_AMOUNT
    private const uint MaxInventory = 200;               // mmo.hpp MAX_INVENTORY: the array pc_additem searches, however many slots are permitted
    private const int NaturalHealWeightRate = 70;        // battle_config.natural_heal_weight_rate (SC_WEIGHT50)
    private const int MajorOverweightRate = 90;          // battle_config.major_overweight_rate (SC_WEIGHT90)

    public uint MaxWeight(in MaxWeightInput input)
    {
        // status_calc_weight: job_db.get_maxWeight + STR * 300, then (CALCWT_MAXBONUS) the item bonus, skills, mount, SC_KNOWLEDGE and Mado gear in this order.
        long max = GeneratedJobWeight.MaxWeight(input.JobClass) + (long)input.Strength * 300;
        max += input.AddMaxWeightBonus;
        if (input.IncreaseCarryLevel > 0) max += 2000L * input.IncreaseCarryLevel;
        if (input.Riding) max += 10000;
        else if (input.RidingDragon) max += 5000 + 2000L * input.DragonTrainingLevel;
        if (input.KnowledgeLevel > 0) max += max * input.KnowledgeLevel / 10;
        if (input.AllIncreaseCarryLevel > 0) max += 2000L * input.AllIncreaseCarryLevel;
        if (input.MadoGear) max += 15000;
        return (uint)Math.Max(0, max);
    }

    public uint TotalWeight(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items)
    {
        ulong total = 0;
        foreach (var row in inventory.Items)
            if (items.TryGetValue(row.ItemId, out var definition) && definition.Meta is { } meta)
                total += (ulong)meta.Weight * row.Amount;
        return (uint)Math.Min(total, uint.MaxValue);
    }

    public int WeightPercent(uint weight, uint maxWeight) => (int)Math.Min((ulong)weight * 100 / Math.Max(maxWeight, 1u), int.MaxValue);

    public WeightLevel WeightLevelFor(int weightPercent) =>
        weightPercent >= MajorOverweightRate ? WeightLevel.Major : weightPercent >= NaturalHealWeightRate ? WeightLevel.Over : WeightLevel.Normal;

    public bool IsCreatedIdentified(ItemDefinition item) => item is not (WeaponItemDefinition or ArmorItemDefinition or PetArmorItemDefinition or ShadowGearItemDefinition);

    public uint FreeSlots(CharacterInventorySnapshot inventory, uint inventorySlots)
    {
        var used = inventory.Items.Where(row => row.SlotIndex < inventorySlots).Select(row => row.SlotIndex).ToHashSet();
        return inventorySlots - (uint)used.Count;
    }

    public AddItemDecision EvaluateAdd(CharacterInventorySnapshot inventory, InventoryLimits limits, uint currentWeight, in AddItemCandidate candidate)
    {
        var item = candidate.Item;
        var amount = candidate.Amount;
        if (amount == 0) return AddItemDecision.Rejected(AddItemResult.Invalid);
        if (amount > MaxAmount) return AddItemDecision.Rejected(AddItemResult.OverAmount);

        var data = item.Meta?.Data;
        var limitedStack = data is not null && data.Flags.HasFlag(PackItemFlags.StackInventory);
        var stackAmount = data?.StackAmount ?? 0;
        if (limitedStack && amount > stackAmount) return AddItemDecision.Rejected(AddItemResult.StackLimit);

        var weight = (ulong)(data?.Weight ?? 0) * amount;
        if ((ulong)currentWeight + weight > limits.MaxWeight) return AddItemDecision.Rejected(AddItemResult.Overweight);

        // An item flagged UniqueId gets a fresh id before the merge search, so it can never match an existing stack.
        var uniqueId = candidate.UniqueId;
        if (uniqueId == 0 && data is not null && data.Flags.HasFlag(PackItemFlags.UniqueId)) uniqueId = InventoryItemRow.GenerateUniqueId;

        if (item.Stackable && candidate.ExpireTime == 0)
        {
            // The first matching stack in slot order is the only candidate (pc_additem never splits across rows).
            foreach (var row in inventory.Items.OrderBy(row => row.SlotIndex))
            {
                if (row.ItemId != item.Id || row.Bound != candidate.Bound || row.Details.ExpireTime != 0 || row.Details.UniqueId != uniqueId
                    || row.Details.Card0 != candidate.Card0 || row.Details.Card1 != candidate.Card1 || row.Details.Card2 != candidate.Card2 || row.Details.Card3 != candidate.Card3)
                    continue;

                if (amount > MaxAmount - row.Amount || (limitedStack && amount > stackAmount - row.Amount)) return AddItemDecision.Rejected(AddItemResult.OverAmount);
                if (row.SlotIndex >= limits.InventorySlots) return AddItemDecision.Rejected(AddItemResult.OverAmount);   // the stack sits in a slot the character may no longer use
                return new AddItemDecision(AddItemResult.Success, row, null, (uint)weight);
            }
        }

        var occupied = inventory.Items.Select(row => row.SlotIndex).ToHashSet();
        uint slot = 0;
        while (slot < MaxInventory && occupied.Contains(slot)) slot++;
        if (slot >= MaxInventory || slot >= limits.InventorySlots) return AddItemDecision.Rejected(AddItemResult.OverItem);
        return new AddItemDecision(AddItemResult.Success, null, slot, (uint)weight);
    }
}
