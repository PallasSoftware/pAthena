using Athena.Net.MapServer.World.Items;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

public sealed class ItemScriptLowererTests
{
    private static RathenaConstantTable Constants() => RathenaConstantTable.Load(string.Join('\n',
        "bStr\t1\tc", "bBaseAtk\t2\tc", "RC_Dragon\t9\tc", "BaseLevel\t11\tp", "true\t1\tc", ""));

    private static readonly IReadOnlySet<string> Builtins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "bonus", "bonus2", "autobonus", "percentheal", "getrefine", "callfunc", "specialeffect2", "min", "rand", "sc_start" };

    private static PackScript Lower(string source) => new ItemScriptLowerer(Constants(), Builtins).Lower(source, "test");

    [Fact]
    public void Bonus_LowersToACommandWithResolvedConstants()
    {
        var script = Lower("bonus bStr,1; bonus2 bBaseAtk,RC_Dragon,5;");
        Assert.Equal(PackScriptSupport.Lowered, script.Support);
        Assert.Equal(PackScriptUses.Bonus, script.Uses);
        var program = Assert.IsType<IrNode>(script.Program);
        Assert.Equal(2, program.Items.Count);
        var second = program.Items[1];
        Assert.Equal((IrKind.Command, "bonus2"), (second.Kind, second.Text));
        Assert.Equal(new IrNode(IrKind.Const, "RC_Dragon", 9), second.Items[1]);
        Assert.Equal(new IrNode(IrKind.Int, Number: 5), second.Items[2]);
    }

    [Fact]
    public void Conditionals_AndLocalVariablesLower()
    {
        var script = Lower(".@r = getrefine(); if (.@r >= 7 && BaseLevel > 50) { bonus bStr,.@r; } else bonus bStr,1;");
        Assert.Equal(PackScriptSupport.Lowered, script.Support);
        var statements = script.Program!.Items;
        Assert.Equal(IrKind.ExprStmt, statements[0].Kind);
        Assert.Equal(IrKind.Assign, statements[0].Items[0].Kind);
        var conditional = statements[1];
        Assert.Equal(IrKind.If, conditional.Kind);
        Assert.Equal(3, conditional.Items.Count);
        Assert.Contains(Flatten(conditional.Items[0]), node => node.Kind == IrKind.Param && node.Text == "BaseLevel" && node.Number == 11);
    }

    [Fact]
    public void AutoBonus_NestedScriptStringsAreLoweredToo()
    {
        var script = Lower("autobonus \"{ bonus bBaseAtk,30; }\",30,10000,0,\"{ specialeffect2 99; }\";");
        Assert.Equal(PackScriptSupport.Lowered, script.Support);
        Assert.True(script.Uses.HasFlag(PackScriptUses.TriggeredBonus));
        var command = script.Program!.Items[0];
        Assert.Equal(IrKind.Script, command.Items[0].Kind);
        // The nested source is "{ ... }": a Script node wrapping the program Block, whose first statement is the braced Block.
        Assert.Equal(IrKind.Command, command.Items[0].Items[0].Items[0].Items[0].Kind);
        Assert.Equal(IrKind.Script, command.Items[4].Kind);
    }

    [Fact]
    public void SignedFirstArgumentCommands_AreCommandsNotSubtractions()
    {
        var script = Lower("percentheal -100,-100;");
        Assert.Equal(PackScriptSupport.Lowered, script.Support);
        var command = script.Program!.Items[0];
        Assert.Equal((IrKind.Command, "percentheal", 2), (command.Kind, command.Text, command.Items.Count));
        Assert.Equal((IrKind.Unary, (long)IrOp.Neg), (command.Items[0].Kind, command.Items[0].Number));
    }

    [Fact]
    public void GeneralScriptConstructs_AreDeferredToFeature20_NotDropped()
    {
        var script = Lower("bonus bStr,1; callfunc(\"F_Foo\");");
        Assert.Equal(PackScriptSupport.Deferred, script.Support);
        Assert.Equal("callfunc", script.DeferredConstruct);
        Assert.Equal(ItemScriptOwner.GeneralScript, script.DeferredOwner);
        Assert.NotNull(script.Program);
    }

    [Fact]
    public void PersistentVariablesAndUnknownCommands_AreNamedBlockers()
    {
        var persistent = Lower("RouletteGold++;");
        Assert.Equal(PackScriptSupport.Deferred, persistent.Support);
        Assert.Equal("persistent-variable:RouletteGold", persistent.DeferredConstruct);

        var unknown = Lower("totallymadeup 1;");
        Assert.Equal(PackScriptSupport.Deferred, unknown.Support);
        Assert.Equal("unknown-command:totallymadeup", unknown.DeferredConstruct);
        Assert.Equal(ItemScriptOwner.Unknown, unknown.DeferredOwner);
    }

    [Fact]
    public void StatusEffectCommands_AreLoweredAndMarkedForTheirOwner()
    {
        var script = Lower("sc_start 12,60000,1;");
        Assert.Equal(PackScriptSupport.Lowered, script.Support);
        Assert.True(script.Uses.HasFlag(PackScriptUses.StatusEffect));
    }

    private static IEnumerable<IrNode> Flatten(IrNode node) => new[] { node }.Concat(node.Items.SelectMany(Flatten));
}
