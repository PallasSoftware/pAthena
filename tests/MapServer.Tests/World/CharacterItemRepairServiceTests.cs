using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

// Pinned BUILDIN_FUNC(repair) (script.cpp:9452-9474): clears a broken item's attribute back to 0 in
// one durable operation. Client-facing repair-list UI is Protocol-pending-capture (see
// CharacterItemRepairService's own doc comment); these tests prove the server-authoritative
// mutation only.
public sealed class CharacterItemRepairServiceTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    private static (InMemoryInventoryOperations Operations, CharacterItemRepairService Service) Setup(CharacterInventorySnapshot inventory)
    {
        var operations = new InMemoryInventoryOperations(inventory);
        var service = TestInventory.Service(AccountId, CharId, operations, inventory);
        return (operations, new CharacterItemRepairService(service));
    }

    private static ItemInstanceDetail Broken => ItemInstanceDetail.Empty with { Attribute = 1, Revision = 1 };

    [Fact]
    public async Task RepairAsync_BrokenItem_ClearsAttributeAndPersists()
    {
        var brokenRow = new CharacterInventoryItem(1, 0, 1201, 1, 0, true, 0, 0, 0, Broken);
        var (operations, service) = Setup(new([brokenRow]));

        var (result, updated) = await service.RepairAsync(1, CancellationToken.None);

        Assert.Equal(CharacterItemRepairService.RepairResult.Success, result);
        Assert.Equal(0, updated!.Items[0].Details.Attribute);
        Assert.Equal(0, operations.Rows.Single().Attribute);
    }

    [Fact]
    public async Task RepairAsync_NotBrokenItem_ReturnsNotBrokenWithoutPersisting()
    {
        var intactRow = new CharacterInventoryItem(1, 0, 1201, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([intactRow]));

        var (result, updated) = await service.RepairAsync(1, CancellationToken.None);

        Assert.Equal(CharacterItemRepairService.RepairResult.NotBroken, result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task RepairAsync_UnknownDurableId_ReturnsUnknownRow()
    {
        var (operations, service) = Setup(new([]));

        var (result, updated) = await service.RepairAsync(999, CancellationToken.None);

        Assert.Equal(CharacterItemRepairService.RepairResult.UnknownRow, result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task RepairAsync_PersistenceFailure_ReturnsPersistenceFailedWithoutMutatingSnapshot()
    {
        var brokenRow = new CharacterInventoryItem(1, 0, 1201, 1, 0, true, 0, 0, 0, Broken);
        var (operations, service) = Setup(new([brokenRow]));
        operations.DropRequests = 100;

        var (result, updated) = await service.RepairAsync(1, CancellationToken.None);

        Assert.Equal(CharacterItemRepairService.RepairResult.PersistenceFailed, result);
        Assert.Null(updated);
    }
}
