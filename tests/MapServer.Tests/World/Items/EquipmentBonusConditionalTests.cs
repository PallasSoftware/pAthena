using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Gameplay.Rules;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.World.Items;

// Conditional and local-variable equipment scripts (pinned status_calc_pc running each script with its own `.@` scope,
// getrefine()/getenchantgrade()/getequiprefinerycnt() read-only context). Real pinned scripts are quoted per test.
public sealed class EquipmentBonusConditionalTests
{
    public static TheoryData<GameplayRegion> Regions => new() { GameplayRegion.Iro, GameplayRegion.Kro };

    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items = ItemCatalog.Current.ById;

    private static CharacterInventoryItem Row(uint id, int itemId, uint equip, byte refine = 0, uint card0 = 0, byte grade = 0) =>
        new(id, id - 1, itemId, 1, equip, true, refine, 0, 0, ItemInstanceDetail.Empty with { Card0 = card0, EnchantGrade = grade });

    [Theory]
    [MemberData(nameof(Regions))]
    public void CardScript_ReadsTheCarryingRowsRefine(GameplayRegion region)
    {
        // Sting_Card 4226: `bonus bDef,2; if (getrefine()>8) bonus bMdef,5;` in Cotton_Shirt 2301. Pinned status_calc_pc runs
        // card scripts with current_equip_item_index = the armor (status.cpp:4100), so the armor's +9 enables the MDEF line.
        var rules = TestGameplayRules.Services(region);
        Assert.True(rules.Stats.TryResolveEquipment(new([Row(1, 2301, 0x10, refine: 9, card0: 4226)]), Items, out var plus9));
        Assert.True(rules.Stats.TryResolveEquipment(new([Row(1, 2301, 0x10, refine: 8, card0: 4226)]), Items, out var plus8));

        Assert.Equal((2, 5), (plus9.FlatBonusesOrEmpty.Get("bDef"), plus9.FlatBonusesOrEmpty.Get("bMdef")));
        Assert.Equal((2, 0), (plus8.FlatBonusesOrEmpty.Get("bDef"), plus8.FlatBonusesOrEmpty.Get("bMdef")));
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public void Combo_ConditionOnAnotherSlotsRefine_ActivatesUpdatesAndDeactivates(GameplayRegion region)
    {
        // item_combos.yml Durga (1287, right hand) + Whikebain_Suit (15123, armor):
        // `if (getequiprefinerycnt(EQI_HAND_R) >= 7) { autobonus "{ bonus bIgnoreDefClass,Class_Normal; }",70,3000,BF_WEAPON; }`
        var rules = TestGameplayRules.Services(region);
        var durgaSlot = ((IEquippableItemDefinition)Items[1287]).EquipLocation;
        var suitSlot = ((IEquippableItemDefinition)Items[15123]).EquipLocation;
        int Registrations(params CharacterInventoryItem[] rows)
        {
            Assert.True(rules.Stats.TryResolveEquipment(new(rows), Items, out var equipment));
            return equipment.TriggeredBonusesOrEmpty.Count(t => t is { Rate: 70, DurationMilliseconds: 3000 });
        }

        Assert.Equal(1, Registrations(Row(1, 1287, durgaSlot, refine: 7), Row(2, 15123, suitSlot)));
        Assert.Equal(0, Registrations(Row(1, 1287, durgaSlot, refine: 6), Row(2, 15123, suitSlot)));   // condition false
        Assert.Equal(0, Registrations(Row(1, 1287, durgaSlot, refine: 9), Row(2, 15123, 0)));          // combo incomplete
    }

    private static EquipmentCharacter Wearer(ushort baseLevel = 99, ushort strength = 1, params CharacterLearnedSkill[] skills) =>
        new(new CharacterGameplayState(CharacterId: 1, Version: 0, JobClass: 0, BaseLevel: baseLevel, JobLevel: 1, BaseExperience: 0, JobExperience: 0,
            CurrentHp: 1, CurrentSp: 1, MaxHp: 1, MaxSp: 1, StatPoints: 0, SkillPoints: 0, Strength: strength, Agility: 1, Vitality: 1, Intelligence: 1,
            Dexterity: 1, Luck: 1), new CharacterSkillSnapshot(skills), 1);

    private static EquipmentExtendedBonus OnlyExtended(IStatRules stats, CharacterInventoryItem row, EquipmentCharacter wearer)
    {
        Assert.True(stats.TryResolveEquipment(new([row]), Items, out var equipment, wearer));
        return Assert.Single(equipment.ExtendedBonusesOrEmpty);
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public void BaseLevel_IsReadFromTheWearer(GameplayRegion region)
    {
        // P_Skeleton_Card 4446 in a Knife: `bonus2 bAddEff,Eff_Stun,(BaseLevel>=100?300:200);`.
        var stats = TestGameplayRules.Services(region).Stats;
        var knife = Row(1, 1201, 0x2, card0: 4446);
        Assert.Equal(300, OnlyExtended(stats, knife, Wearer(baseLevel: 100)).Value);
        Assert.Equal(200, OnlyExtended(stats, knife, Wearer(baseLevel: 99)).Value);
        Assert.False(stats.TryResolveEquipment(new([knife]), Items, out _)); // no wearer: never evaluated as level 0
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public void ReadParam_ReadsTheBaseStat(GameplayRegion region)
    {
        // Karakasa_Card 4286 in Cotton_Shirt: `bonus2 bAddEffWhenHit,Eff_Confusion,300+600*(readparam(bStr)>=77);` -
        // pc_readparam(SP_STR) is sd->status.str, the allocated base stat.
        var stats = TestGameplayRules.Services(region).Stats;
        var shirt = Row(1, 2301, 0x10, card0: 4286);
        Assert.Equal(900, OnlyExtended(stats, shirt, Wearer(strength: 77)).Value);
        Assert.Equal(300, OnlyExtended(stats, shirt, Wearer(strength: 76)).Value);
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public void GetSkillLv_ReadsTheLearnedLevel_InTheShieldsLeftHandContext(GameplayRegion region)
    {
        // Live_Peach_Tree_Card 4217 in Guard (shield, worn exactly at EQP_HAND_L, so its cards run as LR_FLAG_WEAPON):
        // `bonus3 bAutoSpell,"AL_HEAL",1+9*(getskilllv("AL_HEAL") == 10),20;` - AL_HEAL is skill 28.
        var stats = TestGameplayRules.Services(region).Stats;
        var guard = Row(1, 2101, 0x20, card0: 4217);
        var mastered = OnlyExtended(stats, guard, Wearer(skills: new CharacterLearnedSkill(28, 10, CharSkillFlag.Permanent)));
        var unlearned = OnlyExtended(stats, guard, Wearer());
        Assert.Equal((28L, 10, EquipmentBonusHand.Left), (mastered.SubKey1, mastered.Value, mastered.Hand));
        Assert.Equal(1, unlearned.Value);
    }

    private static IrNode Const(string name, long value = 0) => new(IrKind.Const, Text: name, Number: value);
    private static IrNode Int(long value) => new(IrKind.Int, Number: value);
    private static IrNode Local(string name) => new(IrKind.Local, Text: name);
    private static IrNode Call(string name, params IrNode[] args) => new(IrKind.Call, Text: name, Children: args);
    private static IrNode Bonus(string param, IrNode value) => new(IrKind.Command, Text: "bonus", Children: [Const(param), value]);
    private static IrNode Set(string local, IrNode value) => new(IrKind.ExprStmt, Children: [new IrNode(IrKind.Assign, Number: (long)IrOp.Set, Children: [Local(local), value])]);
    private static IrNode If(IrNode condition, IrNode then, IrNode? otherwise = null) =>
        new(IrKind.If, Children: otherwise is null ? [condition, then] : [condition, then, otherwise]);
    private static IrNode Binary(IrOp op, IrNode a, IrNode b) => new(IrKind.Binary, Number: (long)op, Children: [a, b]);
    private static PackScript Script(params IrNode[] statements) =>
        new("test", PackScriptSupport.Lowered, null, ItemScriptOwner.Item, PackScriptUses.Bonus, new IrNode(IrKind.Block, Children: statements));

    private static EquipmentBonusAccumulator Run(EquipmentScriptContext context, params PackScript[] scripts)
    {
        var accumulator = new EquipmentBonusAccumulator();
        foreach (var script in scripts) EquipmentBonusResolver.Accumulate(script, context, accumulator);
        return accumulator;
    }

    [Fact]
    public void LocalFromGetRefine_UsesIntegerDivision()
    {
        // `.@r = getrefine(); bonus bStr,.@r/2;` at +7 -> 3 (script integers truncate).
        var bonuses = Run(new EquipmentScriptContext(Refine: 7), Script(Set(".@r", Call("getrefine")), Bonus("bStr", Binary(IrOp.Div, Local(".@r"), Int(2)))));
        Assert.Equal(3, bonuses.Param.Strength);
    }

    [Fact]
    public void Locals_AreScopedToOneScript()
    {
        // A second script reading .@r it never set sees 0 (pinned: each run_script gets a fresh script_state).
        var bonuses = Run(new EquipmentScriptContext(Refine: 7),
            Script(Set(".@r", Call("getrefine")), Bonus("bStr", Local(".@r"))),
            Script(Bonus("bAgi", Local(".@r"))));
        Assert.Equal((7, 0), (bonuses.Param.Strength, bonuses.Param.Agility));
    }

    [Fact]
    public void ElseBranch_AndEnchantGradeComparison()
    {
        // `if (getenchantgrade() >= ENCHANTGRADE_C) bonus bStr,5; else bonus bAgi,5;` with ENCHANTGRADE_C = 2.
        var script = Script(If(Binary(IrOp.Ge, Call("getenchantgrade"), Const("ENCHANTGRADE_C", 2)), Bonus("bStr", Int(5)), Bonus("bAgi", Int(5))));
        Assert.Equal((5, 0), (Run(new EquipmentScriptContext(EnchantGrade: 3), script).Param.Strength, Run(new EquipmentScriptContext(EnchantGrade: 3), script).Param.Agility));
        Assert.Equal((0, 5), (Run(new EquipmentScriptContext(EnchantGrade: 1), script).Param.Strength, Run(new EquipmentScriptContext(EnchantGrade: 1), script).Param.Agility));
    }

    [Fact]
    public void UnsupportedConstructInAnUntakenBranch_StillRejectsTheScript()
    {
        // At +0 the branch is not taken, but the item must not be accepted now and fail to resolve after refining.
        var script = Script(If(Binary(IrOp.Ge, Call("getrefine"), Int(9)), new IrNode(IrKind.Command, Text: "skill", Children: [new(IrKind.Str, Text: "AL_HEAL"), Int(1)])));
        Assert.Throws<EquipmentBonusResolver.UnsupportedEquipScriptException>(() => Run(new EquipmentScriptContext(Refine: 0), script));
    }

    [Fact]
    public void EquipmentSlotQueries_ReadTheWornRow_AndDefaultsForEmptySlots()
    {
        // getequiprefinerycnt(EQI_SHOES=2): shoes row +6 -> 6; getequipid(EQI_GARMENT=3) empty -> -1.
        var shoes = Row(1, 2401, 0x40, refine: 6);
        var context = new EquipmentScriptContext(EquippedAt: eqi => eqi == 2 ? shoes : null, Items: Items);
        var bonuses = Run(context, Script(Bonus("bStr", Call("getequiprefinerycnt", Const("EQI_SHOES", 2))), Bonus("bAgi", Call("getequipid", Const("EQI_GARMENT", 3)))));
        Assert.Equal((6, -1), (bonuses.Param.Strength, bonuses.Param.Agility));
    }

    [Fact]
    public void CharacterContext_WithoutAWearer_IsNotFakedAsZero()
    {
        // BaseLevel needs the character; until it is supplied the script is unsupported, never evaluated as level 0.
        var script = Script(Bonus("bStr", new IrNode(IrKind.Param, Text: "BaseLevel", Number: 11)));
        Assert.Throws<EquipmentBonusResolver.UnsupportedEquipScriptException>(() => Run(new EquipmentScriptContext(), script));
    }
}
