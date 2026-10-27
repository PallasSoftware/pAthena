using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.World;

public sealed class ItemRepairResolverTests
{
    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items = ItemCatalog.Current.ById;
    private static readonly int Knife = ItemCatalog.Current.ByAegisName["Knife"].Id;

    private static CharacterInventoryItem BrokenRow(uint durableId, int itemId) =>
        new(durableId, SlotIndex: durableId, itemId, Amount: 1, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0,
            Detail: ItemInstanceDetail.Empty with { Attribute = 1 });

    private static CharacterInventoryItem IntactRow(uint durableId, int itemId) =>
        new(durableId, SlotIndex: durableId, itemId, Amount: 1, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0);

    [Fact]
    public void FindBroken_NoItemsBroken_ReturnsEmpty()
    {
        var snapshot = new CharacterInventorySnapshot([IntactRow(1, Knife)]);
        Assert.Empty(ItemRepairResolver.FindBroken(snapshot, Items));
    }

    [Fact]
    public void FindBroken_OneBrokenRow_ReturnsIt()
    {
        var snapshot = new CharacterInventorySnapshot([IntactRow(1, Knife), BrokenRow(2, Knife)]);
        var broken = ItemRepairResolver.FindBroken(snapshot, Items);
        var only = Assert.Single(broken);
        Assert.Equal(2u, only.DurableId);
        Assert.Equal(Knife, only.ItemId);
    }

    [Fact]
    public void FindBroken_MultipleBrokenRows_ReturnsAllInSourceOrder()
    {
        var snapshot = new CharacterInventorySnapshot([BrokenRow(1, Knife), IntactRow(2, Knife), BrokenRow(3, Knife)]);
        var broken = ItemRepairResolver.FindBroken(snapshot, Items);
        Assert.Equal([1u, 3u], broken.Select(b => b.DurableId));
    }

    [Fact]
    public void FindBroken_PetEgg_IsExcludedEvenIfAttributeIsSet()
    {
        // Pinned itemdb_ishatched_egg exclusion (script.cpp repair/repairall): a real PetEgg item
        // (9125 E_Duckling_Egg) is never repairable even if its attribute happens to be nonzero.
        const int petEgg = 9125;
        var snapshot = new CharacterInventorySnapshot([BrokenRow(1, petEgg)]);
        Assert.Empty(ItemRepairResolver.FindBroken(snapshot, Items));
    }

    [Fact]
    public void FindBroken_UnknownItemId_StillReturnedAsBroken()
    {
        // Resolver only needs to exclude PetEgg specifically; an item id this catalog doesn't
        // recognize is not assumed to be a pet egg.
        var snapshot = new CharacterInventorySnapshot([BrokenRow(1, 999999)]);
        Assert.Single(ItemRepairResolver.FindBroken(snapshot, Items));
    }
}
