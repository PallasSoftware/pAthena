using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.World;

// Equipment contribution to the status calculation for the supported scope: hard DEF summed over every equipped armor row,
// the right-hand weapon, flat primary-stat bonuses from every equipped item's EquipScript (EquipmentBonusResolver), and
// weapon/armor refine ATK/MATK/DEF bonuses (GeneratedRefineBonus - see IStatRules.TryResolveEquipment's own doc comment
// for the exact pinned formula and what remains deferred: the enchant-grade multiplier, since enchantgrade_db is not yet
// generated). Plain data - how it is resolved from the inventory is a regional rule (IStatRules.TryResolveEquipment),
// which rejects an equipped item it cannot account for instead of assuming "no effect".
//
// WeaponOverrefine is GeneratedRefineBonus.OverrefineBonus/100 (pinned wd->overrefine = info->randombonus_max/100,
// status.cpp:3986) - despite the pinned YAML field's name "RandomBonus", this is a deterministic per-(weapon level,
// refine level) value, never an RNG roll at refine time. It widens the live player Renewal MATK range asymmetrically
// (matk_min++, matk_max += overrefine-1, status.cpp:6325-6327) on top of the wMatk/variance term - see
// IStatRules.CalculateDerivedStats's own doc comment for the full formula.
//
// OWNERSHIP BOUNDARY (Feature 16 vs Feature 17): WeaponRefineAtk/WeaponRefineMatk are typed, correctly-
// computed Feature 16 data, but the LIVE player-vs-monster damage formula (IroCombatRules/
// KroCombatRules.CalculatePlayerBasicAttack, via EquippedWeaponResolver) reads WeaponItemDefinition.Attack
// directly from a separately-resolved weapon with NO refine/EquipmentStats context at all - it does not
// consume WeaponAttack below yet. Feature 16 does not claim refine ATK is live in combat; wiring
// battle_calc_base_weapon_attack's watk term to read refine-adjusted ATK (battle.cpp:2440-2490) is Feature
// 17's own integration work. See EquipmentStatsRefineBoundaryTests for the regression proving this gap is
// explicit, not silently "working".
public readonly record struct EquipmentStats(
    int HardDefense, WeaponItemDefinition? Weapon, EquipmentParamBonus ParamBonus = default,
    int WeaponRefineAtk = 0, int WeaponRefineMatk = 0, int ArmorRefineDefense = 0, int WeaponOverrefine = 0,
    IReadOnlyList<EquipmentExtendedBonus>? ExtendedBonuses = null, IReadOnlyList<TriggeredBonusDefinition>? TriggeredBonuses = null,
    EquipmentFlatBonuses? FlatBonuses = null, IReadOnlyList<EquipmentMultiBonus>? MultiBonuses = null)
{
    // Every single-value pc_bonus parameter outside EquipmentParamBonus, combined with its pinned rule. Each parameter's
    // EquipmentBonusParameters entry names its consumer: LiveDerivedStats ones (bDef, bDef2, bMdef2, bHit, bFlee, bFlee2,
    // bCritical) are applied by CalculateDerivedStats; Feature17Combat/Feature18Skill ones are typed, accumulated data the
    // owning feature reads - not applied to live gameplay yet.
    public EquipmentFlatBonuses FlatBonusesOrEmpty => FlatBonuses ?? EquipmentFlatBonuses.Empty;

    // bonus4/bonus5 entries (autospell / status-on-skill families) - Feature 17/18 dispatch data, carried uninterpreted.
    public IReadOnlyList<EquipmentMultiBonus> MultiBonusesOrEmpty => MultiBonuses ?? [];

    // bonus2/bonus3 entries (element/race/size/class damage modifiers) accumulated from every equipped item's own
    // script, every equipped card and every occupied random-option slot (EquipmentBonusResolver's own `extended`
    // output) - typed, correctly-accumulated Feature 16 data, NOT yet consumed by any Feature 17 combat formula.
    // Same "computed here, wired into the live formula as a separate increment" precedent as WeaponRefineAtk
    // above. Never null in a successfully resolved EquipmentStats (TryResolveEquipment always passes a list,
    // possibly empty); null only distinguishes `default(EquipmentStats)`.
    public IReadOnlyList<EquipmentExtendedBonus> ExtendedBonusesOrEmpty => ExtendedBonuses ?? [];

    // autobonus/autobonus2/autobonus3 REGISTRATIONS from every equipped item's own script, every equipped
    // card, AND every active combo's own shared script (428 real pinned item_combos.yml entries use autobonus)
    // - typed Feature 16 data (the "item-triggered-effect registration" half of the ownership table's own
    // phrase), NOT yet dispatched by any Feature 17 on-attack trigger (pc_exeautobonus's own combat-loop call
    // site). Never null in a successfully resolved EquipmentStats; null only distinguishes `default(EquipmentStats)`.
    public IReadOnlyList<TriggeredBonusDefinition> TriggeredBonusesOrEmpty => TriggeredBonuses ?? [];

    // The CORRECT total static+refine ATK per pinned wa->atk + wa->atk2 (status.cpp:3952,3957) - ready for
    // Feature 17 to consume once CalculatePlayerBasicAttack is updated to take EquipmentStats/this value
    // instead of a bare WeaponItemDefinition. Not yet read by any live combat path (see this type's own
    // doc comment above).
    public int WeaponAttack => (Weapon?.Attack ?? 0) + WeaponRefineAtk;
}
