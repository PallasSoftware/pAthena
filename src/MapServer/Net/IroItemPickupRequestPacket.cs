using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// VERIFIED via G108.pcapng frame 83238: 62 03 BD 6E 01 00 CD -> opcode.W groundActorId.L +
// one opaque trailing byte(1) = 7 (see ai/iro-2026-wire.md's "Verified ground-item appearance/
// pickup/removal wire evidence" section for the full independent re-parse). GroundActorId is
// World's OWN minted identity for the ground item (WorldGroundItemView.GroundItemId) - the
// client supplies intent only; the authoritative item, its position, and eligibility are
// resolved server-side against World's own ground-item state, never trusted from the client.
// Matches the SAME "+1 opaque trailing byte beyond a familiar generic shape" pattern already
// proven for attack/equip/unequip/movement/NPC/item-use/skill-up client packets on this
// MapServer connection.
public readonly record struct IroItemPickupRequestPacket(uint GroundActorId, byte OpaqueTrailingByte)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroItemPickupRequestPacket value)
    {
        value = default;
        if (packet.Length != PacketConstants.IroCzItemPickupRequestLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzItemPickupRequest)
        {
            return false;
        }

        value = new(BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]), packet[6]);
        return true;
    }
}
