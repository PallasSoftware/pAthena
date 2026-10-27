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

    // ---- job lineage (pinned pc_jobid2mapid/pc_mapid2jobid, generated from pc.cpp/map.hpp) ----

    private static EquipmentCharacter Job(ushort jobClass, byte sex = 1) =>
        Wearer() with { State = Wearer().State with { JobClass = jobClass }, Sex = sex };

    private static IrNode BaseJob => new(IrKind.Param, Text: "BaseJob", Number: 119);
    private static IrNode BaseClass => new(IrKind.Param, Text: "BaseClass", Number: 120);
    private static IrNode Upper => new(IrKind.Param, Text: "Upper", Number: 56);

    [Theory]
    [MemberData(nameof(Regions))]
    public void RuneKnightSeyrenCard_AppliesItsThirdJobKnightBranch_OnlyToThirdJobKnights(GameplayRegion region)
    {
        // 4679: `if (eaclass()&EAJL_THIRD && BaseJob == Job_Knight) { bonus bAspd,2; ... }` carried by Cotton_Shirt.
        var rules = TestGameplayRules.Services(region);
        int Aspd(ushort job)
        {
            Assert.True(rules.Stats.TryResolveEquipment(new([Row(1, 2301, 0x10, card0: 4679)]), Items, out var equipment, Job(job)));
            return equipment.FlatBonusesOrEmpty.Get("bAspd");
        }

        // bAspd,2 is held as pinned sd->bonus.aspd_add -= 10*val.
        Assert.Equal(-20, Aspd(4054)); // Rune Knight: MAPID 0x1101 has JOBL_THIRD, & SECONDMASK = Knight
        Assert.Equal(-20, Aspd(4060)); // Rune Knight (trans)
        Assert.Equal(0, Aspd(4008)); // Lord Knight: not third
        Assert.Equal(0, Aspd(4066)); // Royal Guard: third, but the Crusader line
    }

    [Theory]
    [InlineData(4054, 1, 7, 1)]     // Rune Knight -> Knight / Swordman
    [InlineData(4066, 1, 14, 1)]    // Royal Guard -> Crusader / Swordman
    [InlineData(4068, 1, 19, 3)]    // Minstrel (male) -> Bard / Archer
    [InlineData(4069, 0, 20, 3)]    // Wanderer (female) -> Dancer / Archer
    [InlineData(0, 1, 0, 0)]        // Novice
    public void BaseJobAndBaseClass_FollowPinnedMapIdMasksAndSex(ushort job, byte sex, int baseJob, int baseClass)
    {
        var bonuses = Run(new EquipmentScriptContext(Character: Job(job, sex)), Script(Bonus("bStr", BaseJob), Bonus("bAgi", BaseClass)));
        Assert.Equal((baseJob, baseClass), (bonuses.Param.Strength, bonuses.Param.Agility));
    }

    [Theory]
    [InlineData(7, 0)]      // Knight
    [InlineData(4008, 1)]   // Lord Knight: JOBL_UPPER
    [InlineData(4030, 2)]   // Baby Knight: JOBL_BABY
    public void Upper_ReadsTheUpperOrBabyBit(ushort job, int upper) =>
        Assert.Equal(upper, Run(new EquipmentScriptContext(Character: Job(job)), Script(Bonus("bStr", Upper))).Param.Strength);

    [Fact]
    public void Eaclass_WithAJobArgument_IsThatJobsMapId_AndAnUnknownJobIsMinusOne()
    {
        var bonuses = Run(new EquipmentScriptContext(), Script(Bonus("bMaxHP", Call("eaclass", Int(4008))), Bonus("bMaxSP", Call("eaclass", Int(13)))));
        Assert.Equal((0x100101, -1), (bonuses.Param.MaxHp, bonuses.Param.MaxSp)); // MAPID_LORD_KNIGHT; JOB_KNIGHT2 has no case
    }

    // ---- the evaluating row (current_equip_item_index) ----

    [Fact]
    public void GetEquipWeaponLvArmorLvAndItemPos_ReadTheEvaluatingRow()
    {
        var script = Script(Bonus("bStr", Call("getequipweaponlv")), Bonus("bAgi", Call("getequiparmorlv")), Bonus("bVit", Call("getitempos")));
        var knife = Run(new EquipmentScriptContext(Items: Items, Current: Row(1, 1201, 0x02)), script);
        var shirt = Run(new EquipmentScriptContext(Items: Items, Current: Row(1, 2301, 0x10)), script);

        Assert.Equal((1, 0, 0x02), (knife.Param.Strength, knife.Param.Agility, knife.Param.Vitality)); // Knife: weapon level 1
        Assert.Equal((0, 1, 0x10), (shirt.Param.Strength, shirt.Param.Agility, shirt.Param.Vitality)); // Cotton Shirt: armor level 1
        // A combo has no evaluating row: pinned errors, so it stays unsupported rather than reading 0.
        Assert.Throws<EquipmentBonusResolver.UnsupportedEquipScriptException>(() => Run(new EquipmentScriptContext(Items: Items), script));
    }

    // ---- switch and statement increments ----

    private static IrNode Case(long? value, params IrNode[] body) =>
        new(IrKind.Case, Number: value is null ? 1 : 0, Children: [value is { } v ? Int(v) : new IrNode(IrKind.Empty), .. body]);
    private static readonly IrNode Break = new(IrKind.Break);
    private static IrNode Increment(string local) => new(IrKind.ExprStmt, Children: [new IrNode(IrKind.Postfix, Number: (long)IrOp.PostInc, Children: [Local(local)])]);

    [Theory]
    [InlineData(1, 1)]   // case 1: body, break
    [InlineData(2, 5)]   // case 2 falls through into case 3 (+1 +4)
    [InlineData(3, 4)]
    [InlineData(9, 10)]  // default
    public void Switch_MatchesFallsThroughAndBreaksLikePinned(int selector, int strength)
    {
        var script = Script(new IrNode(IrKind.Switch, Children:
        [
            Int(selector),
            Case(1, Bonus("bStr", Int(1)), Break),
            Case(2, Bonus("bStr", Int(1))),
            Case(3, Bonus("bStr", Int(4)), Break),
            Case(null, Bonus("bStr", Int(10))),
        ]));
        Assert.Equal(strength, Run(new EquipmentScriptContext(), script).Param.Strength);
    }

    [Fact]
    public void PostIncrementStatement_StepsTheLocal()
    {
        // 28645: `.@val = 3; if (.@r>=12) .@val++; ...`
        var script = Script(Set(".@val", Int(3)), Increment(".@val"), Increment(".@val"), Bonus("bStr", Local(".@val")));
        Assert.Equal(5, Run(new EquipmentScriptContext(), script).Param.Strength);
    }

    [Fact]
    public void LocalArrayElements_AreIntegerLocals_AndElementZeroIsTheBareName()
    {
        // Goibne set combo: `.@r[0] = ...; .@r[1] = ...; .@sum = .@r[0]+.@r[1]...` (item_combos.yml).
        static IrNode Element(int i) => new(IrKind.Index, Text: ".@r", Number: (long)IrVarScope.Local, Children: [Int(i)]);
        static IrNode SetElement(int i, long v) => new(IrKind.ExprStmt, Children: [new IrNode(IrKind.Assign, Number: (long)IrOp.Set, Children: [Element(i), Int(v)])]);
        var script = Script(SetElement(0, 7), SetElement(1, 9), Bonus("bStr", Binary(IrOp.Add, Element(0), Element(1))), Bonus("bAgi", Local(".@r")));
        var bonuses = Run(new EquipmentScriptContext(), script);
        Assert.Equal((16, 7), (bonuses.Param.Strength, bonuses.Param.Agility));
    }
}
