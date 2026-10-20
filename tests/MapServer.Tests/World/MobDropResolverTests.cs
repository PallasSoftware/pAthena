using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.World;

public sealed class MobDropResolverTests
{
    private static readonly IReadOnlyDictionary<string, ItemDefinition> ByAegisName = ItemCatalog.Current.ByAegisName;
    private static readonly ItemDefinition Wood = ByAegisName["Wood"];

    private static MobDropEntry[] WoodEntry(int rate, bool stealProtected = false) => [new("Wood", rate, stealProtected)];

    [Fact]
    public void KnownItem_GuaranteedRate10000_NeverRolls_AlwaysDrops()
    {
        var resolver = new MobDropResolver(ByAegisName, randomSource: () => 0.9999);
        var drops = resolver.ResolveDrops(WoodEntry(10000));

        var single = Assert.Single(drops);
        Assert.Equal(Wood.Id, single.Item.Id);
        Assert.Equal(1u, single.Amount);
    }

    [Fact]
    public void SubGuaranteedRate_RollsAndCanFail()
    {
        var alwaysFail = new MobDropResolver(ByAegisName, randomSource: () => 0.9);
        var alwaysSucceed = new MobDropResolver(ByAegisName, randomSource: () => 0.1);

        Assert.Empty(alwaysFail.ResolveDrops(WoodEntry(5000)));
        Assert.Single(alwaysSucceed.ResolveDrops(WoodEntry(5000)));
    }

    // mob.cpp:3326-3327: `if (it == nullptr) continue;` - an unresolvable AegisName is silently
    // skipped, never a hard failure that would suppress every OTHER entry on the same mob.
    [Fact]
    public void UnknownAegisName_IsSkippedSilently_OtherEntriesStillResolve()
    {
        var resolver = new MobDropResolver(ByAegisName, randomSource: () => 0.0);
        var drops = resolver.ResolveDrops([new("Totally_Not_A_Real_Item_Name", 10000), new("Wood", 10000)]);

        var single = Assert.Single(drops);
        Assert.Equal(Wood.Id, single.Item.Id);
    }

    // mob.cpp:3338-3341: a PetEgg drop entry is special-cased into pet_create_egg, never a plain
    // ground/inventory item - the pet system is not modeled yet (see ItemGrantResolver's identical
    // getitem-side exclusion), so this resolver excludes it rather than fabricating a fake grant.
    [Fact]
    public void PetEggEntry_IsExcluded()
    {
        var petEgg = ByAegisName.Values.OfType<PetEggItemDefinition>().First();
        var resolver = new MobDropResolver(ByAegisName, randomSource: () => 0.0);
        var drops = resolver.ResolveDrops([new(petEgg.AegisName, 10000)]);

        Assert.Empty(drops);
    }

    [Fact]
    public void StealProtectedFlag_IsCarriedThrough_UnmodifiedByTheResolver()
    {
        var resolver = new MobDropResolver(ByAegisName, randomSource: () => 0.0);
        var drops = resolver.ResolveDrops(WoodEntry(10000, stealProtected: true));

        Assert.True(Assert.Single(drops).StealProtected);
    }

    [Fact]
    public void EmptyDropList_ResolvesToNoDrops()
    {
        var resolver = new MobDropResolver(ByAegisName, randomSource: () => 0.0);
        Assert.Empty(resolver.ResolveDrops([]));
    }

    [Fact]
    public void MultipleEntries_EachRollsIndependently()
    {
        var cottonShirt = ByAegisName["Cotton_Shirt"];
        var resolver = new MobDropResolver(ByAegisName, randomSource: () => 0.0); // Always succeeds.
        var drops = resolver.ResolveDrops([new("Wood", 10000), new(cottonShirt.AegisName, 10000)]);

        Assert.Equal(2, drops.Count);
        Assert.Contains(drops, d => d.Item.Id == Wood.Id);
        Assert.Contains(drops, d => d.Item.Id == cottonShirt.Id);
    }
}
