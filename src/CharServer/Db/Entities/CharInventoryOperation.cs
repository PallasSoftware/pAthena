namespace Athena.Net.CharServer.Db.Entities;

// Durable log of applied inventory operations (idempotency). The row is written in the SAME transaction as the item mutations it
// describes, so "the operation is in the log" and "the operation was applied" are one fact. A repeated OperationId returns
// ResponseBytes unchanged instead of applying again; PayloadHash detects an id reused with different content.
public sealed class CharInventoryOperation
{
    public Guid OperationId { get; set; }
    public uint CharId { get; set; }
    public byte Kind { get; set; }
    public byte[] PayloadHash { get; set; } = [];
    public byte[] ResponseBytes { get; set; } = [];
    public long CreatedAt { get; set; }
}
