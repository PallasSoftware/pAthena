using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class ItemIdentifyResolverTests
{
    private static CharacterInventoryItem Row(uint durableId, bool identified) =>
        new(durableId, SlotIndex: durableId, ItemId: 1201, Amount: 1, Equip: 0, identified, Refine: 0, Favorite: 0, Bound: 0);

    [Fact]
    public void FindUnidentified_AllIdentified_ReturnsEmpty()
    {
        var snapshot = new CharacterInventorySnapshot([Row(1, true), Row(2, true)]);
        Assert.Empty(ItemIdentifyResolver.FindUnidentified(snapshot));
    }

    [Fact]
    public void FindUnidentified_MixedRows_ReturnsOnlyUnidentifiedInSourceOrder()
    {
        var snapshot = new CharacterInventorySnapshot([Row(1, false), Row(2, true), Row(3, false)]);
        var unidentified = ItemIdentifyResolver.FindUnidentified(snapshot);
        Assert.Equal([1u, 3u], unidentified.Select(u => u.DurableId));
    }

    [Fact]
    public void FindUnidentified_EmptyInventory_ReturnsEmpty()
    {
        var snapshot = new CharacterInventorySnapshot([]);
        Assert.Empty(ItemIdentifyResolver.FindUnidentified(snapshot));
    }
}
