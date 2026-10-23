using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Iro;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace MapServer.Tests.World.Items;

// Reference fixtures: real pinned items run through ItemGrantResolver (script.cpp BUILDIN_FUNC(getitem)/BUILDIN_FUNC(getgroupitem)
// -> ItemGroupDatabase::pc_get_itemgroup) exactly as ItemScriptEvaluator plans them, plus synthetic PackItemGroup fixtures for
// branches no small pinned group exercises cleanly (SharedPool reset, Random, All, a rejected unresolvable entry).
public sealed class ItemGrantResolverTests
{
    private static readonly IInventoryRules Rules = new IroInventoryRules();
    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items = ItemCatalog.Current.ById;
    private static readonly IReadOnlyDictionary<uint, PackItemGroup> Groups = ItemCatalog.Current.Groups;

    private static IEnumerable<ItemEffect> GrantEffectsOf(int itemId) =>
        ItemScriptEvaluator.Plan(Items[itemId].Meta!.Script, (min, _) => min).Effects.Where(ItemScriptEvaluator.IsGrant);

    // Old_Blue_Box (603): `getgroupitem(IG_BlueBox);` - a real pinned SharedPool/Random group, run with the real rAthena random
    // draw to prove the resolver reaches a durable grant list without throwing or looping, whatever today's pinned IG_BlueBox is.
    [Fact]
    public void OldBlueBox_ResolvesGetGroupItem_ToNonEmptyGrants()
    {
        var pools = new ItemGroupPools();
        var result = ItemGrantResolver.Resolve(GrantEffectsOf(603), Items, Groups, pools, Rules, charId: 1, nowUnixSeconds: 0, Random.Shared.NextInt64);

        Assert.Null(result.Unsupported);
        Assert.NotEmpty(result.Grants);
    }

    // A synthetic getitem effect mirroring item 9539's own script (`getitem 11572,10;`): one grant, amount 10, created identified
    // (itemdb.cpp:7670-7738 - getitem always creates identified regardless of the item's own IsCreatedIdentified rule).
    [Fact]
    public void GetItem_ResolvesToOneIdentifiedGrant_WithExactAmount()
    {
        var effects = new[] { new ItemEffect("getitem", [11572, 10]) };
        var result = ItemGrantResolver.Resolve(effects, Items, Groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Null(result.Unsupported);
        var grant = Assert.Single(result.Grants);
        Assert.Equal(11572, grant.Item.Id);
        Assert.Equal(10u, grant.Amount);
        Assert.Equal(1, grant.Template!.Identify);
    }

    // script.cpp:7670: amount 0 gives nothing, but the command itself still succeeds (no Unsupported).
    [Fact]
    public void GetItem_WithZeroAmount_GivesNoGrant()
    {
        var effects = new[] { new ItemEffect("getitem", [11572, 0]) };
        var result = ItemGrantResolver.Resolve(effects, Items, Groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Null(result.Unsupported);
        Assert.Empty(result.Grants);
    }

    [Fact]
    public void GetItem_OfUnknownItemId_IsRejected()
    {
        var effects = new[] { new ItemEffect("getitem", [999_999, 1]) };
        var result = ItemGrantResolver.Resolve(effects, Items, Groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Equal("getitem:unknown-item:999999", result.Unsupported);
        Assert.Empty(result.Grants);
    }

    // pet_create_egg (script.cpp:7727): a getitem targeting a pet egg is out of scope (the pet system is not modeled).
    [Fact]
    public void GetItem_OfPetEgg_IsRejected()
    {
        var eggId = Items.Values.OfType<PetEggItemDefinition>().First().Id;
        var effects = new[] { new ItemEffect("getitem", [eggId, 1]) };
        var result = ItemGrantResolver.Resolve(effects, Items, Groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Equal($"getitem:pet-egg:{eggId}", result.Unsupported);
    }

    // itemdb.cpp:3122-3143: an unknown group id rejects outright.
    [Fact]
    public void GetGroupItem_OfUnknownGroup_IsRejected()
    {
        var effects = new[] { new ItemEffect("getgroupitem", [999_999]) };
        var result = ItemGrantResolver.Resolve(effects, Items, Groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Equal("getgroupitem:unknown-group:999999", result.Unsupported);
    }

    private static ItemDefinition KnownEquip() => Items.Values.OfType<WeaponItemDefinition>().First();
    private static ItemDefinition KnownStackable() => Items.Values.First(item => item.Stackable && item is not WeaponItemDefinition && item is not ArmorItemDefinition);

    private static PackGroupEntry Entry(uint index, uint itemId, uint rate, uint amount = 1, bool stacked = true, bool named = false,
        bool uniqueId = false, byte bound = 0, uint durationMinutes = 0, string? randomOptionGroup = null,
        uint refineMin = 0, uint refineMax = 0, byte gradeMin = 0, byte gradeMax = 0) =>
        new(index, itemId, rate, amount, durationMinutes, Announced: false, uniqueId, stacked, named, bound, randomOptionGroup, refineMin, refineMax, gradeMin, gradeMax);

    // itemdb.cpp:2963-2979 GROUP_ALGORITHM_RANDOM: a position of exactly the first entry's rate boundary still lands on that
    // entry (the range is inclusive of its own upper edge via the running `current` accumulator), never the next one.
    [Fact]
    public void Random_PicksEntry_ByPositionWithinCumulativeRange()
    {
        var stackable = KnownStackable();
        var group = new PackItemGroup(1, "TestRandom", [new PackSubGroup(0, PackGroupAlgorithm.Random, TotalRate: 10,
            [Entry(0, (uint)stackable.Id, rate: 4), Entry(1, (uint)stackable.Id + 0, rate: 6)])]);
        var groups = new Dictionary<uint, PackItemGroup> { [1] = group };
        var items = new Dictionary<int, ItemDefinition> { [stackable.Id] = stackable };

        var effects = new[] { new ItemEffect("getgroupitem", [1]) };
        var result = ItemGrantResolver.Resolve(effects, items, groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 4);

        Assert.Null(result.Unsupported);
        var grant = Assert.Single(result.Grants);
        Assert.Equal(stackable.Id, grant.Item.Id);
    }

    // itemdb.cpp:2981-3008 GROUP_ALGORITHM_SHAREDPOOL: each draw advances the shared per-process pool; once every position has
    // been given, the pool resets (Array.Clear) so the next draw can give any entry again.
    [Fact]
    public void SharedPool_DrawsDeterministically_AndResetsWhenExhausted()
    {
        var stackable = KnownStackable();
        var group = new PackItemGroup(2, "TestSharedPool", [new PackSubGroup(0, PackGroupAlgorithm.SharedPool, TotalRate: 2,
            [Entry(0, (uint)stackable.Id, rate: 1), Entry(1, (uint)stackable.Id, rate: 1)])]);
        var groups = new Dictionary<uint, PackItemGroup> { [2] = group };
        var items = new Dictionary<int, ItemDefinition> { [stackable.Id] = stackable };
        var pools = new ItemGroupPools();
        var effects = new[] { new ItemEffect("getgroupitem", [2]) };

        // Remaining starts at 2 (TotalRate - 0 given): position 1 always draws the first not-yet-given entry.
        var first = ItemGrantResolver.Resolve(effects, items, groups, pools, Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 1);
        Assert.Null(first.Unsupported);
        Assert.Single(first.Grants);

        // After one draw, remaining is 1: the same fixed position 1 now draws the OTHER entry (the pool advanced, not reset).
        var second = ItemGrantResolver.Resolve(effects, items, groups, pools, Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 1);
        Assert.Null(second.Unsupported);
        Assert.Single(second.Grants);

        // Every position now given (2/2) -> the pool cleared itself; a third draw behaves exactly like the first again.
        var third = ItemGrantResolver.Resolve(effects, items, groups, pools, Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 1);
        Assert.Null(third.Unsupported);
        Assert.Single(third.Grants);
    }

    // itemdb.cpp pc_get_itemgroup_sub, GROUP_ALGORITHM_ALL: every entry of an All sub group is granted, unconditionally.
    [Fact]
    public void All_GrantsEveryEntry()
    {
        var stackable = KnownStackable();
        var group = new PackItemGroup(3, "TestAll", [new PackSubGroup(0, PackGroupAlgorithm.All, TotalRate: 0,
            [Entry(0, (uint)stackable.Id, rate: 0, amount: 2), Entry(1, (uint)stackable.Id, rate: 0, amount: 3)])]);
        var groups = new Dictionary<uint, PackItemGroup> { [3] = group };
        var items = new Dictionary<int, ItemDefinition> { [stackable.Id] = stackable };

        var effects = new[] { new ItemEffect("getgroupitem", [3]) };
        var result = ItemGrantResolver.Resolve(effects, items, groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Null(result.Unsupported);
        Assert.Equal(2, result.Grants.Count);
        Assert.Equal([2u, 3u], result.Grants.Select(g => g.Amount));
    }

    // pc_get_itemgroup has no DROP case (itemdb.cpp): a Drop sub group gives nothing, but the command still succeeds.
    [Fact]
    public void Drop_GrantsNothing_ButStillSucceeds()
    {
        var stackable = KnownStackable();
        var group = new PackItemGroup(4, "TestDrop", [new PackSubGroup(0, PackGroupAlgorithm.Drop, TotalRate: 1, [Entry(0, (uint)stackable.Id, rate: 1)])]);
        var groups = new Dictionary<uint, PackItemGroup> { [4] = group };
        var items = new Dictionary<int, ItemDefinition> { [stackable.Id] = stackable };

        var effects = new[] { new ItemEffect("getgroupitem", [4]) };
        var result = ItemGrantResolver.Resolve(effects, items, groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Null(result.Unsupported);
        Assert.Empty(result.Grants);
    }

    // Every entry of the WHOLE group is checked for an unknown item BEFORE any sub group draws, so a later sub group's bad
    // entry never leaves an earlier sub group's SharedPool draw already advanced.
    [Fact]
    public void UnknownItemInAnySubGroup_RejectsWholeGroup_BeforeAnyDraw()
    {
        var stackable = KnownStackable();
        var pools = new ItemGroupPools();
        var group = new PackItemGroup(5, "TestUnknownItem", [
            new PackSubGroup(0, PackGroupAlgorithm.SharedPool, TotalRate: 1, [Entry(0, (uint)stackable.Id, rate: 1)]),
            new PackSubGroup(1, PackGroupAlgorithm.All, TotalRate: 0, [Entry(0, 999_999, rate: 0)]),
        ]);
        var groups = new Dictionary<uint, PackItemGroup> { [5] = group };
        var items = new Dictionary<int, ItemDefinition> { [stackable.Id] = stackable };

        var effects = new[] { new ItemEffect("getgroupitem", [5]) };
        var result = ItemGrantResolver.Resolve(effects, items, groups, pools, Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 1);

        Assert.Equal("getgroupitem:TestUnknownItem:unknown-item:999999", result.Unsupported);
        Assert.Empty(result.Grants);

        // Resolving the SAME group again after the rejection draws exactly like a first-ever draw: the earlier (aborted)
        // attempt must never have advanced the SharedPool.
        var retryGroup = new PackItemGroup(6, "TestUnknownItemRetry", [new PackSubGroup(0, PackGroupAlgorithm.SharedPool, TotalRate: 1, [Entry(0, (uint)stackable.Id, rate: 1)])]);
        var retryGroups = new Dictionary<uint, PackItemGroup> { [6] = retryGroup };
        var retryEffects = new[] { new ItemEffect("getgroupitem", [6]) };
        var retry = ItemGrantResolver.Resolve(retryEffects, items, retryGroups, pools, Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 1);
        Assert.Null(retry.Unsupported);
        Assert.Single(retry.Grants);
    }

    // shortcut: random option groups (S7) are not modeled; such an entry stays rejected until they are.
    [Fact]
    public void RandomOptionGroupEntry_IsRejected()
    {
        var equip = KnownEquip();
        var group = new PackItemGroup(7, "TestRandomOption", [new PackSubGroup(0, PackGroupAlgorithm.All, TotalRate: 0,
            [Entry(0, (uint)equip.Id, rate: 0, randomOptionGroup: "IG_Something")])]);
        var groups = new Dictionary<uint, PackItemGroup> { [7] = group };
        var items = new Dictionary<int, ItemDefinition> { [equip.Id] = equip };

        var effects = new[] { new ItemEffect("getgroupitem", [7]) };
        var result = ItemGrantResolver.Resolve(effects, items, groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Equal($"getgroupitem:TestRandomOption:random-option-group:IG_Something", result.Unsupported);
    }

    // ItemGroupDatabase::pc_get_itemgroup_sub (itemdb.cpp:3046-3113): an equip entry's refine/grade are rolled within the
    // entry's own min/max bounds, and Named marks the card slots with the obtaining character's id (Card0=Card0Forge for an
    // equip, Card2/Card3 = charId's low/high 16 bits).
    [Fact]
    public void EquipEntry_RollsRefineAndGrade_AndNamesTheCharacter_WhenRequested()
    {
        var equip = KnownEquip();
        var group = new PackItemGroup(8, "TestEquip", [new PackSubGroup(0, PackGroupAlgorithm.All, TotalRate: 0,
            [Entry(0, (uint)equip.Id, rate: 0, named: true, refineMin: 5, refineMax: 9, gradeMin: 1, gradeMax: 3)])]);
        var groups = new Dictionary<uint, PackItemGroup> { [8] = group };
        var items = new Dictionary<int, ItemDefinition> { [equip.Id] = equip };

        var effects = new[] { new ItemEffect("getgroupitem", [8]) };
        var result = ItemGrantResolver.Resolve(effects, items, groups, new ItemGroupPools(), Rules, charId: 0x0002_0001, nowUnixSeconds: 0, (min, _) => min);

        Assert.Null(result.Unsupported);
        var grant = Assert.Single(result.Grants);
        Assert.Equal((byte)5, grant.Template!.Refine);
        Assert.Equal((byte)1, grant.Template.EnchantGrade);
        Assert.Equal(0x0002u, grant.Template.Card3);
        Assert.Equal(0x0001u, grant.Template.Card2);
    }

    // A Stacked entry of a stackable item collapses into ONE row carrying the full amount; Stacked: false (or a non-stackable
    // item) instead grants `Amount` separate unit rows.
    [Fact]
    public void StackedEntry_CollapsesIntoOneRow_UnstackedEntryGrantsSeparateUnits()
    {
        var stackable = KnownStackable();
        var group = new PackItemGroup(9, "TestStack", [new PackSubGroup(0, PackGroupAlgorithm.All, TotalRate: 0,
            [Entry(0, (uint)stackable.Id, rate: 0, amount: 3, stacked: true), Entry(1, (uint)stackable.Id, rate: 0, amount: 3, stacked: false)])]);
        var groups = new Dictionary<uint, PackItemGroup> { [9] = group };
        var items = new Dictionary<int, ItemDefinition> { [stackable.Id] = stackable };

        var effects = new[] { new ItemEffect("getgroupitem", [9]) };
        var result = ItemGrantResolver.Resolve(effects, items, groups, new ItemGroupPools(), Rules, charId: 1, nowUnixSeconds: 0, (_, _) => 0);

        Assert.Null(result.Unsupported);
        Assert.Equal(4, result.Grants.Count); // one stacked row of 3, plus three unstacked unit rows
        Assert.Equal(3u, result.Grants[0].Amount);
        Assert.All(result.Grants.Skip(1), g => Assert.Equal(1u, g.Amount));
    }
}
