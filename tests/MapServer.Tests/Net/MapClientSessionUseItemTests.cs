using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.Shared.Inventory;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.Net;

// Wire-level integration test using MapClientSession's real socket path (RunAsync/
// HandlePacketAsync), matching the established pattern in MapClientSessionEquipmentMutationTests/
// MapClientSessionMonsterCombatTests - only CharServer persistence is a test double.
//
// Live-verified request framing (see ai/map-server.md "Item-use request"): 0x00A7, 9 bytes,
// opcode.W clientIndex.W accountId.L opaqueByte.B. Fixture uses the same First Aid Box (23484)
// container scenario the live capture exercised - clientIndex 4 -> SlotIndex 2 -> ItemId 23484.
public sealed class MapClientSessionUseItemTests
{
    private const uint AccountId = 2_000_000;
    private const uint CharId = 9;

    // Live-captured bytes: A7 00 04 00 80 84 1E 00 D2.
    private static byte[] UseItemRequestPacket(ushort clientIndex, uint accountId, byte opaqueByte = 0xd2)
    {
        var packet = new byte[PacketConstants.IroCzUseItemLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzUseItem);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), accountId);
        packet[8] = opaqueByte;
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private static CharacterInventorySnapshot ThreeStarterRowsPlusFirstAidBox() => new(
    [
        new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0), // Knife, equipped
        new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, 2301, 1, 0x000010, true, 0, 0, 0), // Cotton Shirt, equipped
        new CharacterInventoryItem(DurableId: 3, SlotIndex: 2, 23484, 1, 0, true, 0, 0, 0), // First Aid Box, unequipped - clientIndex 4
    ]);

    // The user's exact required scenario: slot 0 Knife, slot 1 Cotton Shirt, slot 2 FirstAidBox,
    // slot 3 Wood - proving that after consuming the First Aid Box (leaving a hole at slot 2),
    // Wood remains slot 3/clientIndex 5, and the first grant reuses the hole at slot 2/clientIndex 4.
    private static CharacterInventorySnapshot FourStarterRowsPlusFirstAidBoxAndWood() => new(
    [
        new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0), // Knife, equipped
        new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, 2301, 1, 0x000010, true, 0, 0, 0), // Cotton Shirt, equipped
        new CharacterInventoryItem(DurableId: 3, SlotIndex: 2, 23484, 1, 0, true, 0, 0, 0), // First Aid Box, unequipped - clientIndex 4
        new CharacterInventoryItem(DurableId: 4, SlotIndex: 3, 6008, 5, 0, true, 0, 0, 0), // Wood, unequipped - clientIndex 5
    ]);

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, InMemoryInventoryOperations Persistence)> SetupAsync(
        CharacterInventorySnapshot initialInventory)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var gameplayState = new CharacterGameplayState(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        var gameplayPersistence = new StubGameplayStatePersistence(gameplayState);
        var inventoryPersistence = new InMemoryInventoryOperations(initialInventory);

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: gameplayPersistence, inventoryOperations: inventoryPersistence,
            accountId: AccountId, charId: CharId);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        // Consume the fixed 4-packet iRO bootstrap (0x0B18/0x0283/0x0ADE/0x02EB) plus the
        // variable-length 0x0B32 skill list that now always follows it.
        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);

        return (client, stream, session, run, inventoryPersistence);
    }

    private sealed class StubGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == AccountId && c == CharId ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(u);
    }

    [Fact]
    public async Task UseFirstAidBox_ConsumesOneRow_GrantsFiveItems_SendsCorrectAck()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId)); // clientIndex 4 -> slotIndex 2 -> First Aid Box

        // ZC_USE_ITEM_ACK2 (0x01C8, 15 bytes): index.W itemId.L accountId.L amount.W result.B.
        var ack = await ReadExact(stream, 15);
        Assert.Equal((short)0x01c8, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2))); // ack uses the SAME client index
        Assert.Equal(23484u, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4))); // First Aid Box ClientViewId
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(8)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(12))); // amount after use: row deleted
        Assert.Equal((byte)1, ack[14]); // success

        // Five 0x0B41 grant packets follow, one per getitem statement in pinned order.
        var expectedGrants = new (int ItemId, ushort Count)[] { (11518, 10), (11614, 20), (12325, 15), (22542, 1), (23485, 1) };
        foreach (var (itemId, count) in expectedGrants)
        {
            var pickup = await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
            Assert.Equal((short)PacketConstants.ZcItemPickupAck, BinaryPrimitives.ReadInt16LittleEndian(pickup));
            Assert.Equal(count, BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(4)));
            Assert.Equal((uint)itemId, BinaryPrimitives.ReadUInt32LittleEndian(pickup.AsSpan(6)));
        }

        // ONE durable operation: the box is consumed and all five contents are created together (all or nothing).
        var operation = Assert.Single(persistence.Applied);
        Assert.Equal(6, operation.Mutations.Count);
        var consume = Assert.IsType<RemoveFromRowMutation>(operation.Mutations[0]);
        Assert.Equal((3u, 1u), (consume.DurableId, consume.Amount)); // First Aid Box's DurableId=3
        Assert.Equal(5, operation.Mutations.OfType<AddNewRowMutation>().Count());

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseFirstAidBox_RowDeleted_RuntimeSnapshotImmediatelyReflectsRemovalAndGrants()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId));
        await ReadExact(stream, 15); // ack
        for (var i = 0; i < 5; i++) await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);

        // Requirement 11: the First Aid Box slot is gone; no fifth stale row.
        Assert.DoesNotContain(session.Inventory!.Items, i => i.ItemId == 23484);
        // Requirement: the five granted items are immediately present in the SAME session.
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 11518 && i.Amount == 10);
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 11614 && i.Amount == 20);
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 12325 && i.Amount == 15);
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 22542 && i.Amount == 1);
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 23485 && i.Amount == 1);

        // Requirement: existing equipped Knife/Cotton Shirt indices remain stable (they were
        // BEFORE the deleted slot, so WithoutSlot's renumbering never touches them).
        Assert.Equal(0u, session.Inventory.Items.Single(i => i.ItemId == 1201).SlotIndex);
        Assert.Equal(1u, session.Inventory.Items.Single(i => i.ItemId == 2301).SlotIndex);
        Assert.Equal(1201, session.Equipment!.RightHandItemId);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Live-verified evidence this test proves: consuming the First Aid Box (durable row at slot
    // 2) must leave a HOLE at slot 2 - Wood (already occupying slot 3) must NOT be renumbered
    // down to slot 2. The first getitem grant (11518) must then reuse that exact hole at slot 2/
    // clientIndex 4, and Wood must still report slot 3/clientIndex 5 afterward. This is the
    // pinned pc_delitem/pc_additem behavior (array slot zeroed in place, first-empty-slot reuse)
    // this architecture exists to mirror - never CharServer row-position renumbering.
    [Fact]
    public async Task UseFirstAidBox_WithWoodAlreadyPastIt_LeavesHoleAtSlotTwo_FirstGrantReusesHole_WoodStaysAtSlotThree()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(FourStarterRowsPlusFirstAidBoxAndWood());
        using var _ = client;

        // Setup check: four starting rows at the expected server slots before consuming anything.
        Assert.Equal(4, session.Inventory!.Items.Count);
        Assert.Equal(0u, session.Inventory.Items.Single(i => i.ItemId == 1201).SlotIndex);
        Assert.Equal(1u, session.Inventory.Items.Single(i => i.ItemId == 2301).SlotIndex);
        Assert.Equal(2u, session.Inventory.Items.Single(i => i.ItemId == 23484).SlotIndex);
        Assert.Equal(3u, session.Inventory.Items.Single(i => i.ItemId == 6008).SlotIndex);

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId)); // clientIndex 4 -> slotIndex 2 -> First Aid Box

        var ack = await ReadExact(stream, 15);
        Assert.Equal((short)0x01c8, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((byte)1, ack[14]); // success

        // Immediately after the consume (before any grant is applied), the First Aid Box row is
        // gone and Wood remains EXACTLY at slot 3 - the hole at slot 2 has not been backfilled by
        // renumbering.
        Assert.DoesNotContain(session.Inventory!.Items, i => i.ItemId == 23484);
        Assert.Equal(3u, session.Inventory.Items.Single(i => i.ItemId == 6008).SlotIndex);

        // First grant (11518) must reuse the hole at slot 2 / clientIndex 4.
        var firstGrantPickup = await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
        Assert.Equal((short)PacketConstants.ZcItemPickupAck, BinaryPrimitives.ReadInt16LittleEndian(firstGrantPickup));
        var firstGrantClientIndex = BinaryPrimitives.ReadUInt16LittleEndian(firstGrantPickup.AsSpan(2));
        Assert.Equal((ushort)4, firstGrantClientIndex); // slot 2 + 2
        Assert.Equal(11518u, BinaryPrimitives.ReadUInt32LittleEndian(firstGrantPickup.AsSpan(6)));
        Assert.Equal(2u, session.Inventory.Items.Single(i => i.ItemId == 11518).SlotIndex);
        Assert.Equal(3u, session.Inventory.Items.Single(i => i.ItemId == 6008).SlotIndex); // Wood still slot 3

        // Remaining four grants continue using subsequent first-free slots (4, 5, 6, 7 - none of
        // which collide with Wood's slot 3).
        var expectedRemainingGrants = new (int ItemId, ushort Count, uint ExpectedSlot)[]
        {
            (11614, 20, 4),
            (12325, 15, 5),
            (22542, 1, 6),
            (23485, 1, 7),
        };
        foreach (var (itemId, count, expectedSlot) in expectedRemainingGrants)
        {
            var pickup = await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
            Assert.Equal((short)PacketConstants.ZcItemPickupAck, BinaryPrimitives.ReadInt16LittleEndian(pickup));
            Assert.Equal(count, BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(4)));
            Assert.Equal((uint)itemId, BinaryPrimitives.ReadUInt32LittleEndian(pickup.AsSpan(6)));
            Assert.Equal((ushort)(expectedSlot + 2), BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(2)));
            Assert.Equal(expectedSlot, session.Inventory.Items.Single(i => i.ItemId == itemId).SlotIndex);
        }

        // Wood's slot/clientIndex is unaffected by the entire hole/reuse sequence.
        Assert.Equal(3u, session.Inventory.Items.Single(i => i.ItemId == 6008).SlotIndex);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Proves that after the hole/reuse sequence, using/equipping an EXISTING item (the Knife,
    // never touched by the consume/grant sequence) still resolves to the same authoritative
    // durable row and works exactly as before - the durable-id/runtime-slot split does not break
    // ordinary equip/unequip for unrelated rows.
    [Fact]
    public async Task AfterHoleReuseSequence_EquippingExistingItem_ResolvesSameAuthoritativeRow()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(FourStarterRowsPlusFirstAidBoxAndWood());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId));
        await ReadExact(stream, 15); // ack
        for (var i = 0; i < 5; i++) await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);

        // Knife (slot 0, clientIndex 2) was never touched by the hole/reuse sequence - unequip
        // must still succeed against the SAME durable row it always referenced.
        var unequipPacket = new byte[PacketConstants.IroCzReqTakeoffEquipLength];
        BinaryPrimitives.WriteInt16LittleEndian(unequipPacket, PacketConstants.IroCzReqTakeoffEquip);
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPacket.AsSpan(2), 2);
        await stream.WriteAsync(unequipPacket);

        await ReadExact(stream, 15); // 0x01D7 appearance refresh (unarmed)
        var unequipAck = await ReadExact(stream, PacketConstants.IroZcReqTakeoffEquipAckLength);
        Assert.Equal((short)PacketConstants.IroZcReqTakeoffEquipAck, BinaryPrimitives.ReadInt16LittleEndian(unequipAck));
        Assert.Null(session.Equipment!.RightHandItemId);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Proves that after opening a First Aid Box, the newly modeled item types it grants
    // (HealingItemDefinition: BluePotion/FreshMilk, DelayConsumeItemDefinition: NoviceMagnifier)
    // survive a reconnect/full inventory serialization (0x0B08/0x0B09/0x0B0B, the SAME
    // SendSelfInventoryAsync path a real reconnect exercises) without throwing, with the correct
    // pinned item_type byte for HealingItemDefinition (IT_HEALING=0), and with the current-iRO
    // client-facing IT_USABLE=2 byte for DelayConsumeItemDefinition (NoviceMagnifier) - NOT
    // pinned rAthena's own server-side IT_DELAYCONSUME=11, which a live stock-iRO test proved
    // places the item under the client's Gear tab instead of Items (see
    // IroInventoryListPackets.ItemType's own doc comment for the full capture/live-test trace).
    // This never throws the NotSupportedException ItemType() previously threw for these two types.
    [Fact]
    public async Task ReconnectFullInventorySerialization_HealingAndDelayConsumeItemsSerializeWithCorrectItemType()
    {
        var reconnectInventory = new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 11518, 10, 0, true, 0, 0, 0), // BluePotion (Healing)
            new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, 11614, 20, 0, true, 0, 0, 0), // FreshMilk (Healing)
            new CharacterInventoryItem(DurableId: 3, SlotIndex: 2, 12325, 15, 0, true, 0, 0, 0), // NoviceMagnifier (DelayConsume)
        ]);
        var (client, stream, session, run, persistence) = await SetupAsync(reconnectInventory);
        using var _ = client;

        // Trigger the SAME full inventory-list burst a reconnect sends (0x007D map-loaded).
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });

        await ReadExact(stream, 15); // 0x01D7 self weapon look (unarmed - nothing equipped)
        await ReadExact(stream, 6); // 0x0B08 inventoryStart
        var normalList = await ReadDynamic(stream); // 0x0B09 normal list (all three rows - none is equippable)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd (no 0x0B39 - nothing equippable)

        Assert.Equal((short)0x0b09, BinaryPrimitives.ReadInt16LittleEndian(normalList));
        const int NormalItemLength = 34;
        const int NormalListHeaderLength = 5;
        Assert.Equal(NormalListHeaderLength + 3 * NormalItemLength, normalList.Length);

        var entries = new (uint ItemId, byte ItemType)[3];
        for (var i = 0; i < 3; i++)
        {
            var entry = normalList.AsSpan(NormalListHeaderLength + i * NormalItemLength);
            entries[i] = (BinaryPrimitives.ReadUInt32LittleEndian(entry[2..]), entry[6]);
        }

        Assert.Contains(entries, e => e.ItemId == 11518 && e.ItemType == 0);  // BluePotion, IT_HEALING
        Assert.Contains(entries, e => e.ItemId == 11614 && e.ItemType == 0);  // FreshMilk, IT_HEALING
        Assert.Contains(entries, e => e.ItemId == 12325 && e.ItemType == 2); // NoviceMagnifier, current-iRO client-facing IT_USABLE

        Assert.Equal(3, session.Inventory!.Items.Count); // Full reload succeeded - nothing dropped.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_WrongAccountId_RejectsWithoutMutation()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(4, accountId: AccountId + 1)); // client-claimed account does not match session

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b }); // ping probe
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Empty(persistence.Requests);
        Assert.Equal(3, session.Inventory!.Items.Count);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_InvalidSlot_RejectsWithoutMutation()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(99, AccountId)); // slotIndex 97 - no such row

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Empty(persistence.Requests);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_NonUsableItem_RejectsWithoutMutation()
    {
        // clientIndex 2 -> slotIndex 0 -> Knife (a weapon, not usable).
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Empty(persistence.Requests);
        Assert.Equal(1201, session.Equipment!.RightHandItemId); // Knife untouched.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_ItemWithNoModeledUseEffect_RejectsWithoutMutation()
    {
        // A Usable item with no Grants (no source-backed effect implemented) must be rejected,
        // not silently treated as a no-op success.
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 22542, 1, 0, true, 0, 0, 0)]); // Concentration Potion
        var (client, stream, session, run, persistence) = await SetupAsync(inventory);
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Empty(persistence.Requests);
        Assert.Single(session.Inventory!.Items);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_ConsumePersistenceFailure_DoesNotMutateRuntimeState_SendsNoAck()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;
        persistence.DropRequests = 100; // CharServer never confirms the operation

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId));

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next)); // No ack/grant packets were sent.

        Assert.Equal(3, session.Inventory!.Items.Count); // Runtime snapshot untouched.
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 23484);
        Assert.Empty(persistence.Applied); // Neither the consume nor any grant was applied.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_FramingLeavesNextPacketCorrectlyFramed()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;
        persistence.DropRequests = 100; // Take the short-circuit path to isolate framing.

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId));
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b }); // ping - must be read as an independent, correctly-framed packet.
        var next = await ReadExact(stream, 2);

        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
