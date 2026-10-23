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
// Hand is the pinned lr_flag the script ran under (EquipmentBonusHand): pc_bonus2/3 keep separate right_weapon/left_weapon/
// arrow_* tables for most cases, so the same entry from a left-hand weapon or an arrow must stay distinguishable.
public readonly record struct EquipmentExtendedBonus(long Param, long SubKey1, long? SubKey2, int Value, EquipmentBonusHand Hand = EquipmentBonusHand.Right);

// bonus4/bonus5 (pinned pc_bonus4/pc_bonus5: bAutoSpellOnSkill, bAddEffOnSkill, bAddEff2-style autospell/status-on-hit
// entries, every one of them a Feature 17/18 dispatch). Carried in pinned argument order, uninterpreted, exactly like
// EquipmentExtendedBonus - the per-case meaning of each argument belongs to the consuming feature.
public readonly record struct EquipmentMultiBonus(long Param, IReadOnlyList<long> Arguments, EquipmentBonusHand Hand = EquipmentBonusHand.Right);

// Read-only context one equipment script runs against (pinned status_calc_pc's current_equip_item_index /
// current_equip_opt_index / lr_flag). Refine and EnchantGrade are the CARRYING equipment row's own values for its own
// script AND for the cards compounded into it (status.cpp:4100 passes the equipment's index to card scripts), 0 / -1 for
// a combo (current_equip_item_index = -1, status.cpp:4060). Option is the random-option slot being evaluated.
// EquippedAt answers pinned pc_checkequip(sd, equip_bitmask[eqi]) for the getequip* builtins (the row worn on that EQI
// slot, or null); Items resolves item ids for getiteminfo. Both are read-only: nothing a script reads here can change.
public readonly record struct EquipmentScriptContext(byte Refine = 0, int EnchantGrade = -1, InventoryOption Option = default,
    EquipmentBonusHand Hand = EquipmentBonusHand.Right, Func<int, CharacterInventoryItem?>? EquippedAt = null,
    IReadOnlyDictionary<int, ItemDefinition>? Items = null);

// Everything every equipped script contributed, folded with each pc_bonus case's own pinned rule.
public sealed class EquipmentBonusAccumulator
{
    private readonly Dictionary<(string, EquipmentBonusHand), int> _flat = [];

    public EquipmentParamBonus Param { get; private set; }
    public List<EquipmentExtendedBonus> Extended { get; } = [];
    public List<EquipmentMultiBonus> Multi { get; } = [];
    public List<TriggeredBonusDefinition> Triggered { get; } = [];
    public EquipmentFlatBonuses Flat => new(new Dictionary<(string, EquipmentBonusHand), int>(_flat));

    internal void AddParam(EquipmentParamBonus bonus) => Param += bonus;

    internal void Apply(string name, EquipmentBonusParameter parameter, EquipmentBonusHand hand, int value)
    {
        var mode = hand switch { EquipmentBonusHand.Left => parameter.Left, EquipmentBonusHand.Arrow => parameter.Arrow, _ => EquipmentBonusHandMode.Shared };
        if (mode == EquipmentBonusHandMode.Ignored) return;
        var key = (name, mode == EquipmentBonusHandMode.Separate ? hand : EquipmentBonusHand.Right);
        var current = _flat.GetValueOrDefault(key);
        _flat[key] = parameter.Combine switch
        {
            EquipmentBonusCombine.Add => (int)Math.Clamp((long)current + (long)value * parameter.Scale, parameter.ClampMin, parameter.ClampMax),
            EquipmentBonusCombine.Max => Math.Max(current, value),
            EquipmentBonusCombine.Min => Math.Min(current, value * parameter.Scale),
            EquipmentBonusCombine.Set => value,
            EquipmentBonusCombine.OrShift => value is >= 0 and < 32 ? current | (1 << value) : throw new EquipmentBonusResolver.UnsupportedEquipScriptException($"bonus-bit-out-of-range:{name}:{value}"),
            EquipmentBonusCombine.OrValue => current | value,
            EquipmentBonusCombine.OrMask => current | (int)parameter.Mask,
            _ => 1,
        };
    }
}



// Registration-time data for pinned BUILDIN_FUNC(autobonus/autobonus2/autobonus3) (script.cpp:10128-10200+,
// identical argument shape across all three - only WHICH internal sd->autobonus* list a pinned attempt
// populates differs, a pure combat-dispatch detail outside this slice). `autobonus <bonus_script>,<rate>,
// <duration>[,<atk_type>[,<other_script>]];` registers a conditional bonus that ACTIVATES on a later combat
// event (on-hit roll of Rate out of 10000, matching AtkType's attack-type filter) for Duration milliseconds -
// Feature 16 owns the registration/typed-contract half (ownership table: "item-triggered-effect registration
// and dispatch contract"); the actual on-hit TRIGGER (pc_exeautobonus, called from the live attack-resolution
// path) is Feature 17's own combat-loop integration, not modeled here. BonusScript/OtherScript are the inner
// scripts' own ALREADY-LOWERED IrNode trees (ItemScriptLowerer recursively compiles the nested script string at
// import time, IrKind.Script - no runtime script compilation needed), kept as PackScript so
// EquipmentBonusResolver.Resolve can validate/execute them exactly like any other equip script when a future
// Feature 17 trigger fires. OtherScript is null when the pinned script omits that optional 5th argument.
public readonly record struct TriggeredBonusDefinition(PackScript BonusScript, int Rate, uint DurationMilliseconds, int AttackType, PackScript? OtherScript);

public static class EquipmentBonusResolver
{
    public sealed class UnsupportedEquipScriptException(string reason) : Exception(reason);

    // getrandomoptinfo's own pinned attribute selector (script_constants.hpp ROA_*): ROA_ID=0, ROA_VALUE=1, ROA_PARAM=2.
    private const long RoaId = 0, RoaValue = 1, RoaParam = 2;

    // Pinned BUILDIN_FUNC(bonus) (script.cpp): these parameters take a skill NAME (or id) as their first argument, for
    // every arity; skill_name2id resolves it and an unknown name skips the bonus with an error.
    private static readonly HashSet<string> SkillKeyed = new(StringComparer.OrdinalIgnoreCase)
    {
        "bAutoSpell", "bAutoSpellWhenHit", "bAutoSpellOnSkill", "bSkillAtk", "bSkillHeal", "bSkillHeal2", "bAddSkillBlow", "bCastrate",
        "bAddEffOnSkill", "bSkillUseSPrate", "bSkillCooldown", "bSkillFixedCast", "bSkillVariableCast", "bVariableCastrate",
        "bFixedCastrate", "bSkillDelay", "bSkillUseSP", "bSubSkill",
    };

    // Resolves one equipped item's Script (or one card's, combo's or random-option slot's) into its flat param bonus plus
    // any bonus2/3 extended entries. Null (no script) is zero bonus. refine is the evaluating row's own refine (pinned
    // getrefine() reads current_equip_item_index), option the random-option slot being evaluated. Anything this resolver
    // cannot account for throws UnsupportedEquipScriptException naming the construct, so the caller rejects the equip
    // rather than silently applying a wrong bonus.
    public static EquipmentParamBonus Resolve(PackScript? equipScript, byte refine = 0, InventoryOption option = default)
        => Resolve(equipScript, refine, option, out _);

    public static EquipmentParamBonus Resolve(PackScript? equipScript, byte refine, InventoryOption option, out IReadOnlyList<EquipmentExtendedBonus> extended)
        => Resolve(equipScript, refine, option, out extended, out _);

    public static EquipmentParamBonus Resolve(PackScript? equipScript, byte refine, InventoryOption option,
        out IReadOnlyList<EquipmentExtendedBonus> extended, out IReadOnlyList<TriggeredBonusDefinition> triggered)
    {
        var accumulator = new EquipmentBonusAccumulator();
        Accumulate(equipScript, new EquipmentScriptContext(refine, -1, option), accumulator);
        extended = accumulator.Extended;
        triggered = accumulator.Triggered;
        return accumulator.Param;
    }

    // Runs one script against `context`, folding every bonus into `into`. On UnsupportedEquipScriptException `into` may hold a
    // partial result; callers discard it (an equipment set with any unsupported script does not resolve at all).
    public static void Accumulate(PackScript? script, EquipmentScriptContext context, EquipmentBonusAccumulator into)
    {
        if (script is null) return;
        if (script.Support == PackScriptSupport.Deferred)
            throw new UnsupportedEquipScriptException($"deferred:{script.DeferredOwner}:{script.DeferredConstruct}");
        if (script.Program is null) throw new UnsupportedEquipScriptException("no-program");
        // Every branch must be supported, not just the one today's refine/equipment takes: otherwise an item accepted at +0
        // would stop resolving at +10. The explore pass takes every branch into a discarded accumulator.
        new Run(context, new EquipmentBonusAccumulator(), explore: true).Statement(script.Program);
        new Run(context, into, explore: false).Statement(script.Program);
    }

    // One execution of one script: pinned `.@` variables live exactly as long as this run (script_state scope).
    private sealed class Run(EquipmentScriptContext context, EquipmentBonusAccumulator into, bool explore)
    {
        private readonly Dictionary<string, long> _locals = new(StringComparer.OrdinalIgnoreCase);

        public void Statement(IrNode node)
        {
            switch (node.Kind)
            {
                case IrKind.Block:
                    foreach (var child in node.Items) Statement(child);
                    return;
                case IrKind.If:
                    // If[cond, then, else?] - pinned C_EQ..C_LOR yield 1/0; any nonzero condition takes the branch.
                {
                    var taken = Value(node.Items[0]) != 0;
                    var hasElse = node.Items.Count > 2 && node.Items[2].Kind != IrKind.Empty;
                    if (taken || explore) Statement(node.Items[1]);
                    if ((!taken || explore) && hasElse) Statement(node.Items[2]);
                    return;
                }
                case IrKind.ExprStmt when node.Items is [{ Kind: IrKind.Assign } assign]:
                    Assign(assign);
                    return;
                case IrKind.Assign:
                    Assign(node);
                    return;
                case IrKind.Empty:
                    return;
                case IrKind.Command when node.Text is "bonus" or "bonus2" or "bonus3" or "bonus4" or "bonus5":
                    Bonus(node);
                    return;
                case IrKind.Command when node.Text is "autobonus" or "autobonus2" or "autobonus3":
                    into.Triggered.Add(AutoBonus(node));
                    return;
                case IrKind.Command:
                    throw new UnsupportedEquipScriptException($"command:{node.Text}");
                default:
                    throw new UnsupportedEquipScriptException($"statement:{node.Kind}");
            }
        }

        // Pinned BUILDIN_FUNC(bonus) for every arity (script.cpp): the argument count after the parameter picks pc_bonus
        // (0 or 1 - a missing value is 0, which the flag cases ignore), pc_bonus2, pc_bonus3, pc_bonus4 or pc_bonus5.
        private void Bonus(IrNode node)
        {
            var arity = node.Text is "bonus" ? 1 : node.Text![5] - '0';
            var arguments = node.Items.Count - 1;
            if (node.Items.Count == 0 || (arity == 1 ? arguments > 1 : arguments != arity)) throw new UnsupportedEquipScriptException($"{node.Text}-arguments");
            if (node.Items[0] is not { Kind: IrKind.Const, Text: { } name } param) throw new UnsupportedEquipScriptException($"{node.Text}-param-not-constant");
            var hand = context.Hand;

            if (arity == 1)
            {
                var value = arguments == 0 ? 0 : SubKey(name, node.Items[1], node.Text);
                if (PrimaryParam(name, value) is { } primary)
                {
                    if (hand != EquipmentBonusHand.Arrow) into.AddParam(primary); // every param_bonus case: lr_flag != LR_FLAG_ARROW
                    return;
                }
                if (!EquipmentBonusParameters.TryGet(name, out var canonical, out var parameter)) throw new UnsupportedEquipScriptException($"bonus-param:{name}");
                into.Apply(canonical, parameter, hand, value);
                return;
            }

            var first = SubKey(name, node.Items[1], node.Text);
            if (arity is 2 or 3)
            {
                // bonus3's value is ALWAYS its 3rd argument (pc_bonus3 per-case naming, e.g. pc.cpp:5235 `bonus3 bSubRace,r,x,bf;`).
                var value = (int)Value(node.Items[2]);
                long? second = arity == 3 ? Value(node.Items[3]) : null;
                into.Extended.Add(new EquipmentExtendedBonus(param.Number, first, second, value, hand));
                return;
            }
            // bonus4/5: the 2nd argument may also be a skill name for bAutoSpellOnSkill (script.cpp BUILDIN_FUNC(bonus) case 4/5).
            var rest = node.Items.Skip(2).Select((argument, index) => index == 0 && name == "bAutoSpellOnSkill" ? SubKey(name, argument, node.Text) : Value(argument));
            into.Multi.Add(new EquipmentMultiBonus(param.Number, [first, .. rest], hand));
        }

        // The first argument after the parameter: a skill name for the skill-keyed parameters, a number otherwise.
        private int SubKey(string param, IrNode node, string? command)
        {
            if (node.Kind != IrKind.Str) return (int)Value(node);
            if (!SkillKeyed.Contains(param)) throw new UnsupportedEquipScriptException($"{command}-string-argument:{param}");
            try { return Generated.Skills.GeneratedSkillRegistry.GetByName(node.Text!).SkillId; }
            catch (NotSupportedException) { throw new UnsupportedEquipScriptException($"{command}-unknown-skill:{node.Text}"); }
        }

        // autobonus/autobonus2/autobonus3 <bonus_script>,<rate>,<duration>[,<atk_type>[,<other_script>]]; (script.cpp
        // BUILDIN_FUNC(autobonus/2/3), identical argument shape across all three - script.cpp:10128-10200+). The
        // bonus_script/other_script arguments are ALREADY a lowered nested IrKind.Script node (ItemScriptLowerer
        // recursively compiles the string at import time, never here). bonus_script's own inner Program must fully
        // resolve as a BONUS script (recursively, through this SAME resolver) for the autobonus to be considered
        // supported - fail-clearly: an autobonus whose bonus payload this resolver cannot account for rejects the
        // WHOLE equip. other_script is NOT a bonus script at all (real pinned data: `{ specialeffect2
        // EF_POTION_BERSERK; }` or empty `{}` - a pure client-visual trigger run via script_run_autobonus, pc.cpp),
        // so it is carried as a typed-but-unvalidated PackScript for Feature 17/ClientEffect's own future dispatch
        // to interpret, never run through the bonus-only vocabulary here. rate/duration/atk_type are evaluated once at
        // EQUIP time, long before any attack roll, through the same value rules as a bonus value.
        private TriggeredBonusDefinition AutoBonus(IrNode node)
        {
            if (node.Items.Count is < 3 or > 5) throw new UnsupportedEquipScriptException($"{node.Text}-arguments");
            var bonusScript = ResolveNestedBonusScript(node.Items[0]);
            var rate = (int)Value(node.Items[1]);
            var duration = (uint)Value(node.Items[2]);
            var attackType = node.Items.Count > 3 ? (int)Value(node.Items[3]) : 0;
            var otherScript = node.Items.Count > 4 ? NestedScript(node.Items[4], ItemScriptOwner.ClientEffect, PackScriptUses.ClientEffect) : null;
            return new TriggeredBonusDefinition(bonusScript, rate, duration, attackType, otherScript);
        }

        // The nested bonus-script argument must be an already-lowered IrKind.Script; it is validated here by resolving it
        // into a throw-away accumulator (TriggeredBonusDefinition.BonusScript carries the same PackScript for Feature 17 to
        // run when the trigger fires).
        private PackScript ResolveNestedBonusScript(IrNode node)
        {
            var nested = NestedScript(node, ItemScriptOwner.Item, PackScriptUses.Bonus);
            Accumulate(nested, context, new EquipmentBonusAccumulator());
            return nested;
        }

        private static PackScript NestedScript(IrNode node, ItemScriptOwner owner, PackScriptUses uses)
        {
            if (node is not { Kind: IrKind.Script, Items: [var program] }) throw new UnsupportedEquipScriptException("autobonus-nested-script-not-lowered");
            return new PackScript(node.Text ?? string.Empty, PackScriptSupport.Lowered, null, owner, uses, program);
        }

        // `.@name = value;` and the compound forms. Only integer locals: a string local (.@name$), or any non-local target,
        // needs general script storage (Feature 20) - the importer already defers persistent variables.
        private void Assign(IrNode node)
        {
            if (node.Items is not [{ Kind: IrKind.Local, Text: { } name }, var valueNode] || name.EndsWith('$'))
                throw new UnsupportedEquipScriptException("assign-target-not-integer-local");
            var value = Value(valueNode);
            var current = _locals.GetValueOrDefault(name);
            _locals[name] = (IrOp)node.Number switch
            {
                IrOp.Set => value,
                IrOp.AddSet => current + value,
                IrOp.SubSet => current - value,
                IrOp.MulSet => current * value,
                IrOp.DivSet when value != 0 => current / value,
                IrOp.ModSet when value != 0 => current % value,
                IrOp.DivSet or IrOp.ModSet when explore => 0,
                _ => throw new UnsupportedEquipScriptException($"assign-op:{(IrOp)node.Number}"),
            };
        }

        // Deterministic expressions: literals, script constants (already resolved to their pinned numbers by the importer),
        // locals, arithmetic/comparison/logic operators, and the read-only equipment builtins below. script.cpp
        // BUILDIN_FUNC(getrefine) with no arguments reads the currently-evaluating item's refine; BUILDIN_FUNC(getrandomoptinfo)
        // the current random-option slot (script.cpp:24833-24858).
        public long Value(IrNode node)
        {
            switch (node.Kind)
            {
                case IrKind.Int:
                case IrKind.Const:
                    return node.Number;
                case IrKind.Local when node.Text is { } local && !local.EndsWith('$'):
                    return _locals.GetValueOrDefault(local); // pinned: an unset variable reads as 0
                case IrKind.Conditional:
                {
                    var condition = Value(node.Items[0]) != 0;
                    if (explore) { var a = Value(node.Items[1]); var b = Value(node.Items[2]); return condition ? a : b; }
                    return condition ? Value(node.Items[1]) : Value(node.Items[2]);
                }
                case IrKind.Unary when (IrOp)node.Number is IrOp.Not:
                    return Value(node.Items[0]) == 0 ? 1 : 0;
                case IrKind.Unary when (IrOp)node.Number is IrOp.Plus:
                    return Value(node.Items[0]);
                case IrKind.Unary when (IrOp)node.Number is IrOp.BitNot:
                    return ~Value(node.Items[0]);
                case IrKind.Binary when (IrOp)node.Number is IrOp.Eq or IrOp.Ne or IrOp.Lt or IrOp.Gt or IrOp.Le or IrOp.Ge or IrOp.LogAnd or IrOp.LogOr
                    or IrOp.Mod or IrOp.BitAnd or IrOp.BitOr or IrOp.BitXor or IrOp.Shl or IrOp.Shr:
                {
                    var a = Value(node.Items[0]);
                    var b = Value(node.Items[1]);
                    return (IrOp)node.Number switch
                    {
                        IrOp.Eq => a == b ? 1 : 0,
                        IrOp.Ne => a != b ? 1 : 0,
                        IrOp.Lt => a < b ? 1 : 0,
                        IrOp.Gt => a > b ? 1 : 0,
                        IrOp.Le => a <= b ? 1 : 0,
                        IrOp.Ge => a >= b ? 1 : 0,
                        IrOp.LogAnd => a != 0 && b != 0 ? 1 : 0,
                        IrOp.LogOr => a != 0 || b != 0 ? 1 : 0,
                        IrOp.Mod when b != 0 => a % b,
                        IrOp.Mod when explore => 0,
                        IrOp.BitAnd => a & b,
                        IrOp.BitOr => a | b,
                        IrOp.BitXor => a ^ b,
                        IrOp.Shl => a << (int)b,
                        IrOp.Shr => a >> (int)b,
                        _ => throw new UnsupportedEquipScriptException("bonus-value-modulo-by-zero"),
                    };
                }
                case IrKind.Call when node.Text is "min" or "minimum" or "max" or "maximum" && node.Items.Count > 0:
                {
                    // BUILDIN_FUNC(minmax): folds every integer argument.
                    var values = node.Items.Select(Value);
                    return node.Text!.StartsWith("min") ? values.Min() : values.Max();
                }
                case IrKind.Call when node.Text == "pow" && node.Items.Count == 2:
                    // BUILDIN_FUNC(pow): (int32)pow((double)a, (double)b).
                    return (int)Math.Clamp(Math.Pow(Value(node.Items[0]), Value(node.Items[1])), int.MinValue, int.MaxValue);
                case IrKind.Call when node.Text == "vip_status" && node.Items.Count is 1 or 2:
                    // BUILDIN_FUNC(vip_status) pushes 0 when VIP_ENABLE is undefined - the pinned default (config/core.hpp:52).
                    return 0;
                case IrKind.Call when node.Text == "getenchantgrade" && node.Items.Count is 0 or 1:
                {
                    // BUILDIN_FUNC(getenchantgrade): the evaluating item (EQI_COMPOUND_ON) or the row on an EQI slot; -1 for none.
                    if (node.Items.Count == 0) return context.EnchantGrade;
                    return EquippedRow(node.Items[0]) is { } row ? row.Details.EnchantGrade : -1;
                }
                case IrKind.Call when node.Text == "getequiprefinerycnt" && node.Items.Count == 1:
                    // BUILDIN_FUNC(getequiprefinerycnt): the refine of the row on that EQI slot, 0 when the slot is empty.
                    return EquippedRow(node.Items[0])?.Refine ?? 0;
                case IrKind.Call when node.Text == "getequipid" && node.Items.Count == 1:
                    // BUILDIN_FUNC(getequipid): the item id worn on that EQI slot, -1 when empty. (The no-argument form reads
                    // current_equip_item_index, which this context does not carry for combos; it stays unsupported.)
                    return EquippedRow(node.Items[0])?.ItemId ?? -1;
                case IrKind.Call when node.Text == "getiteminfo" && node.Items.Count == 2:
                    return ItemInfo(Value(node.Items[0]), Value(node.Items[1]));
                case IrKind.Call when node.Text == "getrefine" && node.Items.Count == 0:
                    return context.Refine;
                case IrKind.Call when node.Text == "getrandomoptinfo" && node.Items.Count == 1:
                    var option = context.Option;
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
                    return -Value(node.Items[0]);
                case IrKind.Binary when (IrOp)node.Number is IrOp.Add or IrOp.Sub or IrOp.Mul or IrOp.Div:
                    var left = Value(node.Items[0]);
                    var right = Value(node.Items[1]);
                    if (right == 0 && (IrOp)node.Number == IrOp.Div)
                        return explore ? 0 : throw new UnsupportedEquipScriptException("bonus-value-division-by-zero");
                    return (IrOp)node.Number switch { IrOp.Add => left + right, IrOp.Sub => left - right, IrOp.Mul => left * right, _ => left / right };
                case IrKind.Call:
                    throw new UnsupportedEquipScriptException($"function:{node.Text}");
                case IrKind.Param:
                    throw new UnsupportedEquipScriptException($"character-param:{node.Text}");
                default:
                    throw new UnsupportedEquipScriptException("bonus-value-not-constant");
            }
        }

        // The EQI argument of a getequip* builtin: pinned equip_index_check(num) && pc_checkequip(sd, equip_bitmask[num]).
        private CharacterInventoryItem? EquippedRow(IrNode eqiNode)
        {
            var eqi = Value(eqiNode);
            if (context.EquippedAt is null) throw new UnsupportedEquipScriptException("equipment-context-unavailable");
            return eqi is >= 0 and < 21 ? context.EquippedAt((int)eqi) : null;
        }

        // BUILDIN_FUNC(getiteminfo) for the numeric item properties equipment scripts read (ITEMINFO_* constants,
        // script_constants.hpp); an unknown item is -1. Other properties stay unsupported.
        private long ItemInfo(long itemId, long type)
        {
            if (context.Items is null) throw new UnsupportedEquipScriptException("item-context-unavailable");
            if (!context.Items.TryGetValue((int)itemId, out var item)) return -1;
            return type switch
            {
                ItemInfoLocations => item is IEquippableItemDefinition equippable ? equippable.EquipLocation : 0,
                ItemInfoWeaponLevel => item is WeaponItemDefinition weapon ? weapon.WeaponLevel : 0,
                ItemInfoArmorLevel => item is ArmorItemDefinition ? item.Meta?.ArmorLevel ?? 0 : 0,
                ItemInfoEquipLevelMin => item.Meta?.EquipLevelMin ?? 0,
                ItemInfoSlot => item.Meta?.Slots ?? 0,
                ItemInfoView or ItemInfoSubType when item is WeaponItemDefinition weaponType => (long)weaponType.WeaponType,
                ItemInfoSubType when item is AmmoItemDefinition ammo => ammo.AmmoType,
                _ => throw new UnsupportedEquipScriptException($"getiteminfo-type:{type}"),
            };
        }
    }

    // script_constants.hpp ITEMINFO_* (enum e_iteminfo, script.hpp).
    private const long ItemInfoLocations = 5, ItemInfoSlot = 10, ItemInfoView = 11, ItemInfoEquipLevelMin = 12, ItemInfoWeaponLevel = 13, ItemInfoArmorLevel = 19, ItemInfoSubType = 20;

    // The pc_bonus cases that fold into sd->indexed_bonus.param_bonus (pc.cpp:3734-3742, 4290-4312: SP_STR..SP_LUK,
    // SP_ALL_STATS, SP_AGI_VIT, SP_AGI_DEX_STR) or the HP/SP accumulators EquipmentParamBonus carries (sd->bonus.hp/sp,
    // hprate/sprate, hprecov_rate/sprecov_rate).
    private static EquipmentParamBonus? PrimaryParam(string name, int v) => PrimaryNames.GetValueOrDefault(name) switch
    {
        "bStr" => new EquipmentParamBonus(Strength: v, 0, 0, 0, 0, 0),
        "bAgi" => new EquipmentParamBonus(0, Agility: v, 0, 0, 0, 0),
        "bVit" => new EquipmentParamBonus(0, 0, Vitality: v, 0, 0, 0),
        "bInt" => new EquipmentParamBonus(0, 0, 0, Intelligence: v, 0, 0),
        "bDex" => new EquipmentParamBonus(0, 0, 0, 0, Dexterity: v, 0),
        "bLuk" => new EquipmentParamBonus(0, 0, 0, 0, 0, Luck: v),
        "bAllStats" => new EquipmentParamBonus(v, v, v, v, v, v),
        "bAgiVit" => new EquipmentParamBonus(0, v, v, 0, 0, 0),
        "bAgiDexStr" => new EquipmentParamBonus(v, v, 0, 0, v, 0),
        "bMaxHP" => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxHp: v),
        "bMaxSP" => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxSp: v),
        "bMaxHPrate" => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxHpRate: v),
        "bMaxSPrate" => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxSpRate: v),
        "bHPrecovRate" => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, HpRecovRate: v),
        "bSPrecovRate" => new EquipmentParamBonus(0, 0, 0, 0, 0, 0, SpRecovRate: v),
        _ => null,
    };

    private static readonly Dictionary<string, string> PrimaryNames = new[]
    {
        "bStr", "bAgi", "bVit", "bInt", "bDex", "bLuk", "bAllStats", "bAgiVit", "bAgiDexStr", "bMaxHP", "bMaxSP", "bMaxHPrate", "bMaxSPrate", "bHPrecovRate", "bSPrecovRate",
    }.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
}
