using Athena.Net.MapServer.Net;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World.Items;

// Flat primary-stat equip bonuses: pinned BUILDIN_FUNC(bonus) (script.cpp:3710-3733, pc_bonus pc.cpp:3725-3744)
// `bonus <SP_* param>,<val>;` accumulates into sd->indexed_bonus.param_bonus[param] while the item stays equipped,
// summed into the character's effective stat at status_calc_pc_sub (status.cpp:4291-4301) alongside status/param_equip
// bonuses. An equip script this resolver cannot fully account for throws rather than silently granting a partial or
// zero bonus - matching this project's own fail-clearly convention (ItemScriptEvaluator's Unsupported diagnostics).
//
// Reads ItemMetadata.Script, NOT EquipScript: pinned item_db_equip.yml's own header comment ("Script: Script to
// execute when the item is used/equipped") confirms Weapon/Armor rows carry their continuously-active bonus
// statements in the SAME Script column a Usable item's on-use effect uses - EquipScript/UnEquipScript are a
// separate, rarely-used pinned column for one-time equip/unequip triggers, not where `bonus` lines live.
//
// MaxHp/MaxSp/MaxHpRate/MaxSpRate/HpRecovRate/SpRecovRate are typed, correctly-accumulated Feature 16 data (pinned
// SP_MAXHP/MAXSP/MAXHPRATE/MAXSPRATE/HPRATE/SPRATE single-value `bonus` targets - used by several random-option
// entries, e.g. item_randomopt_db.yml Id 1/2/9/10/11) - NOT yet consumed by CalculateDerivedStats's own HP/SP
// formulas (status_calc_maxhp_pc/maxsp_pc, status.cpp:3511-3600), matching this type's own EquipmentStats-level
// precedent (WeaponRefineAtk: computed correctly here, wired into the live formula as a separate increment).
public readonly record struct EquipmentParamBonus(
    int Strength, int Agility, int Vitality, int Intelligence, int Dexterity, int Luck,
    int MaxHp = 0, int MaxSp = 0, int MaxHpRate = 0, int MaxSpRate = 0, int HpRecovRate = 0, int SpRecovRate = 0)
{
    public static readonly EquipmentParamBonus None = default;

    public static EquipmentParamBonus operator +(EquipmentParamBonus a, EquipmentParamBonus b) => new(
        a.Strength + b.Strength, a.Agility + b.Agility, a.Vitality + b.Vitality,
        a.Intelligence + b.Intelligence, a.Dexterity + b.Dexterity, a.Luck + b.Luck,
        a.MaxHp + b.MaxHp, a.MaxSp + b.MaxSp, a.MaxHpRate + b.MaxHpRate, a.MaxSpRate + b.MaxSpRate,
        a.HpRecovRate + b.HpRecovRate, a.SpRecovRate + b.SpRecovRate);
}

// Generic carrier for bonus2/bonus3 (pinned BUILDIN_FUNC(bonus2)/BUILDIN_FUNC(bonus3), script.cpp): `bonus2
// <param>,<subkey>,<val>;` / `bonus3 <param>,<subkey1>,<subkey2>,<val>;`. Param/SubKey1/SubKey2 are the pinned
// script constants' RESOLVED numeric values (e.g. SP_SUBELE/Ele_Neutral), kept generic and uninterpreted here -
// Feature 16 owns accumulating these (ownership table: "bonus accumulation"), but their MEANING (element/race/
// size/class damage modifiers) is a live combat-formula concern the ownership table assigns to Feature 17
// ("cardfix, elements... formula branches"). Matches this project's existing EquipmentStats precedent: typed,
// correctly-accumulated Feature 16 data, not yet consumed by any Feature 17 combat formula - see
// EquipmentStats.ExtendedBonuses's own doc comment.
public readonly record struct EquipmentExtendedBonus(long Param, long SubKey1, long? SubKey2, int Value);

public static class EquipmentBonusResolver
{
    public sealed class UnsupportedEquipScriptException(string reason) : Exception(reason);

    // Pinned SP_* constants (script_constants.hpp) for the MaxHp/MaxSp family a few random-option scripts target
    // (item_randomopt_db.yml Id 1/2/9/10/11) - the 6 primary-stat SP_* constants already live in
    // IroStatusEffectPackets (that file's own narrower packet-wire scope), these are this resolver's own.
    private const long SpMaxHp = 6, SpMaxSp = 8, SpMaxHpRate = 1004, SpMaxSpRate = 1005, SpHpRecovRate = 1016, SpSpRecovRate = 1017;

    // getrandomoptinfo's own pinned attribute selector (script_constants.hpp ROA_*): ROA_ID=0, ROA_VALUE=1, ROA_PARAM=2.
    private const long RoaId = 0, RoaValue = 1, RoaParam = 2;

    // Resolves one equipped item's EquipScript (or one occupied random-option slot's own script) into its flat
    // param bonus plus any bonus2/3 extended entries. Null (no EquipScript) is zero bonus - most equip-capable
    // items have none. refine is the equipped row's OWN persisted refine level (pinned getrefine()'s
    // current_equip_item_index semantics, script.cpp:16903-16914: with no index argument it always reads the item
    // currently being evaluated, never another equipped slot). option is the CURRENT random-option slot being
    // evaluated (pinned current_equip_opt_index, script.cpp:24836) - zero/default when resolving an item's own
    // equip script or a card's script, neither of which can reference getrandomoptinfo(). Anything this resolver
    // cannot account for (autobonus, any other command, control flow, deferred constructs, a non-constant/
    // non-getrefine/non-getrandomoptinfo bonus value) throws UnsupportedEquipScriptException naming the construct,
    // so the caller rejects the equip rather than silently applying a wrong bonus.
    public static EquipmentParamBonus Resolve(PackScript? equipScript, byte refine = 0, InventoryOption option = default)
        => Resolve(equipScript, refine, option, out _);

    public static EquipmentParamBonus Resolve(PackScript? equipScript, byte refine, InventoryOption option, out IReadOnlyList<EquipmentExtendedBonus> extended)
    {
        extended = [];
        if (equipScript is null) return EquipmentParamBonus.None;
        if (equipScript.Support == PackScriptSupport.Deferred)
            throw new UnsupportedEquipScriptException($"deferred:{equipScript.DeferredOwner}:{equipScript.DeferredConstruct}");
        if (equipScript.Program is null) throw new UnsupportedEquipScriptException("no-program");

        var total = EquipmentParamBonus.None;
        var extendedList = new List<EquipmentExtendedBonus>();
        Statement(equipScript.Program, refine, option, ref total, extendedList);
        extended = extendedList;
        return total;
    }

    private static void Statement(IrNode node, byte refine, InventoryOption option, ref EquipmentParamBonus total, List<EquipmentExtendedBonus> extended)
    {
        switch (node.Kind)
        {
            case IrKind.Block:
                foreach (var child in node.Items) Statement(child, refine, option, ref total, extended);
                return;
            case IrKind.Command when node.Text == "bonus":
                total += Bonus(node, refine, option);
                return;
            case IrKind.Command when node.Text is "bonus2" or "bonus3":
                extended.Add(Bonus23(node, refine, option));
                return;
            case IrKind.Command:
                throw new UnsupportedEquipScriptException($"command:{node.Text}");
            default:
                throw new UnsupportedEquipScriptException($"statement:{node.Kind}");
        }
    }

    // bonus <param>,<val>; (script.cpp BUILDIN_FUNC(bonus)): the param must be a plain constant; the value may be a
    // constant/literal arithmetic expression over an Int literal, getrefine() and/or getrandomoptinfo() - a
    // character-dependent or random value needs the general evaluator this resolver deliberately does not reuse
    // (equip-time bonuses are static per item while equipped, never rolled per use).
    private static EquipmentParamBonus Bonus(IrNode node, byte refine, InventoryOption option)
    {
        if (node.Items.Count != 2) throw new UnsupportedEquipScriptException("bonus-arguments");
        var param = node.Items[0] is { Kind: IrKind.Const } constant ? constant.Number : throw new UnsupportedEquipScriptException("bonus-param-not-constant");
        var delta = (int)Value(node.Items[1], refine, option);
        return param switch
        {
            IroStatusEffectPackets.SpStr => new EquipmentParamBonus(Strength: delta, 0, 0, 0, 0, 0),
            IroStatusEffectPackets.SpAgi => new EquipmentParamBonus(0, Agility: delta, 0, 0, 0, 0),
            IroStatusEffectPackets.SpVit => new EquipmentParamBonus(0, 0, Vitality: delta, 0, 0, 0),
            IroStatusEffectPackets.SpInt => new EquipmentParamBonus(0, 0, 0, Intelligence: delta, 0, 0),
            IroStatusEffectPackets.SpDex => new EquipmentParamBonus(0, 0, 0, 0, Dexterity: delta, 0),
            IroStatusEffectPackets.SpLuk => new EquipmentParamBonus(0, 0, 0, 0, 0, Luck: delta),
            SpMaxHp => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxHp: delta),
            SpMaxSp => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxSp: delta),
            SpMaxHpRate => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxHpRate: delta),
            SpMaxSpRate => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxSpRate: delta),
            SpHpRecovRate => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, HpRecovRate: delta),
            SpSpRecovRate => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, SpRecovRate: delta),
            _ => throw new UnsupportedEquipScriptException($"bonus-param:{param}"),
        };
    }

    // bonus2 <param>,<subkey>,<val>; (script.cpp BUILDIN_FUNC(bonus2)) / bonus3 <param>,<subkey1>,<val>,<subkey2>;
    // (BUILDIN_FUNC(bonus3) - pinned pc_bonus3's own per-case argument naming, e.g. pc.cpp:5235 `bonus3
    // bSubRace,r,x,bf;` = (param, subkey1=race, VALUE=x, subkey2=bf): val is ALWAYS the 3rd argument for both
    // forms, never the last - bonus3's trailing 4th argument is a second sub-classifier, not another value.
    // Every sub-key argument must be a plain constant; the value follows the same rules as plain `bonus`.
    // Uninterpreted here by design - see EquipmentExtendedBonus's own doc comment for the Feature 16/17 boundary.
    private static EquipmentExtendedBonus Bonus23(IrNode node, byte refine, InventoryOption option)
    {
        var isBonus3 = node.Text == "bonus3";
        var expectedArgs = isBonus3 ? 4 : 3;
        if (node.Items.Count != expectedArgs) throw new UnsupportedEquipScriptException($"{node.Text}-arguments");
        long Const(int index) => node.Items[index] is { Kind: IrKind.Const } c ? c.Number : throw new UnsupportedEquipScriptException($"{node.Text}-arg{index}-not-constant");
        var param = Const(0);
        var subKey1 = Const(1);
        var delta = (int)Value(node.Items[2], refine, option);
        var subKey2 = isBonus3 ? Const(3) : (long?)null;
        return new EquipmentExtendedBonus(param, subKey1, subKey2, delta);
    }

    // script.cpp BUILDIN_FUNC(getrefine) with no arguments: always the currently-evaluating item's own refine value.
    // BUILDIN_FUNC(getrandomoptinfo): reads the CURRENT random-option slot's own Id/Value/Param (script.cpp:24833-24858).
    private static long Value(IrNode node, byte refine, InventoryOption option)
    {
        switch (node.Kind)
        {
            case IrKind.Int:
                return node.Number;
            case IrKind.Call when node.Text == "getrefine" && node.Items.Count == 0:
                return refine;
            case IrKind.Call when node.Text == "getrandomoptinfo" && node.Items.Count == 1:
                if (option.Id == 0) return 0; // pinned: option.id==0 (no option) always yields 0, regardless of attribute.
                var attribute = node.Items[0] is { Kind: IrKind.Const } attr ? attr.Number : throw new UnsupportedEquipScriptException("getrandomoptinfo-arg-not-constant");
                return attribute switch
                {
                    RoaId => option.Id,
                    RoaValue => option.Value,
                    RoaParam => option.Param,
                    _ => throw new UnsupportedEquipScriptException($"getrandomoptinfo-attribute:{attribute}"),
                };
            case IrKind.Unary when (IrOp)node.Number == IrOp.Neg:
                return -Value(node.Items[0], refine, option);
            case IrKind.Binary when (IrOp)node.Number is IrOp.Add or IrOp.Sub or IrOp.Mul or IrOp.Div:
                var left = Value(node.Items[0], refine, option);
                var right = Value(node.Items[1], refine, option);
                if (right == 0 && (IrOp)node.Number == IrOp.Div) throw new UnsupportedEquipScriptException("bonus-value-division-by-zero");
                return (IrOp)node.Number switch { IrOp.Add => left + right, IrOp.Sub => left - right, IrOp.Mul => left * right, _ => left / right };
            default:
                throw new UnsupportedEquipScriptException("bonus-value-not-constant");
        }
    }
}
