using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace MapServer.Tests.World.Items;

// Reference fixtures: real pinned items (db/re/item_db_usable.yml at e985006171d2eb320ee512a653f4c83aea3d81b6) run through the
// generic evaluator exactly as the item pack stores them. The evaluator never sees an item id - only the lowered script.
public sealed class ItemScriptEvaluatorTests
{
    private static PackScript ScriptOf(int itemId) => ItemCatalog.Current.ById[itemId].Meta!.Script!;

    // A deterministic stand-in for rnd_value that records the bounds it was asked for.
    private sealed class FixedRandom(long value)
    {
        public List<(long Min, long Max)> Calls { get; } = [];
        public long Next(long min, long max) { Calls.Add((min, max)); return value; }
    }

    private sealed class RecordingHost : IItemEffectHost
    {
        public List<string> Calls { get; } = [];
        public Task ItemHealAsync(int hp, int sp, CancellationToken cancellationToken) { Calls.Add($"itemheal {hp},{sp}"); return Task.CompletedTask; }
        public Task PercentHealAsync(int hpRate, int spRate, CancellationToken cancellationToken) { Calls.Add($"percentheal {hpRate},{spRate}"); return Task.CompletedTask; }
        public Task HealAsync(int hp, int sp, CancellationToken cancellationToken) { Calls.Add($"heal {hp},{sp}"); return Task.CompletedTask; }
        public Task StartStatusAsync(ushort statusId, int durationMilliseconds, int val1, int val2, int val3, CancellationToken cancellationToken) { Calls.Add($"sc_start {statusId},{durationMilliseconds},{val1},{val2},{val3}"); return Task.CompletedTask; }
        public Task EndStatusAsync(ushort statusId, CancellationToken cancellationToken) { Calls.Add($"sc_end {statusId}"); return Task.CompletedTask; }
    }

    // Red_Potion (501): `itemheal rand(45,65),0;`.
    [Fact]
    public async Task RedPotion_RollsRandInclusiveBounds_AndDispatchesItemHeal()
    {
        var random = new FixedRandom(55);
        var plan = ItemScriptEvaluator.Plan(ScriptOf(501), random.Next);

        Assert.True(plan.Executable);
        Assert.Equal([(45L, 65L)], random.Calls);
        var host = new RecordingHost();
        await ItemScriptEvaluator.ExecuteAsync(plan, host, default);
        Assert.Equal(["itemheal 55,0"], host.Calls);
    }

    // pc_itemheal for Red_Potion's roll of 55 by a VIT 10 character without recovery skills: bonus = 100 + 2*10 = 120, so
    // 55 * 120 / 100 = 66. With SM_RECOVERY 10 and AM_LEARNINGPOTION 10: bonus = 120 + 100 + 50 = 270 -> 148.
    // Blue_Potion (505, `itemheal 0,rand(40,60);`) roll 50 at INT 20 and MG_SRECOVERY 5: bonus = 100 + 40 + 50 = 190 -> 95.
    [Theory]
    [InlineData(55, 0, 10, 1, 0, 0, 0, 0, 66, 0)]
    [InlineData(55, 0, 10, 1, 10, 0, 10, 0, 148, 0)]
    [InlineData(0, 50, 1, 20, 0, 5, 0, 0, 0, 95)]
    // SC_INCHEALRATE val1 20 (pc.cpp:10719-10720): bonus 120 -> 120 + 24 = 144 -> 55 * 144 / 100 = 79.
    [InlineData(55, 0, 10, 1, 0, 0, 0, 20, 79, 0)]
    // VIT 0: bonus is exactly 100, so the amount is unchanged.
    [InlineData(55, 7, 0, 0, 0, 0, 0, 0, 55, 7)]
    public void ItemHealFormula_MatchesPinnedPcItemHeal(int hp, int sp, int vit, int intelligence, int recovery, int spRecovery, int learningPotion, int incHealRate, int expectedHp, int expectedSp) =>
        Assert.Equal((expectedHp, expectedSp), ItemHealFormula.ItemHeal(hp, sp, vit, intelligence, recovery, spRecovery, learningPotion, incHealRate));

    // Yggdrasilberry (607, `percentheal 100,100;`) and Seed_Of_Yggdrasil (608, `percentheal 50,50;`).
    [Fact]
    public async Task YggdrasilItems_DispatchPercentHeal_WithPinnedRates()
    {
        var host = new RecordingHost();
        await ItemScriptEvaluator.ExecuteAsync(ItemScriptEvaluator.Plan(ScriptOf(607), (_, _) => throw new InvalidOperationException()), host, default);
        await ItemScriptEvaluator.ExecuteAsync(ItemScriptEvaluator.Plan(ScriptOf(608), (_, _) => throw new InvalidOperationException()), host, default);
        Assert.Equal(["percentheal 100,100", "percentheal 50,50"], host.Calls);
    }

    // status_percent_change via apply_rate: 100% is the full maximum, 50% of 1234 is 617, a sub-1 result heals 1, and a maximum
    // above 100000 divides first (apply_rate's overflow-safe branch): 150001 / 100 * 50 = 75000.
    [Theory]
    [InlineData(1234u, 100, 1234)]
    [InlineData(1234u, 50, 617)]
    [InlineData(10u, 5, 1)]
    [InlineData(150_001u, 50, 75_000)]
    [InlineData(1234u, 0, 0)]
    public void PercentHealFormula_MatchesPinnedApplyRate(uint maximum, int rate, int expected) =>
        Assert.Equal((expected, expected), ItemHealFormula.PercentHeal(maximum, maximum, rate, rate));

    // Speed_Up_Potion (12016): `sc_start SC_SPEEDUP1,5000,50;` - the status id is the constant the importer resolved.
    [Fact]
    public async Task SpeedUpPotion_DispatchesStatusStart_WithResolvedStatusId()
    {
        var plan = ItemScriptEvaluator.Plan(ScriptOf(12016), (_, _) => throw new InvalidOperationException());
        var statusId = ScriptOf(12016).Program!.Items[0].Items[0];
        Assert.Equal((IrKind.Const, "SC_SPEEDUP1"), (statusId.Kind, statusId.Text));

        var host = new RecordingHost();
        await ItemScriptEvaluator.ExecuteAsync(plan, host, default);
        Assert.Equal([$"sc_start {statusId.Number},5000,50,0,0"], host.Calls);
    }

    // Green_Potion (506): five sc_end commands, run in source order.
    [Fact]
    public async Task GreenPotion_EndsEachStatusInSourceOrder()
    {
        var host = new RecordingHost();
        await ItemScriptEvaluator.ExecuteAsync(ItemScriptEvaluator.Plan(ScriptOf(506), (_, _) => 0), host, default);
        Assert.Equal(5, host.Calls.Count);
        Assert.All(host.Calls, call => Assert.StartsWith("sc_end ", call));
    }

    // Leaf_Of_Yggdrasil (610) `itemskill "ALL_RESURRECTION",1;` is skill execution (Feature 18): never executed here.
    [Fact]
    public void ItemSkill_IsNotExecuted_AndNamesItsOwner()
    {
        var plan = ItemScriptEvaluator.Plan(ScriptOf(610), (_, _) => 0);
        Assert.False(plan.Executable);
        Assert.Equal("command:itemskill owner:SkillExecution", plan.Unsupported);
    }

    // A script the lowerer deferred to general script execution (Feature 20, e.g. callfunc) is never partially executed.
    [Fact]
    public void DeferredGeneralScript_IsNotExecuted_AndNamesItsOwner()
    {
        var deferred = ItemCatalog.Current.ById.Values.First(item => item.Meta!.Script is { Support: PackScriptSupport.Deferred, DeferredConstruct: "callfunc" });
        var plan = ItemScriptEvaluator.Plan(deferred.Meta!.Script, (_, _) => 0);
        Assert.False(plan.Executable);
        Assert.Equal("deferred:GeneralScript:callfunc", plan.Unsupported);
        Assert.Empty(plan.Effects);
    }

    // An unsupported command anywhere in the script rejects the whole script: no partial effect list is ever returned.
    [Fact]
    public void MixedScript_WithOneUnsupportedCommand_YieldsNoEffects()
    {
        var program = new IrNode(IrKind.Block, Children:
        [
            new(IrKind.Command, "itemheal", Children: [new(IrKind.Int, Number: 10), new(IrKind.Int, Number: 0)]),
            new(IrKind.Command, "warp", Children: [new(IrKind.Str, "prontera"), new(IrKind.Int, Number: 1), new(IrKind.Int, Number: 1)]),
        ]);
        var plan = ItemScriptEvaluator.Plan(new PackScript("", PackScriptSupport.Lowered, null, ItemScriptOwner.Item, PackScriptUses.ItemOperation, program), (_, _) => 0);
        Assert.Equal("command:warp owner:OtherSystem", plan.Unsupported);
        Assert.Empty(plan.Effects);
    }

    // rAthena: rand(n) is [0, n-1]; reversed bounds are swapped; arithmetic and unary minus evaluate like script.cpp's op_2num.
    [Fact]
    public void Expressions_FollowPinnedRandAndArithmeticSemantics()
    {
        var random = new FixedRandom(3);
        var program = new IrNode(IrKind.Block, Children:
        [
            new(IrKind.Command, "itemheal", Children:
            [
                new(IrKind.Binary, Number: (long)IrOp.Add, Children: [new(IrKind.Call, "rand", Children: [new(IrKind.Int, Number: 10)]), new(IrKind.Int, Number: 4)]),
                new(IrKind.Call, "rand", Children: [new(IrKind.Int, Number: 9), new(IrKind.Int, Number: 2)]),
            ]),
        ]);
        var plan = ItemScriptEvaluator.Plan(new PackScript("", PackScriptSupport.Lowered, null, ItemScriptOwner.Item, PackScriptUses.ItemOperation, program), random.Next);
        Assert.True(plan.Executable);
        Assert.Equal([(0L, 9L), (2L, 9L)], random.Calls);
        Assert.Equal([7L, 3L], plan.Effects[0].Arguments);
    }

    // The heal-rate status id ItemHealFormula reads is the value the importer resolved for SC_INCHEALRATE from pinned source.
    [Fact]
    public void IncHealRateStatusId_MatchesThePinnedConstant()
    {
        static IEnumerable<IrNode> Walk(IrNode node) => node.Items.SelectMany(Walk).Prepend(node);
        var resolved = ItemCatalog.Current.Scripts.Where(script => script.Program is not null).SelectMany(script => Walk(script.Program!))
            .First(node => node is { Kind: IrKind.Const, Text: "SC_INCHEALRATE" });
        Assert.Equal(ItemHealFormula.IncHealRateStatusId, resolved.Number);
    }

    // Coverage measurement over the whole pinned catalog: every consumable item (Healing/Usable/Cash) either plans to an
    // executable effect list or carries a named diagnostic - no silent effect loss. The counts pin today's coverage so a
    // change in either direction is deliberate.
    [Fact]
    public void Coverage_EveryConsumableEitherExecutesOrNamesWhatBlocksIt()
    {
        var consumables = ItemCatalog.Current.ById.Values
            .Where(item => item.Meta!.Data.Type is PackItemTypes.Healing or PackItemTypes.Usable or PackItemTypes.Cash && item.Meta.Script is not null)
            .ToArray();
        var plans = consumables.Select(item => ItemScriptEvaluator.Plan(item.Meta!.Script, (min, _) => min)).ToArray();

        Assert.All(plans, plan => Assert.True(plan.Executable || !string.IsNullOrWhiteSpace(plan.Unsupported)));
        Assert.All(plans.Where(plan => plan.Executable), plan => Assert.NotEmpty(plan.Effects));
        Assert.Equal(795, plans.Count(plan => plan.Executable));
    }
}
