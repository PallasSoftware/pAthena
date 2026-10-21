using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World.Items;

// Flat primary-stat equip bonuses: pinned BUILDIN_FUNC(bonus) (script.cpp:3710-3733, pc_bonus pc.cpp:3725-3744)
// `bonus <SP_* param>,<val>;` accumulates into sd->indexed_bonus.param_bonus[param] while the item stays equipped,
// summed into the character's effective stat at status_calc_pc_sub (status.cpp:4291-4301) alongside status/param_equip
// bonuses. bonus2-5's extra sub-key arguments (race/size/element/etc targets, card/combo territory) are explicitly out
// of this slice's scope - ONLY the plain bStr/bAgi/bVit/bInt/bDex/bLuk single-value `bonus` form is modeled. An equip
// script this resolver cannot fully account for throws rather than silently granting a partial or zero bonus - matching
// this project's own fail-clearly convention (ItemScriptEvaluator's Unsupported diagnostics).
//
// Reads ItemMetadata.Script, NOT EquipScript: pinned item_db_equip.yml's own header comment ("Script: Script to
// execute when the item is used/equipped") confirms Weapon/Armor rows carry their continuously-active bonus
// statements in the SAME Script column a Usable item's on-use effect uses - EquipScript/UnEquipScript are a
// separate, rarely-used pinned column for one-time equip/unequip triggers, not where `bonus` lines live.
public readonly record struct EquipmentParamBonus(int Strength, int Agility, int Vitality, int Intelligence, int Dexterity, int Luck)
{
    public static readonly EquipmentParamBonus None = default;

    public static EquipmentParamBonus operator +(EquipmentParamBonus a, EquipmentParamBonus b) => new(
        a.Strength + b.Strength, a.Agility + b.Agility, a.Vitality + b.Vitality,
        a.Intelligence + b.Intelligence, a.Dexterity + b.Dexterity, a.Luck + b.Luck);
}

public static class EquipmentBonusResolver
{
    public sealed class UnsupportedEquipScriptException(string reason) : Exception(reason);

    // Resolves one equipped item's EquipScript into its flat param bonus. Null (no EquipScript) is zero bonus - most
    // equip-capable items have none. refine is the equipped row's OWN persisted refine level (pinned getrefine()'s
    // current_equip_item_index semantics, script.cpp:16903-16914: with no index argument it always reads the item
    // currently being evaluated, never another equipped slot). Anything this resolver cannot account for (bonus2-5,
    // autobonus, any other command, control flow, deferred constructs, a non-constant/non-getrefine bonus value)
    // throws UnsupportedEquipScriptException naming the construct, so the caller rejects the equip rather than
    // silently applying a wrong bonus.
    public static EquipmentParamBonus Resolve(PackScript? equipScript, byte refine = 0)
    {
        if (equipScript is null) return EquipmentParamBonus.None;
        if (equipScript.Support == PackScriptSupport.Deferred)
            throw new UnsupportedEquipScriptException($"deferred:{equipScript.DeferredOwner}:{equipScript.DeferredConstruct}");
        if (equipScript.Program is null) throw new UnsupportedEquipScriptException("no-program");

        var total = EquipmentParamBonus.None;
        Statement(equipScript.Program, refine, ref total);
        return total;
    }

    private static void Statement(IrNode node, byte refine, ref EquipmentParamBonus total)
    {
        switch (node.Kind)
        {
            case IrKind.Block:
                foreach (var child in node.Items) Statement(child, refine, ref total);
                return;
            case IrKind.Command when node.Text == "bonus":
                total += Bonus(node, refine);
                return;
            case IrKind.Command:
                throw new UnsupportedEquipScriptException($"command:{node.Text}");
            default:
                throw new UnsupportedEquipScriptException($"statement:{node.Kind}");
        }
    }

    // bonus <param>,<val>; (script.cpp BUILDIN_FUNC(bonus)): the param must be a plain constant; the value may be a
    // constant/literal arithmetic expression over an Int literal and/or getrefine() - a character-dependent or
    // random value needs the general evaluator this resolver deliberately does not reuse (equip-time bonuses are
    // static per item while equipped, never rolled per use).
    private static EquipmentParamBonus Bonus(IrNode node, byte refine)
    {
        if (node.Items.Count != 2) throw new UnsupportedEquipScriptException("bonus-arguments");
        var param = node.Items[0] is { Kind: IrKind.Const } constant ? constant.Number : throw new UnsupportedEquipScriptException("bonus-param-not-constant");
        var delta = (int)Value(node.Items[1], refine);
        return param switch
        {
            IroStatusEffectPackets.SpStr => new EquipmentParamBonus(Strength: delta, 0, 0, 0, 0, 0),
            IroStatusEffectPackets.SpAgi => new EquipmentParamBonus(0, Agility: delta, 0, 0, 0, 0),
            IroStatusEffectPackets.SpVit => new EquipmentParamBonus(0, 0, Vitality: delta, 0, 0, 0),
            IroStatusEffectPackets.SpInt => new EquipmentParamBonus(0, 0, 0, Intelligence: delta, 0, 0),
            IroStatusEffectPackets.SpDex => new EquipmentParamBonus(0, 0, 0, 0, Dexterity: delta, 0),
            IroStatusEffectPackets.SpLuk => new EquipmentParamBonus(0, 0, 0, 0, 0, Luck: delta),
            _ => throw new UnsupportedEquipScriptException($"bonus-param:{param}"),
        };
    }

    // script.cpp BUILDIN_FUNC(getrefine) with no arguments: always the currently-evaluating item's own refine value.
    private static long Value(IrNode node, byte refine)
    {
        switch (node.Kind)
        {
            case IrKind.Int:
                return node.Number;
            case IrKind.Call when node.Text == "getrefine" && node.Items.Count == 0:
                return refine;
            case IrKind.Unary when (IrOp)node.Number == IrOp.Neg:
                return -Value(node.Items[0], refine);
            case IrKind.Binary when (IrOp)node.Number is IrOp.Add or IrOp.Sub or IrOp.Mul or IrOp.Div:
                var left = Value(node.Items[0], refine);
                var right = Value(node.Items[1], refine);
                if (right == 0 && (IrOp)node.Number == IrOp.Div) throw new UnsupportedEquipScriptException("bonus-value-division-by-zero");
                return (IrOp)node.Number switch { IrOp.Add => left + right, IrOp.Sub => left - right, IrOp.Mul => left * right, _ => left / right };
            default:
                throw new UnsupportedEquipScriptException("bonus-value-not-constant");
        }
    }
}
