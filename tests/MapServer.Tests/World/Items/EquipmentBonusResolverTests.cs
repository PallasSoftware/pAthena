using Athena.Net.MapServer.World.Items;

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

    [Fact]
    public void Resolve_Bonus2Command_ThrowsUnsupported()
    {
        // bonus2's extra sub-key argument (race/size/element) is explicitly out of this slice's scope.
        var script = Lowered(new(IrKind.Block, Children: [new(IrKind.Command, Text: "bonus2", Children: [Bonus("bStr", 1), new(IrKind.Int, Number: 1)])]));
        Assert.Throws<EquipmentBonusResolver.UnsupportedEquipScriptException>(() => EquipmentBonusResolver.Resolve(script));
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
