namespace Athena.Net.CharServer.Db.Entities;

// Outbox record of a player drop whose ground item World has not yet confirmed (Athena.Net.Shared.Inventory.PendingGroundDrop).
// Written in the same transaction that moves the units into the PendingGroundDrop container row ItemRowId, deleted in the one
// transaction that settles the drop - so the units are never deleted without a confirmed ground item and never returned twice.
public sealed class CharPendingGroundDrop
{
    public Guid DropId { get; set; }
    public uint CharId { get; set; }
    public string MapId { get; set; } = "";
    public ushort X { get; set; }
    public ushort Y { get; set; }
    public Guid WorldEpoch { get; set; }
    public uint ItemRowId { get; set; }
    public long CreatedAt { get; set; }
}
