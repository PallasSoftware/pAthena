using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Gameplay.Rules;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.World.Items;

// The single-value pc_bonus vocabulary (EquipmentBonusParameters + EquipmentBonusAccumulator). Every expected value is
// derived by hand from the pinned pc_bonus case quoted next to it (pc.cpp:3725-4488), not from this implementation.
public sealed class EquipmentBonusParametersTests
{
    private static IrNode Bonus(string param, long value) => new(IrKind.Command, Text: "bonus",
        Children: [new(IrKind.Const, Text: param, Number: 0), new(IrKind.Int, Number: value)]);

    private static IrNode Flag(string param) => new(IrKind.Command, Text: "bonus", Children: [new(IrKind.Const, Text: param, Number: 0)]);

    private static PackScript Script(params IrNode[] statements) =>
        new("test", PackScriptSupport.Lowered, null, ItemScriptOwner.Item, PackScriptUses.Bonus, new IrNode(IrKind.Block, Children: statements));

    private static EquipmentBonusAccumulator Run(EquipmentBonusHand hand, params PackScript[] scripts)
    {
        var accumulator = new EquipmentBonusAccumulator();
        foreach (var script in scripts) EquipmentBonusResolver.Accumulate(script, new EquipmentScriptContext(Hand: hand), accumulator);
        return accumulator;
    }

    private static EquipmentBonusAccumulator Run(params PackScript[] scripts) => Run(EquipmentBonusHand.Right, scripts);

    [Fact]
    public void Add_AccumulatesAcrossEquippedScripts()
    {
        // SP_FLEE1: status->flee += val (two items, 30 and 10).
        Assert.Equal(40, Run(Script(Bonus("bFlee", 30)), Script(Bonus("bFlee", 10))).Flat.Get("bFlee"));
    }

    [Fact]
    public void CriticalAndPerfectFlee_AreStoredInTenths()
    {
        // SP_CRITICAL: cri += val*10; SP_FLEE2: flee2 += val*10.
        var bonuses = Run(Script(Bonus("bCritical", 3), Bonus("bFlee2", 1)));
        Assert.Equal((30, 10), (bonuses.Flat.Get("bCritical"), bonuses.Flat.Get("bFlee2")));
    }

    [Fact]
    public void NegatedParameters_StoreThePinnedSign()
    {
        // SP_DELAYRATE: delayrate -= val; SP_ASPD: aspd_add -= 10*val; SP_VARCASTRATE (RENEWAL_CAST): varcastrate -= val.
        var bonuses = Run(Script(Bonus("bDelayrate", -5), Bonus("bAspd", 1), Bonus("bVariableCastrate", -10)));
        Assert.Equal((5, -10, 10), (bonuses.Flat.Get("bDelayrate"), bonuses.Flat.Get("bAspd"), bonuses.Flat.Get("bVariableCastrate")));
    }

    [Fact]
    public void MaxAndMinParameters_DoNotStack()
    {
        // SP_DOUBLE_RATE: if (double_rate < val) double_rate = val; SP_SPEED_RATE: speed_rate = min(speed_rate, -val) from 0;
        // SP_FIXCASTRATE: fixcastrate = min(fixcastrate, val) from 0, so a positive value never applies.
        var bonuses = Run(Script(Bonus("bDoubleRate", 5)), Script(Bonus("bDoubleRate", 3)),
            Script(Bonus("bSpeedRate", 25)), Script(Bonus("bSpeedRate", 10)),
            Script(Bonus("bFixedCastrate", -20)), Script(Bonus("bFixedCastrate", 30)));
        Assert.Equal((5, -25, -20), (bonuses.Flat.Get("bDoubleRate"), bonuses.Flat.Get("bSpeedRate"), bonuses.Flat.Get("bFixedCastrate")));
    }

    [Fact]
    public void SetParameters_TakeTheLastScriptsValue()
    {
        // SP_DEFELE: status->def_ele = val (Ele_Water=1, then Ele_Fire=3).
        Assert.Equal(3, Run(Script(Bonus("bDefEle", 1)), Script(Bonus("bDefEle", 3))).Flat.Get("bDefEle"));
    }

    [Fact]
    public void BitAndMaskParameters_OrTogether()
    {
        // SP_IGNORE_DEF_RACE: |= 1<<val (RC_DemiHuman=7, RC_Dragon=9); SP_UNBREAKABLE_WEAPON/ARMOR: |= EQP_WEAPON(2)/EQP_ARMOR(16).
        var bonuses = Run(Script(Bonus("bIgnoreDefRace", 7)), Script(Bonus("bIgnoreDefRace", 9)), Script(Flag("bUnbreakableWeapon"), Flag("bUnbreakableArmor")));
        Assert.Equal(((1 << 7) | (1 << 9), 0x12), (bonuses.Flat.Get("bIgnoreDefRace"), bonuses.Flat.Get("bUnbreakableWeapon") | bonuses.Flat.Get("bUnbreakableArmor")));
    }

    [Fact]
    public void FlagAndCappedParameters()
    {
        // SP_NO_KNOCKBACK: = 1; SP_NO_MAGIC_DAMAGE: val += current, cap_value(val, 0, 100).
        var bonuses = Run(Script(Flag("bNoKnockback")), Script(Bonus("bNoMagicDamage", 60)), Script(Bonus("bNoMagicDamage", 60)));
        Assert.Equal((1, 100), (bonuses.Flat.Get("bNoKnockback"), bonuses.Flat.Get("bNoMagicDamage")));
    }

    [Fact]
    public void AllStatsFamily_FoldsIntoTheParamBonus()
    {
        // SP_ALL_STATS: every param_bonus +val; SP_AGI_VIT: agi, vit; SP_AGI_DEX_STR: agi, dex, str.
        var bonuses = Run(Script(Bonus("bAllStats", 1), Bonus("bAgiVit", 2), Bonus("bAgiDexStr", 3)));
        Assert.Equal(new EquipmentParamBonus(4, 6, 3, 1, 4, 1), bonuses.Param);
    }

    [Fact]
    public void ArrowScripts_IgnoreParamsAndKeepArrowSpecificTargets()
    {
        // lr_flag == LR_FLAG_ARROW: param_bonus untouched; SP_HIT -> arrow_hit; SP_FLEE1 nothing.
        var bonuses = Run(EquipmentBonusHand.Arrow, Script(Bonus("bStr", 5), Bonus("bHit", 3), Bonus("bFlee", 4)));
        Assert.Equal(EquipmentParamBonus.None, bonuses.Param);
        Assert.Equal((3, 0, 0), (bonuses.Flat.Get("bHit", EquipmentBonusHand.Arrow), bonuses.Flat.Get("bHit"), bonuses.Flat.Get("bFlee")));
    }

    [Fact]
    public void LeftHandScripts_KeepSeparateWeaponTables_AndSkipRightOnlyCases()
    {
        // SP_ATK1 lhw.atk; SP_DOUBLE_RATE only for LR_FLAG_NONE; SP_FLEE1 shared.
        var bonuses = Run(EquipmentBonusHand.Left, Script(Bonus("bAtk", 10), Bonus("bDoubleRate", 5), Bonus("bFlee", 2)));
        Assert.Equal((10, 0, 0, 2), (bonuses.Flat.Get("bAtk", EquipmentBonusHand.Left), bonuses.Flat.Get("bAtk"), bonuses.Flat.Get("bDoubleRate"), bonuses.Flat.Get("bFlee")));
    }

    [Fact]
    public void ParameterNames_AreCaseInsensitiveLikePinnedScriptSymbols()
    {
        // Item data writes bPAtk; the constant is exported as bPatk (script.cpp search_str uses strcasecmp).
        Assert.Equal(5, Run(Script(Bonus("bPAtk", 5))).Flat.Get("bPatk"));
    }

    [Fact]
    public void NotAPcBonusCase_IsUnsupported()
    {
        // bFame is a script constant (SP_FAME) but no pc_bonus case: pinned warns "unknown bonus type".
        Assert.Throws<EquipmentBonusResolver.UnsupportedEquipScriptException>(() => Run(Script(Bonus("bFame", 1))));
    }

    // ---- real pinned items through both regions' TryResolveEquipment / CalculateDerivedStats ----

    public static TheoryData<GameplayRegion> Regions => new() { GameplayRegion.Iro, GameplayRegion.Kro };

    private static CharacterStatusSnapshot Base1Character()
    {
        var state = new CharacterGameplayState(CharacterId: 1, Version: 0, JobClass: 0, BaseLevel: 1, JobLevel: 1, BaseExperience: 0, JobExperience: 0,
            CurrentHp: 1, CurrentSp: 1, MaxHp: 1, MaxSp: 1, StatPoints: 0, SkillPoints: 0, Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);
        return new CharacterStatusSnapshot(state, new EffectiveCharacterStats(1, 1, 1, 1, 1, 1, 0, 0), DateTimeOffset.UnixEpoch);
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public void LiveBonuses_ReachDerivedStats_AndLeaveWhenUnequipped(GameplayRegion region)
    {
        var rules = TestGameplayRules.Services(region);
        // Pinned items:
        //   Cotton_Shirt 2301 (DEF 10, no script) + Poring_Card 4001 (`bonus bLuk,2; bonus bFlee2,1;`)
        //   Clip 2607 at ACC_R (`bonus bMaxSP,10;`)
        var worn = new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(1, 0, 2301, 1, 0x10, true, 0, 0, 0, ItemInstanceDetail.Empty with { Card0 = 4001 }),
            new CharacterInventoryItem(2, 1, 2607, 1, 0x08, true, 0, 0, 0),
        ]);
        Assert.True(rules.Stats.TryResolveEquipment(worn, ItemCatalog.Current.ById, out var equipped));
        Assert.True(rules.Stats.TryResolveEquipment(new CharacterInventorySnapshot([worn.Items[0] with { Equip = 0 }, worn.Items[1] with { Equip = 0 }]), ItemCatalog.Current.ById, out var bare));

        var with = rules.Stats.CalculateDerivedStats(Base1Character(), equipped);
        var without = rules.Stats.CalculateDerivedStats(Base1Character(), bare);

        // flee2: bonus 10 + luk(1+2 param) + 10 = 23 vs 1 + 10 = 11; hard DEF 10 (Cotton Shirt) vs 0.
        Assert.Equal((23, 11), (with.PerfectFlee, without.PerfectFlee));
        Assert.Equal((10, 0), (with.HardDefense, without.HardDefense));
        Assert.Equal((2, 10), (equipped.ParamBonus.Luck, equipped.ParamBonus.MaxSp));
        Assert.Empty(bare.FlatBonusesOrEmpty.Values);
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public void HolyArrow_SetsTheArrowElementOnly(GameplayRegion region)
    {
        // Holy_Arrow 1772: `bonus bAtkEle,Ele_Holy;` (Ele_Holy = 6) run under LR_FLAG_ARROW.
        var rules = TestGameplayRules.Services(region);
        var worn = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, 1772, 100, 0x8000, true, 0, 0, 0)]);

        Assert.True(rules.Stats.TryResolveEquipment(worn, ItemCatalog.Current.ById, out var equipment));
        Assert.Equal((6, 0), (equipment.FlatBonusesOrEmpty.Get("bAtkEle", EquipmentBonusHand.Arrow), equipment.FlatBonusesOrEmpty.Get("bAtkEle")));
    }
}
