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

// PendingGroundDrop (owner = the character) holds units a player dropped while World's ground item is not yet confirmed - see
// PendingGroundDrop below. They stay durable player-owned rows until the drop is settled, never deleted on a guess.
public enum InventoryContainer : byte { Inventory = 0, Cart = 1, Storage = 2, GuildStorage = 3, PendingGroundDrop = 4 }

public enum InventoryOperationKind : byte
{
    Add = 1, Consume = 2, Equip = 3, Update = 4, Drop = 5, Pickup = 6, Transfer = 7, Refine = 8, Card = 9, Identify = 10, Repair = 11, Use = 12, Expire = 13, EnchantGrade = 14, EquipSwitch = 15,
}

public enum InventoryOperationStatus : byte
{
    Applied = 0,
    Replayed = 1,            // the same OperationId was already applied; the stored outcome is returned unchanged
    PreconditionFailed = 2,  // a named row is missing or changed (revision/amount/item id); nothing was applied
    Invalid = 3,             // malformed, unauthorized, or an OperationId reused with a different payload; nothing was applied
    Unavailable = 4,         // transient database failure; nothing was applied and retrying the same OperationId is safe
}

public readonly record struct InventoryOption(short Id, short Value, byte Param);

// A complete persisted item row. DurableId/Revision are 0 in an AddNew template.
public sealed record InventoryItemRow(
    uint DurableId, uint ItemId, uint Amount, uint Equip,
    byte Identify, byte Refine, byte Attribute, byte Favorite, byte Bound, byte EnchantGrade,
    uint Card0, uint Card1, uint Card2, uint Card3,
    InventoryOption Option0, InventoryOption Option1, InventoryOption Option2, InventoryOption Option3, InventoryOption Option4,
    uint ExpireTime, ulong UniqueId, uint EquipSwitch, uint Revision)
{
    public const int WireLength = 83;

    /// <summary>UniqueId value of an AddNew template meaning "CharServer assigns a fresh unique id" (pc_generate_unique_id).</summary>
    public const ulong GenerateUniqueId = ulong.MaxValue;

    public static InventoryItemRow New(uint itemId, uint amount, byte identify = 1) =>
        new(0, itemId, amount, 0, identify, 0, 0, 0, 0, 0, 0, 0, 0, 0, default, default, default, default, default, 0, 0, 0, 0);

    public InventoryOption[] Options => [Option0, Option1, Option2, Option3, Option4];
    public uint[] Cards => [Card0, Card1, Card2, Card3];

    public void Write(Span<byte> span)
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

    public static InventoryItemRow Read(ReadOnlySpan<byte> span)
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

public abstract record InventoryMutation(InventoryContainer Container);

/// <summary>Create a new row from the template (non-stackable items, unique items, or no stack to merge into).</summary>
public sealed record AddNewRowMutation(InventoryContainer Container, InventoryItemRow Row) : InventoryMutation(Container);

/// <summary>Add Delta to a named stack. Rejected unless the row still has ExpectedItemId/ExpectedAmount/ExpectedRevision and the result stays within MaxAmount.</summary>
public sealed record AddToStackMutation(InventoryContainer Container, uint DurableId, uint ExpectedItemId, uint Delta, uint ExpectedAmount, uint MaxAmount, uint ExpectedRevision)
    : InventoryMutation(Container);

/// <summary>Remove Amount from a named row; the row is deleted when it reaches 0.</summary>
public sealed record RemoveFromRowMutation(InventoryContainer Container, uint DurableId, uint Amount, uint ExpectedAmount, uint ExpectedRevision)
    : InventoryMutation(Container);

/// <summary>Replace every mutable column except item id and amount (equip, identify, refine, attribute, cards, options, expiry, bind, ...).</summary>
public sealed record UpdateRowMutation(InventoryContainer Container, uint DurableId, uint ExpectedRevision, InventoryItemRow NewRow)
    : InventoryMutation(Container);

// ZenyCost/ExpectedZeny default to 0, meaning "this operation spends no zeny" - every mutation kind except Refine
// leaves them untouched. When ZenyCost > 0, CharServer checks the character's CURRENT zeny equals ExpectedZeny
// (the caller's last-known balance, exactly like ExpectedRevision/ExpectedAmount on a row mutation - rejecting a
// stale write rather than overwriting) AND is >= ZenyCost, deducts ZenyCost, and persists that deduction in the
// SAME transaction as the row mutations and the operation-log row - one all-or-nothing commit, matching the
// pinned pc_payzeny-before-anything-else ordering (clif.cpp:22746: zeny is checked/paid before any item is
// touched). Precondition failure here (stale balance OR insufficient funds) rejects the WHOLE operation; nothing
// is mutated, matching every other precondition in this contract.
//
// RecordPendingDrop / ResolvesPendingDrop make a player drop a transactional outbox (CharacterItemDropService): the operation that
// moves units into the PendingGroundDrop container also writes this record in the same transaction, and the ONE operation that
// settles it (units consumed by a confirmed ground item, or returned to the inventory after World fenced the drop) deletes it in
// its transaction - and is refused (PreconditionFailed) when the record is already gone, so a drop is settled exactly once.
public sealed record InventoryOperationRequest(uint AccountId, uint CharId, Guid OperationId, InventoryOperationKind Kind, IReadOnlyList<InventoryMutation> Mutations,
    uint ZenyCost = 0, uint ExpectedZeny = 0, PendingGroundDrop? RecordPendingDrop = null, Guid ResolvesPendingDrop = default);

// A player drop between "units left the inventory" and "World confirmed or fenced the ground item". DropId is World's drop
// idempotency key; WorldEpoch the ground-item simulation epoch observed before the drop; ItemRowId the PendingGroundDrop
// container row holding the units (assigned by CharServer, 0 in a request).
public sealed record PendingGroundDrop(Guid DropId, string MapId, ushort X, ushort Y, Guid WorldEpoch, uint ItemRowId = 0)
{
    public const int MaxMapIdLength = 64;
}

public sealed record InventoryMutationResult(uint DurableId, bool Deleted, InventoryItemRow? Row);

// RemainingZeny is the character's authoritative zeny balance AFTER this operation when the request carried a
// ZenyCost > 0 - the caller never has to re-derive it. It is always 0 for a ZenyCost == 0 request (every
// non-currency operation never reads the character row for this at all, so there is no balance to report).
public sealed record InventoryOperationResponse(
    InventoryOperationStatus Status, uint CharId, Guid OperationId, int FailedIndex, IReadOnlyList<InventoryMutationResult> Results, uint RemainingZeny = 0)
{
    public bool Succeeded => Status is InventoryOperationStatus.Applied or InventoryOperationStatus.Replayed;
}

public static class InventoryOperationWire
{
    public const short RequestOpcode = 0x2b3e;
    public const short ResponseOpcode = 0x2b3f;
    public const int MaxMutations = 32;
    // +8 over the original 30: ZenyCost.L (4) then ExpectedZeny.L (4), appended after the original fixed fields
    // so every existing byte offset up to 30 is unchanged.
    public const int RequestHeaderLength = 38;
    // +4 over the original 26: RemainingZeny.L (4) appended after the original fixed fields.
    public const int ResponseHeaderLength = 30;
    public const int ResultLength = 4 + 1 + InventoryItemRow.WireLength;

    private const byte AddNewTag = 1, AddToStackTag = 2, RemoveTag = 3, UpdateTag = 4;
    // Optional trailer after the mutations (the total length covers it, so the payload hash does too).
    private const byte RecordPendingDropTag = 1, ResolvesPendingDropTag = 2;

    public static byte[] BuildRequest(InventoryOperationRequest request)
    {
        if (request.Mutations.Count is 0 or > MaxMutations) throw new ArgumentException($"An operation carries 1..{MaxMutations} mutations.", nameof(request));
        var length = RequestHeaderLength + request.Mutations.Sum(MutationLength) + TrailerLength(request);
        if (length > ushort.MaxValue) throw new ArgumentException("Inventory operation is too large.", nameof(request));

        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, RequestOpcode);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), request.AccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), request.CharId);
        request.OperationId.TryWriteBytes(packet.AsSpan(12), bigEndian: false, out _);
        packet[28] = (byte)request.Kind;
        packet[29] = (byte)request.Mutations.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(30), request.ZenyCost);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(34), request.ExpectedZeny);

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
        if (request.RecordPendingDrop is { } pending)
        {
            var span = packet.AsSpan(offset);
            span[0] = RecordPendingDropTag;
            pending.DropId.TryWriteBytes(span[1..], bigEndian: false, out _);
            pending.WorldEpoch.TryWriteBytes(span[17..], bigEndian: false, out _);
            BinaryPrimitives.WriteUInt16LittleEndian(span[33..], pending.X);
            BinaryPrimitives.WriteUInt16LittleEndian(span[35..], pending.Y);
            span[37] = (byte)pending.MapId.Length;
            System.Text.Encoding.ASCII.GetBytes(pending.MapId, span[38..]);
            offset += 38 + pending.MapId.Length;
        }
        if (request.ResolvesPendingDrop != Guid.Empty)
        {
            packet[offset] = ResolvesPendingDropTag;
            request.ResolvesPendingDrop.TryWriteBytes(packet.AsSpan(offset + 1), bigEndian: false, out _);
        }
        return packet;
    }

    private static int TrailerLength(InventoryOperationRequest request)
    {
        if (request.RecordPendingDrop is { MapId: var map } && (map.Length is 0 or > PendingGroundDrop.MaxMapIdLength || map.Any(c => c > 0x7F)))
            throw new ArgumentException("A pending drop needs a 1..64 character ASCII map id.", nameof(request));
        return (request.RecordPendingDrop is { } pending ? 38 + pending.MapId.Length : 0) + (request.ResolvesPendingDrop != Guid.Empty ? 17 : 0);
    }

    private static int MutationLength(InventoryMutation mutation) => mutation switch
    {
        AddNewRowMutation => 2 + InventoryItemRow.WireLength,
        AddToStackMutation => 26,
        RemoveFromRowMutation => 18,
        UpdateRowMutation => 10 + InventoryItemRow.WireLength,
        _ => throw new ArgumentException($"Unsupported mutation {mutation.GetType().Name}."),
    };

    public static bool TryParseRequest(ReadOnlySpan<byte> packet, out InventoryOperationRequest request)
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
        PendingGroundDrop? recordPendingDrop = null;
        var resolvesPendingDrop = Guid.Empty;
        if (offset < packet.Length && packet[offset] == RecordPendingDropTag)
        {
            if (offset + 38 > packet.Length) return false;
            var span = packet[offset..];
            var mapLength = span[37];
            if (mapLength is 0 or > PendingGroundDrop.MaxMapIdLength || offset + 38 + mapLength > packet.Length) return false;
            var mapBytes = span.Slice(38, mapLength);
            foreach (var b in mapBytes) if (b is 0 or > 0x7F) return false;
            recordPendingDrop = new PendingGroundDrop(new Guid(span.Slice(1, 16)), System.Text.Encoding.ASCII.GetString(mapBytes),
                BinaryPrimitives.ReadUInt16LittleEndian(span[33..]), BinaryPrimitives.ReadUInt16LittleEndian(span[35..]), new Guid(span.Slice(17, 16)));
            offset += 38 + mapLength;
        }
        if (offset < packet.Length && packet[offset] == ResolvesPendingDropTag)
        {
            if (offset + 17 > packet.Length) return false;
            resolvesPendingDrop = new Guid(packet.Slice(offset + 1, 16));
            offset += 17;
        }
        if (offset != packet.Length) return false;

        request = new InventoryOperationRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]), BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]),
            new Guid(packet.Slice(12, 16)), (InventoryOperationKind)packet[28], mutations,
            BinaryPrimitives.ReadUInt32LittleEndian(packet[30..]), BinaryPrimitives.ReadUInt32LittleEndian(packet[34..]),
            recordPendingDrop, resolvesPendingDrop);
        return true;
    }

    public static byte[] BuildResponse(InventoryOperationResponse response)
    {
        var length = ResponseHeaderLength + response.Results.Count * ResultLength;
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ResponseOpcode);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = (byte)response.Status;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), response.CharId);
        response.OperationId.TryWriteBytes(packet.AsSpan(9), bigEndian: false, out _);
        packet[25] = (byte)(sbyte)response.FailedIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(26), response.RemainingZeny);
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

    public static bool TryParseResponse(ReadOnlySpan<byte> packet, out InventoryOperationResponse response)
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
            new Guid(packet.Slice(9, 16)), (sbyte)packet[25], results, BinaryPrimitives.ReadUInt32LittleEndian(packet[26..]));
        return true;
    }

    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
}

/// <summary>Reads one container's rows. Variable-length response: opcode.W length.W result.B charId.L container.B count.W rows[count] (total length in `length`).</summary>
public static class InventoryListWire
{
    public const short RequestOpcode = 0x2b33;
    public const short ResponseOpcode = 0x2b34;
    public const int RequestLength = 11;
    public const int ResponseHeaderLength = 12;

    public static byte[] BuildGetRequest(uint accountId, uint charId, InventoryContainer container)
    {
        var packet = new byte[RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, RequestOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), charId);
        packet[10] = (byte)container;
        return packet;
    }

    public static bool TryParseGet(ReadOnlySpan<byte> packet, out uint accountId, out uint charId, out InventoryContainer container)
    {
        accountId = charId = 0;
        container = InventoryContainer.Inventory;
        if (packet.Length != RequestLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != RequestOpcode || !Enum.IsDefined((InventoryContainer)packet[10])) return false;
        accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]);
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]);
        container = (InventoryContainer)packet[10];
        return true;
    }

    /// <summary>result 0 = ok; any other value carries no rows.</summary>
    public static byte[] BuildResponse(byte result, uint charId, InventoryContainer container, IReadOnlyList<InventoryItemRow>? rows)
    {
        var count = result == 0 ? rows?.Count ?? 0 : 0;
        var length = ResponseHeaderLength + count * InventoryItemRow.WireLength;
        if (length > ushort.MaxValue) throw new ArgumentException("Container list is too large for one packet.", nameof(rows));
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ResponseOpcode);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), charId);
        packet[9] = (byte)container;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), (ushort)count);
        for (var i = 0; i < count; i++) rows![i].Write(packet.AsSpan(ResponseHeaderLength + i * InventoryItemRow.WireLength));
        return packet;
    }

    /// <summary>False for a malformed packet or a duplicated durable id (a primary key can never repeat in one read).</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> packet, out byte result, out uint charId, out InventoryContainer container, out IReadOnlyList<InventoryItemRow> rows)
    {
        result = 1; charId = 0; container = InventoryContainer.Inventory; rows = [];
        if (packet.Length < ResponseHeaderLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != ResponseOpcode) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]) != packet.Length || !Enum.IsDefined((InventoryContainer)packet[9])) return false;
        result = packet[4];
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[5..]);
        container = (InventoryContainer)packet[9];
        var count = BinaryPrimitives.ReadUInt16LittleEndian(packet[10..]);
        if (packet.Length != ResponseHeaderLength + count * InventoryItemRow.WireLength) return false;
        var list = new List<InventoryItemRow>(count);
        var seen = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            var row = InventoryItemRow.Read(packet[(ResponseHeaderLength + i * InventoryItemRow.WireLength)..]);
            if (!seen.Add(row.DurableId)) return false;
            list.Add(row);
        }
        rows = list;
        return true;
    }
}

/// <summary>
/// Lists a character's unsettled player drops (see PendingGroundDrop) so a MapServer can settle them after a crash or outage.
/// Request: opcode.W accountId.L charId.L. Response: opcode.W length.W result.B charId.L count.B, then per record dropId(16)
/// epoch(16) x.W y.W itemRowId.L mapLen.B map.
/// </summary>
public static class PendingGroundDropListWire
{
    public const short RequestOpcode = 0x2b44;
    public const short ResponseOpcode = 0x2b45;
    public const int RequestLength = 10;
    public const int ResponseHeaderLength = 10;
    public const int MaxRecords = 255;

    public static byte[] BuildRequest(uint accountId, uint charId)
    {
        var packet = new byte[RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, RequestOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), charId);
        return packet;
    }

    public static bool TryParseRequest(ReadOnlySpan<byte> packet, out uint accountId, out uint charId)
    {
        accountId = charId = 0;
        if (packet.Length != RequestLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != RequestOpcode) return false;
        accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]);
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]);
        return true;
    }

    /// <summary>result 0 = ok; any other value carries no records.</summary>
    public static byte[] BuildResponse(byte result, uint charId, IReadOnlyList<PendingGroundDrop>? records)
    {
        var list = result == 0 ? (records ?? []).Take(MaxRecords).ToArray() : [];
        var length = ResponseHeaderLength + list.Sum(r => 41 + r.MapId.Length);
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ResponseOpcode);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), charId);
        packet[9] = (byte)list.Length;
        var offset = ResponseHeaderLength;
        foreach (var record in list)
        {
            var span = packet.AsSpan(offset);
            record.DropId.TryWriteBytes(span, bigEndian: false, out _);
            record.WorldEpoch.TryWriteBytes(span[16..], bigEndian: false, out _);
            BinaryPrimitives.WriteUInt16LittleEndian(span[32..], record.X);
            BinaryPrimitives.WriteUInt16LittleEndian(span[34..], record.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(span[36..], record.ItemRowId);
            span[40] = (byte)record.MapId.Length;
            System.Text.Encoding.ASCII.GetBytes(record.MapId, span[41..]);
            offset += 41 + record.MapId.Length;
        }
        return packet;
    }

    public static bool TryParseResponse(ReadOnlySpan<byte> packet, out byte result, out uint charId, out IReadOnlyList<PendingGroundDrop> records)
    {
        result = 1; charId = 0; records = [];
        if (packet.Length < ResponseHeaderLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != ResponseOpcode) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]) != packet.Length) return false;
        result = packet[4];
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[5..]);
        var list = new List<PendingGroundDrop>(packet[9]);
        var offset = ResponseHeaderLength;
        for (var i = 0; i < packet[9]; i++)
        {
            if (offset + 41 > packet.Length) return false;
            var span = packet[offset..];
            var mapLength = span[40];
            if (mapLength is 0 or > PendingGroundDrop.MaxMapIdLength || offset + 41 + mapLength > packet.Length) return false;
            list.Add(new PendingGroundDrop(new Guid(span[..16]), System.Text.Encoding.ASCII.GetString(span.Slice(41, mapLength)),
                BinaryPrimitives.ReadUInt16LittleEndian(span[32..]), BinaryPrimitives.ReadUInt16LittleEndian(span[34..]),
                new Guid(span.Slice(16, 16)), BinaryPrimitives.ReadUInt32LittleEndian(span[36..])));
            offset += 41 + mapLength;
        }
        if (offset != packet.Length) return false;
        records = list;
        return true;
    }
}
