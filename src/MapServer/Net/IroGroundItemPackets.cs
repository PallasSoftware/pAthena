using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Client-facing serializers for the ground-item lifecycle. VERIFIED via G108.pcapng - see
// ai/iro-2026-wire.md's "Verified ground-item appearance/pickup/removal wire evidence" section for
// the full independent re-parse and this type's own evidence boundary (0x0ADD's appearance packet
// is intentionally NOT built here yet - 10 of its 24 bytes remain unresolved; only 0x00A1's
// removal, fully confirmed at 6 bytes, is implemented).
internal static class IroGroundItemPackets
{
    // ZC_ITEM_DISAPPEAR (0x00A1), 6 bytes: opcode.W groundActorId.L. Sent to the picking-up
    // player's own session as confirmation; broader bystander visibility (every OTHER session that
    // could see the item) is explicitly deferred to the S8 cross-replica ground-item feed increment
    // - see docs/gameplay/feature-16-execution-plan.md's own stage list.
    internal static byte[] BuildFloorItemRemoval(uint groundActorId)
    {
        var packet = new byte[PacketConstants.ZcFloorItemRemovalLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcFloorItemRemoval);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), groundActorId);
        return packet;
    }
}
