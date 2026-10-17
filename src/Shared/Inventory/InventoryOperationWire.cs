using System.Buffers.Binary;

namespace Athena.Net.Shared.Inventory;

// MapServer <-> CharServer inventory operation contract (Feature 16, stage S2). One compiled-in source file shared by both
// servers (like src/Shared/MapIdentity) so the two ends cannot drift. It is plain data and a binary codec; no gameplay rule
// lives here (what may be added, stacked or consumed is decided by MapServer's regional rules, durability by CharServer).
//
// An operation is an all-or-nothing batch of row mutations on ONE character's containers, identified by a client-chosen
// OperationId. CharServer applies it in a single database transaction together with an operation-log row keyed by that id:
//   - the same id again (lost acknowledgement, reconnect, retry) returns the recorded outcome and applies nothing twice;
//   - every row mutation carries the row revision the caller saw, so a stale cache is rejected, never overwritten.
// Items are never merged by CharServer: the caller names the target row (AddToStack) or asks for a new row (AddNew), because
// stackability and stack limits are item rules, not persistence.

internal enum InventoryContainer : byte { Inventory = 0, Cart = 1, Storage = 2, GuildStorage = 3 }

internal enum InventoryOperationKind : byte
{
    Add = 1, Consume = 2, Equip = 3, Update = 4, Drop = 5, Pickup = 6, Transfer = 7, Refine = 8, Card = 9, Identify = 10, Repair = 11, Use = 12, Expire = 13,
}

internal enum InventoryOperationStatus : byte
{
    Applied = 0,
    Replayed = 1,            // the same OperationId was already applied; the stored outcome is returned unchanged
    PreconditionFailed = 2,  // a named row is missing or changed (revision/amount/item id); nothing was applied
    Invalid = 3,             // malformed, unauthorized, or an OperationId reused with a different payload; nothing was applied
    Unavailable = 4,         // transient database failure; nothing was applied and retrying the same OperationId is safe
}

internal readonly record struct InventoryOption(short Id, short Value, byte Param);

// A complete persisted item row. DurableId/Revision are 0 in an AddNew template.
internal sealed record InventoryItemRow(
    uint DurableId, uint ItemId, uint Amount, uint Equip,
    byte Identify, byte Refine, byte Attribute, byte Favorite, byte Bound, byte EnchantGrade,
    uint Card0, uint Card1, uint Card2, uint Card3,
    InventoryOption Option0, InventoryOption Option1, InventoryOption Option2, InventoryOption Option3, InventoryOption Option4,
    uint ExpireTime, ulong UniqueId, uint EquipSwitch, uint Revision)
{
    internal const int WireLength = 83;

    internal static InventoryItemRow New(uint itemId, uint amount, byte identify = 1) =>
        new(0, itemId, amount, 0, identify, 0, 0, 0, 0, 0, 0, 0, 0, 0, default, default, default, default, default, 0, 0, 0, 0);

    internal InventoryOption[] Options => [Option0, Option1, Option2, Option3, Option4];
    internal uint[] Cards => [Card0, Card1, Card2, Card3];

    internal void Write(Span<byte> span)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span, DurableId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Amount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], Equip);
        span[16] = Identify; span[17] = Refine; span[18] = Attribute; span[19] = Favorite; span[20] = Bound; span[21] = EnchantGrade;
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], Card0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[26..], Card1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[30..], Card2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[34..], Card3);
        var offset = 38;
        foreach (var option in Options)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[offset..], option.Id);
            BinaryPrimitives.WriteInt16LittleEndian(span[(offset + 2)..], option.Value);
            span[offset + 4] = option.Param;
            offset += 5;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[63..], ExpireTime);
        BinaryPrimitives.WriteUInt64LittleEndian(span[67..], UniqueId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[75..], EquipSwitch);
        BinaryPrimitives.WriteUInt32LittleEndian(span[79..], Revision);
    }

    internal static InventoryItemRow Read(ReadOnlySpan<byte> span)
    {
        static InventoryOption Option(ReadOnlySpan<byte> s) => new(BinaryPrimitives.ReadInt16LittleEndian(s), BinaryPrimitives.ReadInt16LittleEndian(s[2..]), s[4]);
        return new(
            BinaryPrimitives.ReadUInt32LittleEndian(span), BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[8..]), BinaryPrimitives.ReadUInt32LittleEndian(span[12..]),
            span[16], span[17], span[18], span[19], span[20], span[21],
            BinaryPrimitives.ReadUInt32LittleEndian(span[22..]), BinaryPrimitives.ReadUInt32LittleEndian(span[26..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[30..]), BinaryPrimitives.ReadUInt32LittleEndian(span[34..]),
            Option(span[38..]), Option(span[43..]), Option(span[48..]), Option(span[53..]), Option(span[58..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[63..]), BinaryPrimitives.ReadUInt64LittleEndian(span[67..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[75..]), BinaryPrimitives.ReadUInt32LittleEndian(span[79..]));
    }
}

internal abstract record InventoryMutation(InventoryContainer Container);

/// <summary>Create a new row from the template (non-stackable items, unique items, or no stack to merge into).</summary>
internal sealed record AddNewRowMutation(InventoryContainer Container, InventoryItemRow Row) : InventoryMutation(Container);

/// <summary>Add Delta to a named stack. Rejected unless the row still has ExpectedItemId/ExpectedAmount/ExpectedRevision and the result stays within MaxAmount.</summary>
internal sealed record AddToStackMutation(InventoryContainer Container, uint DurableId, uint ExpectedItemId, uint Delta, uint ExpectedAmount, uint MaxAmount, uint ExpectedRevision)
    : InventoryMutation(Container);

/// <summary>Remove Amount from a named row; the row is deleted when it reaches 0.</summary>
internal sealed record RemoveFromRowMutation(InventoryContainer Container, uint DurableId, uint Amount, uint ExpectedAmount, uint ExpectedRevision)
    : InventoryMutation(Container);

/// <summary>Replace every mutable column except item id and amount (equip, identify, refine, attribute, cards, options, expiry, bind, ...).</summary>
internal sealed record UpdateRowMutation(InventoryContainer Container, uint DurableId, uint ExpectedRevision, InventoryItemRow NewRow)
    : InventoryMutation(Container);

internal sealed record InventoryOperationRequest(uint AccountId, uint CharId, Guid OperationId, InventoryOperationKind Kind, IReadOnlyList<InventoryMutation> Mutations);

internal sealed record InventoryMutationResult(uint DurableId, bool Deleted, InventoryItemRow? Row);

internal sealed record InventoryOperationResponse(
    InventoryOperationStatus Status, uint CharId, Guid OperationId, int FailedIndex, IReadOnlyList<InventoryMutationResult> Results)
{
    internal bool Succeeded => Status is InventoryOperationStatus.Applied or InventoryOperationStatus.Replayed;
}

internal static class InventoryOperationWire
{
    internal const short RequestOpcode = 0x2b3e;
    internal const short ResponseOpcode = 0x2b3f;
    internal const int MaxMutations = 32;
    internal const int RequestHeaderLength = 30;
    internal const int ResponseHeaderLength = 26;
    internal const int ResultLength = 4 + 1 + InventoryItemRow.WireLength;

    private const byte AddNewTag = 1, AddToStackTag = 2, RemoveTag = 3, UpdateTag = 4;

    internal static byte[] BuildRequest(InventoryOperationRequest request)
    {
        if (request.Mutations.Count is 0 or > MaxMutations) throw new ArgumentException($"An operation carries 1..{MaxMutations} mutations.", nameof(request));
        var length = RequestHeaderLength + request.Mutations.Sum(MutationLength);
        if (length > ushort.MaxValue) throw new ArgumentException("Inventory operation is too large.", nameof(request));

        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, RequestOpcode);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), request.AccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), request.CharId);
        request.OperationId.TryWriteBytes(packet.AsSpan(12), bigEndian: false, out _);
        packet[28] = (byte)request.Kind;
        packet[29] = (byte)request.Mutations.Count;

        var offset = RequestHeaderLength;
        foreach (var mutation in request.Mutations)
        {
            var span = packet.AsSpan(offset);
            switch (mutation)
            {
                case AddNewRowMutation add:
                    span[0] = AddNewTag; span[1] = (byte)add.Container; add.Row.Write(span[2..]);
                    break;
                case AddToStackMutation stack:
                    span[0] = AddToStackTag; span[1] = (byte)stack.Container;
                    BinaryPrimitives.WriteUInt32LittleEndian(span[2..], stack.DurableId);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[6..], stack.ExpectedItemId);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[10..], stack.Delta);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[14..], stack.ExpectedAmount);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[18..], stack.MaxAmount);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[22..], stack.ExpectedRevision);
                    break;
                case RemoveFromRowMutation remove:
                    span[0] = RemoveTag; span[1] = (byte)remove.Container;
                    BinaryPrimitives.WriteUInt32LittleEndian(span[2..], remove.DurableId);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[6..], remove.Amount);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[10..], remove.ExpectedAmount);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[14..], remove.ExpectedRevision);
                    break;
                case UpdateRowMutation update:
                    span[0] = UpdateTag; span[1] = (byte)update.Container;
                    BinaryPrimitives.WriteUInt32LittleEndian(span[2..], update.DurableId);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[6..], update.ExpectedRevision);
                    update.NewRow.Write(span[10..]);
                    break;
                default:
                    throw new ArgumentException($"Unsupported mutation {mutation.GetType().Name}.", nameof(request));
            }
            offset += MutationLength(mutation);
        }
        return packet;
    }

    private static int MutationLength(InventoryMutation mutation) => mutation switch
    {
        AddNewRowMutation => 2 + InventoryItemRow.WireLength,
        AddToStackMutation => 26,
        RemoveFromRowMutation => 18,
        UpdateRowMutation => 10 + InventoryItemRow.WireLength,
        _ => throw new ArgumentException($"Unsupported mutation {mutation.GetType().Name}."),
    };

    internal static bool TryParseRequest(ReadOnlySpan<byte> packet, out InventoryOperationRequest request)
    {
        request = null!;
        if (packet.Length < RequestHeaderLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != RequestOpcode) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]) != packet.Length) return false;
        var count = packet[29];
        if (count is 0 or > MaxMutations || !Enum.IsDefined((InventoryOperationKind)packet[28])) return false;

        var mutations = new List<InventoryMutation>(count);
        var offset = RequestHeaderLength;
        for (var i = 0; i < count; i++)
        {
            if (offset + 2 > packet.Length) return false;
            var span = packet[offset..];
            var container = (InventoryContainer)span[1];
            if (!Enum.IsDefined(container)) return false;
            switch (span[0])
            {
                case AddNewTag when span.Length >= 2 + InventoryItemRow.WireLength:
                    mutations.Add(new AddNewRowMutation(container, InventoryItemRow.Read(span[2..])));
                    offset += 2 + InventoryItemRow.WireLength;
                    break;
                case AddToStackTag when span.Length >= 26:
                    mutations.Add(new AddToStackMutation(container, U32(span, 2), U32(span, 6), U32(span, 10), U32(span, 14), U32(span, 18), U32(span, 22)));
                    offset += 26;
                    break;
                case RemoveTag when span.Length >= 18:
                    mutations.Add(new RemoveFromRowMutation(container, U32(span, 2), U32(span, 6), U32(span, 10), U32(span, 14)));
                    offset += 18;
                    break;
                case UpdateTag when span.Length >= 10 + InventoryItemRow.WireLength:
                    mutations.Add(new UpdateRowMutation(container, U32(span, 2), U32(span, 6), InventoryItemRow.Read(span[10..])));
                    offset += 10 + InventoryItemRow.WireLength;
                    break;
                default:
                    return false;
            }
        }
        if (offset != packet.Length) return false;

        request = new InventoryOperationRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]), BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]),
            new Guid(packet.Slice(12, 16)), (InventoryOperationKind)packet[28], mutations);
        return true;
    }

    internal static byte[] BuildResponse(InventoryOperationResponse response)
    {
        var length = ResponseHeaderLength + response.Results.Count * ResultLength;
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ResponseOpcode);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = (byte)response.Status;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), response.CharId);
        response.OperationId.TryWriteBytes(packet.AsSpan(9), bigEndian: false, out _);
        packet[25] = (byte)(sbyte)response.FailedIndex;
        // The result count is derivable from the length; the byte at 25 carries the failed mutation index (-1 = none).
        var offset = ResponseHeaderLength;
        foreach (var result in response.Results)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset), result.DurableId);
            packet[offset + 4] = result.Deleted ? (byte)1 : (byte)0;
            (result.Row ?? InventoryItemRow.New(0, 0, 0)).Write(packet.AsSpan(offset + 5));
            offset += ResultLength;
        }
        return packet;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> packet, out InventoryOperationResponse response)
    {
        response = null!;
        if (packet.Length < ResponseHeaderLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != ResponseOpcode) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]) != packet.Length) return false;
        if ((packet.Length - ResponseHeaderLength) % ResultLength != 0 || !Enum.IsDefined((InventoryOperationStatus)packet[4])) return false;

        var results = new List<InventoryMutationResult>((packet.Length - ResponseHeaderLength) / ResultLength);
        for (var offset = ResponseHeaderLength; offset < packet.Length; offset += ResultLength)
        {
            var deleted = packet[offset + 4] != 0;
            var row = InventoryItemRow.Read(packet.Slice(offset + 5, InventoryItemRow.WireLength));
            results.Add(new InventoryMutationResult(BinaryPrimitives.ReadUInt32LittleEndian(packet[offset..]), deleted, deleted ? null : row));
        }
        response = new InventoryOperationResponse((InventoryOperationStatus)packet[4], BinaryPrimitives.ReadUInt32LittleEndian(packet[5..]),
            new Guid(packet.Slice(9, 16)), (sbyte)packet[25], results);
        return true;
    }

    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
}
