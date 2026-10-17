using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.Net;

// MapServer's boundary to CharServer's durable inventory. CharServer owns every inventory row; MapServer never writes them
// directly. All changes go through one atomic, idempotent operation (see Athena.Net.Shared.Inventory.InventoryOperationWire):
// callers choose a fresh OperationId per logical change and re-send the SAME request after an unanswered attempt, which CharServer
// either applies once or answers from its operation log.
public interface ICharacterInventoryOperations
{
    // The authoritative inventory-container rows, in CharServer's stable order (MapServer assigns dense runtime slots from it).
    Task<CharacterInventoryReadResult> GetInventoryAsync(uint accountId, uint characterId, CancellationToken cancellationToken);

    // Sends one operation. Returns null when no answer arrived (disconnected, timeout): the outcome is then UNKNOWN - the operation may
    // or may not have been applied - and only re-sending the same request can find out.
    Task<InventoryOperationResponse?> ExecuteAsync(InventoryOperationRequest request, CancellationToken cancellationToken);
}
