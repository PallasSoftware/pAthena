using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.Tests.World.Items;

public sealed class EquipmentBonusResolverTests
{
    private static readonly IReadOnlyDictionary<string, long> Sp = new Dictionary<string, long>
    {
        ["bStr"] = 13, ["bAgi"] = 14, ["bVit"] = 15, ["bInt"] = 16, ["bDex"] = 17, ["bLuk"] = 18,
    };

    private static IrNode Bonus(string param, long value) => new(IrKind.Command, Text: "bonus",
        Children: [new(IrKind.Const, Text: param, Number: Sp[param]), new(IrKind.Int, Number: value)]);

    private static PackScript Lowered(IrNode program) => new("test", PackScriptSupport.Lowered, null, ItemScriptOwner.Item, PackScriptUses.Bonus, program);

    [Fact]
    public void Resolve_NullScript_ReturnsNoneBonus()
    {
        Assert.Equal(EquipmentParamBonus.None, EquipmentBonusResolver.Resolve(null));
    }

    [Fact]
    public void Resolve_SingleFlatStrBonus_ReturnsStrengthDelta()
    {
        // Pinned db/re/item_db_equip.yml Id 2601 "Ring": `bonus bStr,2;`
        var script = Lowered(new(IrKind.Block, Children: [Bonus("bStr", 2)]));
        Assert.Equal(new EquipmentParamBonus(Strength: 2, 0, 0, 0, 0, 0), EquipmentBonusResolver.Resolve(script));
    }

    [Fact]
    public void Resolve_TwoFlatBonuses_AccumulatesBoth()
    {
        // Pinned Id 2633 "Jade_Ring": `bonus bStr,2; bonus bInt,1;`
        var script = Lowered(new(IrKind.Block, Children: [Bonus("bStr", 2), Bonus("bInt", 1)]));
        Assert.Equal(new EquipmentParamBonus(Strength: 2, 0, 0, Intelligence: 1, 0, 0), EquipmentBonusResolver.Resolve(script));
    }

    [Fact]
    public void Resolve_NegativeBonus_SubtractsStat()
    {
        var script = Lowered(new(IrKind.Block, Children: [Bonus("bStr", -5)]));
        Assert.Equal(new EquipmentParamBonus(Strength: -5, 0, 0, 0, 0, 0), EquipmentBonusResolver.Resolve(script));
    }

    [Fact]
    public void Resolve_AllSixFlatStats_MapToDistinctFields()
    {
        var script = Lowered(new(IrKind.Block, Children:
        [
            Bonus("bStr", 1), Bonus("bAgi", 2), Bonus("bVit", 3), Bonus("bInt", 4), Bonus("bDex", 5), Bonus("bLuk", 6),
        ]));
        Assert.Equal(new EquipmentParamBonus(1, 2, 3, 4, 5, 6), EquipmentBonusResolver.Resolve(script));
    }

    private static IrNode GetRefine() => new(IrKind.Call, Text: "getrefine");

    [Fact]
    public void Resolve_GetRefine_ReturnsCurrentItemsOwnRefineValue()
    {
        // Pinned Id 15070 "Robe_Of_Happiness": `bonus bLuk,getrefine();`
        var script = Lowered(new(IrKind.Block, Children: [new(IrKind.Command, Text: "bonus", Children: [new(IrKind.Const, Text: "bLuk", Number: Sp["bLuk"]), GetRefine()])]));
        Assert.Equal(new EquipmentParamBonus(0, 0, 0, 0, 0, Luck: 7), EquipmentBonusResolver.Resolve(script, refine: 7));
    }

    [Fact]
    public void Resolve_GetRefineArithmetic_AppliesOperatorToRefineValue()
    {
        // Pinned Id 4358 "Seyren_Card": `bonus bStr,getrefine()-6;`
        var script = Lowered(new(IrKind.Block, Children:
        [
            new(IrKind.Command, Text: "bonus", Children:
            [
                new(IrKind.Const, Text: "bStr", Number: Sp["bStr"]),
                new(IrKind.Binary, Number: (long)IrOp.Sub, Children: [GetRefine(), new(IrKind.Int, Number: 6)]),
            ]),
        ]));
        Assert.Equal(new EquipmentParamBonus(Strength: 4, 0, 0, 0, 0, 0), EquipmentBonusResolver.Resolve(script, refine: 10));
    }

    [Fact]
    public void Resolve_GetRefineWithDefaultRefineArgument_UsesZero()
    {
        var script = Lowered(new(IrKind.Block, Children: [new(IrKind.Command, Text: "bonus", Children: [new(IrKind.Const, Text: "bStr", Number: Sp["bStr"]), GetRefine()])]));
        Assert.Equal(new EquipmentParamBonus(Strength: 0, 0, 0, 0, 0, 0), EquipmentBonusResolver.Resolve(script));
    }

    // Pinned item_randomopt_db.yml Id 3 "VAR_STRAMOUNT": `bonus bStr,getrandomoptinfo(ROA_VALUE);`
    [Fact]
    public void Resolve_GetRandomOptInfoValue_ReadsTheCurrentOptionSlotsOwnValue()
    {
        var script = Lowered(new(IrKind.Block, Children:
        [
            new(IrKind.Command, Text: "bonus", Children:
            [
                new(IrKind.Const, Text: "bStr", Number: Sp["bStr"]),
                new(IrKind.Call, Text: "getrandomoptinfo", Children: [new(IrKind.Const, Text: "ROA_VALUE", Number: 1)]),
            ]),
        ]));
        var result = EquipmentBonusResolver.Resolve(script, 0, new InventoryOption(3, 9, 0));
        Assert.Equal(new EquipmentParamBonus(Strength: 9, 0, 0, 0, 0, 0), result);
    }

    [Fact]
    public void Resolve_GetRandomOptInfo_NoOptionOccupied_YieldsZeroRegardlessOfAttribute()
    {
        var script = Lowered(new(IrKind.Block, Children:
        [
            new(IrKind.Command, Text: "bonus", Children:
            [
                new(IrKind.Const, Text: "bStr", Number: Sp["bStr"]),
                new(IrKind.Call, Text: "getrandomoptinfo", Children: [new(IrKind.Const, Text: "ROA_VALUE", Number: 1)]),
            ]),
        ]));
        // default(InventoryOption).Id == 0 - pinned script.cpp:24836's own "no option occupied" gate.
        var result = EquipmentBonusResolver.Resolve(script, 0, default);
        Assert.Equal(EquipmentParamBonus.None, result);
    }

    // Pinned item_randomopt_db.yml Id 1 "VAR_MAXHPAMOUNT": `bonus bMaxHP,getrandomoptinfo(ROA_VALUE);`
    [Fact]
    public void Resolve_MaxHpBonus_AccumulatesIntoItsOwnField()
    {
        var script = Lowered(new(IrKind.Block, Children: [new(IrKind.Command, Text: "bonus",
            Children: [new(IrKind.Const, Text: "bMaxHP", Number: 6), new(IrKind.Int, Number: 100)])]));
        Assert.Equal(new EquipmentParamBonus(0, 0, 0, 0, 0, 0, MaxHp: 100), EquipmentBonusResolver.Resolve(script));
    }

    // Pinned item_randomopt_db.yml Id 87 "RACE_TOLERACE_NOTHING": `bonus2 bSubRace,RC_Formless,getrandomoptinfo(ROA_VALUE);`
    [Fact]
    public void Resolve_Bonus2Command_AccumulatesAsExtendedBonus_NotFlatParamBonus()
    {
        var script = Lowered(new(IrKind.Block, Children:
        [
            new(IrKind.Command, Text: "bonus2", Children:
            [
                new(IrKind.Const, Text: "bSubRace", Number: 1011),
                new(IrKind.Const, Text: "RC_Formless", Number: 0),
                new(IrKind.Int, Number: 5),
            ]),
        ]));
        var flat = EquipmentBonusResolver.Resolve(script, 0, default, out var extended);
        Assert.Equal(EquipmentParamBonus.None, flat);
        Assert.Equal([new EquipmentExtendedBonus(1011, 0, null, 5)], extended);
    }

    // Pinned item_randomopt_db.yml Id 194 "RACE_WEAPON_TOLERACE_NOTHING": `bonus3 bSubRace,RC_Formless,getrandomoptinfo(ROA_VALUE),BF_WEAPON;`
    // - the VALUE is the 3rd argument (pc.cpp:5235's own `bonus3 bSubRace,r,x,bf;` naming), never the trailing one.
    [Fact]
    public void Resolve_Bonus3Command_ValueIsThirdArgument_NotLast()
    {
        var script = Lowered(new(IrKind.Block, Children:
        [
            new(IrKind.Command, Text: "bonus3", Children:
            [
                new(IrKind.Const, Text: "bSubRace", Number: 1011),
                new(IrKind.Const, Text: "RC_Formless", Number: 0),
                new(IrKind.Call, Text: "getrandomoptinfo", Children: [new(IrKind.Const, Text: "ROA_VALUE", Number: 1)]),
                new(IrKind.Const, Text: "BF_WEAPON", Number: 1),
            ]),
        ]));
        var flat = EquipmentBonusResolver.Resolve(script, 0, new InventoryOption(87, 7, 0), out var extended);
        Assert.Equal(EquipmentParamBonus.None, flat);
        Assert.Equal([new EquipmentExtendedBonus(1011, 0, 1, 7)], extended);
    }

    [Fact]
    public void Resolve_UnknownParam_ThrowsUnsupported()
    {
        var script = Lowered(new(IrKind.Block, Children: [new(IrKind.Command, Text: "bonus",
            Children: [new(IrKind.Const, Text: "bMaxHP", Number: 999), new(IrKind.Int, Number: 10)])]));
        Assert.Throws<EquipmentBonusResolver.UnsupportedEquipScriptException>(() => EquipmentBonusResolver.Resolve(script));
    }

    [Fact]
    public void Resolve_DeferredScript_ThrowsUnsupported()
    {
        var script = new PackScript("test", PackScriptSupport.Deferred, "if", ItemScriptOwner.GeneralScript, PackScriptUses.GeneralScript, null);
        Assert.Throws<EquipmentBonusResolver.UnsupportedEquipScriptException>(() => EquipmentBonusResolver.Resolve(script));
    }

    [Fact]
    public void Resolve_NonBonusCommand_ThrowsUnsupported()
    {
        var script = Lowered(new(IrKind.Block, Children: [new(IrKind.Command, Text: "skill", Children: [new(IrKind.Str, Text: "SM_BASH"), new(IrKind.Int, Number: 5)])]));
        Assert.Throws<EquipmentBonusResolver.UnsupportedEquipScriptException>(() => EquipmentBonusResolver.Resolve(script));
    }
}
