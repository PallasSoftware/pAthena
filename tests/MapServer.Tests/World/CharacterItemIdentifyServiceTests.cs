using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

// Pinned pc_identifyall (pc.cpp:5664-5679). Client-facing identify-list UI is Protocol-pending-
// capture (see ItemIdentifyResolver's own doc comment); these tests prove the server-authoritative
// bulk mutation only.
public sealed class CharacterItemIdentifyServiceTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    private static (InMemoryInventoryOperations Operations, CharacterItemIdentifyService Service) Setup(CharacterInventorySnapshot inventory)
    {
        var operations = new InMemoryInventoryOperations(inventory);
        var service = TestInventory.Service(AccountId, CharId, operations, inventory);
        return (operations, new CharacterItemIdentifyService(service));
    }

    [Fact]
    public async Task IdentifyAllAsync_MixedRows_IdentifiesOnlyUnidentifiedOnesInOneOperation()
    {
        var identified = new CharacterInventoryItem(1, 0, 1201, 1, 0, true, 0, 0, 0);
        var unidentified1 = new CharacterInventoryItem(2, 1, 1201, 1, 0, false, 0, 0, 0);
        var unidentified2 = new CharacterInventoryItem(3, 2, 1201, 1, 0, false, 0, 0, 0);
        var (operations, service) = Setup(new([identified, unidentified1, unidentified2]));

        var (result, updated) = await service.IdentifyAllAsync(CancellationToken.None);

        Assert.Equal(CharacterItemIdentifyService.IdentifyAllResult.Success, result);
        Assert.All(updated!.Items, item => Assert.True(item.Identified));
        Assert.Single(operations.Requests); // one atomic operation for every row
        Assert.All(operations.Rows, row => Assert.Equal(1, row.Identify));
    }

    [Fact]
    public async Task IdentifyAllAsync_EverythingAlreadyIdentified_ReturnsNothingToIdentifyWithoutPersisting()
    {
        var identified = new CharacterInventoryItem(1, 0, 1201, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([identified]));

        var (result, updated) = await service.IdentifyAllAsync(CancellationToken.None);

        Assert.Equal(CharacterItemIdentifyService.IdentifyAllResult.NothingToIdentify, result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task IdentifyAllAsync_PersistenceFailure_ReturnsPersistenceFailedWithoutMutatingSnapshot()
    {
        var unidentified = new CharacterInventoryItem(1, 0, 1201, 1, 0, false, 0, 0, 0);
        var (operations, service) = Setup(new([unidentified]));
        operations.DropRequests = 100;

        var (result, updated) = await service.IdentifyAllAsync(CancellationToken.None);

        Assert.Equal(CharacterItemIdentifyService.IdentifyAllResult.PersistenceFailed, result);
        Assert.Null(updated);
    }
}
