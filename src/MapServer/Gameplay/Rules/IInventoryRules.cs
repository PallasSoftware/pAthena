using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Pinned enum e_additem_result (pc.hpp): the code pc_additem returns, which is also the failure byte the client receives.
public enum AddItemResult : byte
{
    Success = 0,
    Invalid = 1,
    Overweight = 2,
    OverItem = 4,      // no free slot below the permitted inventory size
    OverAmount = 5,    // MAX_AMOUNT / Stack.Amount reached
    StackLimit = 7,    // a single add above the item's Stack.Amount
}

// The slots the character may use and the weight it may carry right now.
public readonly record struct InventoryLimits(uint InventorySlots, uint MaxWeight);

// Inputs of status_calc_weight that this server can currently supply. Fields default to "not applicable" so a caller only fills what it knows;
// the rules never guess a missing skill or mount state.
public readonly record struct MaxWeightInput(
    ushort JobClass,
    uint Strength,
    int AddMaxWeightBonus = 0,        // bAddMaxWeight (item bonus)
    int IncreaseCarryLevel = 0,       // MC_INCCARRY
    int AllIncreaseCarryLevel = 0,    // ALL_INCCARRY
    bool Riding = false,              // pc_isriding && KN_RIDING learned
    bool RidingDragon = false,
    int DragonTrainingLevel = 0,      // RK_DRAGONTRAINING
    int KnowledgeLevel = 0,           // SC_KNOWLEDGE val1
    bool MadoGear = false);

// A thing to put into the inventory. UniqueId == InventoryItemRowDefaults.GenerateUniqueId means "CharServer assigns a fresh id".
public readonly record struct AddItemCandidate(
    ItemDefinition Item,
    uint Amount,
    byte Bound = 0,
    uint ExpireTime = 0,
    ulong UniqueId = 0,
    uint Card0 = 0, uint Card1 = 0, uint Card2 = 0, uint Card3 = 0);

// The decision of one pc_additem: merge into an existing row, or open a new row in a given runtime slot.
public sealed record AddItemDecision(AddItemResult Result, CharacterInventoryItem? MergeInto, uint? NewSlot, uint AddedWeight)
{
    public bool Succeeded => Result == AddItemResult.Success;
    public static AddItemDecision Rejected(AddItemResult result) => new(result, null, null, 0);
}

public enum WeightLevel : byte { Normal = 0, Over = 1, Major = 2 }

// Item and inventory mechanics of the deployment's region (pinned pc.cpp / status.cpp / battle config). Pure functions of explicit inputs;
// CharServer persists the outcome, this decides it. Implemented separately for each region.
public interface IInventoryRules
{
    // status_calc_weight: base job weight + STR * 300 + the bonuses in MaxWeightInput.
    uint MaxWeight(in MaxWeightInput input);

    // Sum of weight * amount over the carried rows (status_calc_weight CALCWT_ITEM). Weights are item_db values (10 = 1 weight).
    uint TotalWeight(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items);

    // pc_getpercentweight.
    int WeightPercent(uint weight, uint maxWeight);

    // pc_updateweightstatus thresholds: major_overweight_rate (SC_WEIGHT90) and natural_heal_weight_rate (SC_WEIGHT50).
    WeightLevel WeightLevelFor(int weightPercent);

    // itemdb_isidentified: weapons, armor, pet armor and shadow gear are created unidentified; everything else is identified.
    bool IsCreatedIdentified(ItemDefinition item);

    // pc_inventoryblank: empty slots below the permitted size.
    uint FreeSlots(CharacterInventorySnapshot inventory, uint inventorySlots);

    // pc_additem: validates in the pinned order (amount, MAX_AMOUNT, stack limit, weight), then merges into the first matching stack in slot order
    // or opens the first free slot below the permitted size. Never mutates; the caller turns the decision into durable mutations.
    AddItemDecision EvaluateAdd(CharacterInventorySnapshot inventory, InventoryLimits limits, uint currentWeight, in AddItemCandidate candidate);
}
