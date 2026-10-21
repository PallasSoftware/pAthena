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
    // equip-capable items have none. Anything this resolver cannot account for (bonus2-5, autobonus, any other
    // command, control flow, deferred constructs) throws UnsupportedEquipScriptException naming the construct, so the
    // caller rejects the equip rather than silently applying a wrong bonus.
    public static EquipmentParamBonus Resolve(PackScript? equipScript)
    {
        if (equipScript is null) return EquipmentParamBonus.None;
        if (equipScript.Support == PackScriptSupport.Deferred)
            throw new UnsupportedEquipScriptException($"deferred:{equipScript.DeferredOwner}:{equipScript.DeferredConstruct}");
        if (equipScript.Program is null) throw new UnsupportedEquipScriptException("no-program");

        var total = EquipmentParamBonus.None;
        Statement(equipScript.Program, ref total);
        return total;
    }

    private static void Statement(IrNode node, ref EquipmentParamBonus total)
    {
        switch (node.Kind)
        {
            case IrKind.Block:
                foreach (var child in node.Items) Statement(child, ref total);
                return;
            case IrKind.Command when node.Text == "bonus":
                total += Bonus(node);
                return;
            case IrKind.Command:
                throw new UnsupportedEquipScriptException($"command:{node.Text}");
            default:
                throw new UnsupportedEquipScriptException($"statement:{node.Kind}");
        }
    }

    // bonus <param>,<val>; (script.cpp BUILDIN_FUNC(bonus)): both arguments must already be plain constants/literals -
    // a character-dependent or random value needs the general evaluator this resolver deliberately does not reuse
    // (equip-time bonuses are static per item, never rolled per use).
    private static EquipmentParamBonus Bonus(IrNode node)
    {
        if (node.Items.Count != 2) throw new UnsupportedEquipScriptException("bonus-arguments");
        var param = node.Items[0] is { Kind: IrKind.Const } constant ? constant.Number : throw new UnsupportedEquipScriptException("bonus-param-not-constant");
        var value = node.Items[1] is { Kind: IrKind.Int } literal ? literal.Number : throw new UnsupportedEquipScriptException("bonus-value-not-constant");
        var delta = (int)value;
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
}
