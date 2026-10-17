using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Regional character stat rules: six stats with job bonuses, derived stats (incl. MATK), max HP/SP and the resource
// transition on a stat change, equipment-derived DEF/weapon, and stat-point allocation cost/validation.
public interface IStatRules
{
    PrimaryStats BasePrimaryStats(CharacterGameplayState state);
    DerivedStats CalculateDerivedStats(CharacterStatusSnapshot snapshot, EquipmentStats equipment = default);
    uint MaxHp(CharacterGameplayState state, int effectiveVitality);
    uint MaxSp(CharacterGameplayState state, int effectiveIntelligence);

    // New maxima from the state's VIT/INT; current values are only clamped down, never healed.
    CharacterGameplayState WithRecalculatedResources(CharacterGameplayState state);

    // False when an equipped item cannot be accounted for (unknown or unsupported type) instead of assuming "no effect".
    bool TryResolveEquipment(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items, out EquipmentStats equipment);

    uint StatIncreaseCost(ushort currentValue, int increaseAmount);
    StatIncreaseValidationResult ValidateStatIncrease(CharacterGameplayState gameplay, CharacterBaseStat stat, int increaseAmount);
}
