using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

// Pinned pc_insert_card (pc.cpp:5596-5652): consumes the card and writes its id into the target's
// first free slot, atomically. Client-facing compound UI is Protocol-pending-capture (see
// ItemCardInsertResolver's own doc comment); these tests prove the server-authoritative mutation.
public sealed class CharacterItemCardServiceTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private static readonly ItemDefinition PoringCard = ItemCatalog.Current.ByAegisName["Poring_Card"];
    private static readonly ItemDefinition CottonShirt = ItemCatalog.Current.ByAegisName["Cotton_Shirt_"]; // Slots: 1.

    private static (InMemoryInventoryOperations Operations, CharacterItemCardService Service) Setup(CharacterInventorySnapshot inventory)
    {
        var operations = new InMemoryInventoryOperations(inventory);
        var service = TestInventory.Service(AccountId, CharId, operations, inventory);
        return (operations, new CharacterItemCardService(service));
    }

    [Fact]
    public async Task InsertCardAsync_CompatibleCardIntoFreeSlot_WritesCard0AndConsumesCard()
    {
        var armor = new CharacterInventoryItem(1, 0, CottonShirt.Id, 1, 0, true, 0, 0, 0);
        var card = new CharacterInventoryItem(2, 1, PoringCard.Id, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([armor, card]));

        var (succeeded, failure, updated) = await service.InsertCardAsync(1, 2, ItemCatalog.Current.ById, CancellationToken.None);

        Assert.True(succeeded);
        Assert.Null(failure);
        Assert.Equal((uint)PoringCard.Id, updated!.Items[0].Details.Card0);
        Assert.DoesNotContain(updated.Items, i => i.DurableId == 2); // card consumed
        Assert.Equal((uint)PoringCard.Id, operations.Rows.Single(r => r.DurableId == 1).Card0);
        Assert.DoesNotContain(operations.Rows, r => r.DurableId == 2);
    }

    [Fact]
    public async Task InsertCardAsync_UnknownEquipDurableId_ReturnsUnknownEquipRowWithoutPersisting()
    {
        var card = new CharacterInventoryItem(2, 0, PoringCard.Id, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([card]));

        var (succeeded, failure, updated) = await service.InsertCardAsync(999, 2, ItemCatalog.Current.ById, CancellationToken.None);

        Assert.False(succeeded);
        Assert.Equal(CardInsertFailureReason.UnknownEquipRow, failure);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task InsertCardAsync_EquippedTarget_ReturnsEquipTargetCurrentlyEquippedWithoutPersisting()
    {
        var armor = new CharacterInventoryItem(1, 0, CottonShirt.Id, 1, 0x000010, true, 0, 0, 0);
        var card = new CharacterInventoryItem(2, 1, PoringCard.Id, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([armor, card]));

        var (succeeded, failure, updated) = await service.InsertCardAsync(1, 2, ItemCatalog.Current.ById, CancellationToken.None);

        Assert.False(succeeded);
        Assert.Equal(CardInsertFailureReason.EquipTargetCurrentlyEquipped, failure);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task InsertCardAsync_PersistenceFailure_ReturnsPersistenceFailedWithoutMutatingSnapshot()
    {
        var armor = new CharacterInventoryItem(1, 0, CottonShirt.Id, 1, 0, true, 0, 0, 0);
        var card = new CharacterInventoryItem(2, 1, PoringCard.Id, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([armor, card]));
        operations.DropRequests = 100;

        var (succeeded, failure, updated) = await service.InsertCardAsync(1, 2, ItemCatalog.Current.ById, CancellationToken.None);

        Assert.False(succeeded);
        Assert.Equal(CardInsertFailureReason.PersistenceFailed, failure);
        Assert.Null(updated);
    }
}
