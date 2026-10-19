using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
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
        CharacterInventorySnapshot initialInventory, CharacterGameplayState? initialState = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var gameplayState = initialState ?? FullHealthNovice;
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

    // CurrentHp 40/40, CurrentSp 11/11, every base stat 1.
    private static readonly CharacterGameplayState FullHealthNovice = new(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);

    private static CharacterInventorySnapshot Holding(int itemId, uint amount = 2) =>
        new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, itemId, amount, 0, true, 0, 0, 0)]); // clientIndex 2

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

    // Blue_Potion_B_10 (9539, Type: Healing): `getitem 11572,10;` - a getitem-only script, run through the GENERIC
    // ItemScriptEvaluator path (it is not a UsableItemDefinition.Grants container), proving ItemGrantResolver's getitem
    // output reaches the SAME one-durable-operation consume+grant path as a container (architecture decision 1).
    [Fact]
    public async Task UseGetItemScriptItem_ConsumesOneRow_GrantsScriptedItem_SendsCorrectAck()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(Holding(9539, amount: 3));
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId)); // clientIndex 2 -> slotIndex 0 -> Blue_Potion_B_10

        var ack = await ReadExact(stream, 15);
        Assert.Equal((short)0x01c8, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2)));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(8)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(12))); // 3 - 1 remaining
        Assert.Equal((byte)1, ack[14]);

        var pickup = await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
        Assert.Equal((short)PacketConstants.ZcItemPickupAck, BinaryPrimitives.ReadInt16LittleEndian(pickup));
        Assert.Equal((ushort)10, BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(4)));
        Assert.Equal(11572u, BinaryPrimitives.ReadUInt32LittleEndian(pickup.AsSpan(6)));

        // ONE durable operation: the consume and the scripted grant persist together.
        var operation = Assert.Single(persistence.Applied);
        var consume = Assert.IsType<RemoveFromRowMutation>(operation.Mutations[0]);
        Assert.Equal((1u, 1u), (consume.DurableId, consume.Amount));
        var added = Assert.Single(operation.Mutations.OfType<AddNewRowMutation>());
        Assert.Equal(11572u, added.Row.ItemId);
        Assert.Equal(10u, added.Row.Amount);

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
        // A Usable item whose script the evaluator cannot run (pinned 12887 `itemskill "AL_TELEPORT",1;` - skill execution,
        // Feature 18) must be rejected, not consumed for a silently missing effect.
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 12887, 1, 0, true, 0, 0, 0)]);
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

    // Script items now also reach the gate and it is socket-tested with a real restricted potion
    // (UseHealItemRestrictedOnNormalMaps_RejectsWithoutMutation below); this guard stays for containers.
    // Documents a real, honestly-reported scope gap rather than hiding it: the nouse_mapflag gate
    // (HandleIroUseItemRequestAsync, ItemMetadata.RestrictedOnNormalMaps) can only be socket-tested end to end against a
    // REAL pinned item that is BOTH a container with fully-resolvable Grants (the only ItemDefinition shape that reaches
    // the gate today - see the earlier "no source-backed use effect" rejection above it) AND restricted on normal maps
    // (db/re/item_noequip.txt bit 1). No such item exists in the pinned Renewal dataset as of commit
    // e985006171d2eb320ee512a653f4c83aea3d81b6 (every bit-1 row is equipment, a Healing-script potion, or a DelayConsume
    // scroll). This test locks in that fact as a regression guard: if a future data regeneration ever produces such an
    // item, this assertion fails loudly instead of the gate silently going untested. The gate's correctness is instead
    // proven at the data layer (ItemCatalogTests.Catalog_ExposesNoEquipBitOneThroughMeta, against real item 2483) and the
    // full converter pipeline (WorldDataImporter.Tests.ItemPipelineDataDrivenFixtureTests, a synthetic fixture id built
    // only from already-supported mechanics) - MapClientSession has no DI seam to substitute a synthetic ItemCatalog
    // into a live socket test (GeneratedItems.ById reads the process-wide ItemCatalog.Current singleton directly).
    [Fact]
    public void NoRealPinnedContainerItemCurrentlyReachesTheNoUseMapflagGate()
    {
        var reachesGate = GeneratedItems.ById.Values
            .OfType<UsableItemDefinition>()
            .Where(item => item.Grants is { Count: > 0 } grants && grants.All(g => GeneratedItems.ById.ContainsKey(g.ItemId)))
            .Where(item => item.Meta!.RestrictedOnNormalMaps)
            .ToArray();

        Assert.Empty(reachesGate);
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

    private static async Task<byte[]> ReadParameter(Stream stream, ushort expectedType)
    {
        var packet = await ReadExact(stream, 8);
        Assert.Equal(PacketConstants.ZcParameterChange, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(expectedType, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        return packet;
    }

    private static async Task AssertNothingElseSentAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b }); // ping probe: its reply must be the very next packet
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));
    }

    // Pinned 11518 `itemheal 0,5;` at INT 30: pc_itemheal bonus = 100 + 2*30 = 160, 5 * 160 / 100 = 8 SP (pc.cpp:10727-10745).
    // The item is consumed in one durable operation, acked, and the heal goes through the gameplay-state mutation and the
    // 0x00B0 SP_SP update - the same path every other HP/SP change of this session takes.
    [Fact]
    public async Task UseItemHealPotion_ConsumesOne_AppliesPinnedItemHealFormula_SendsSpUpdate()
    {
        var state = FullHealthNovice with { CurrentSp = 2, Intelligence = 30 };
        var (client, stream, session, run, persistence) = await SetupAsync(Holding(11518), state);
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));

        var ack = await ReadExact(stream, 15);
        Assert.Equal((short)0x01c8, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(12))); // one of two left
        Assert.Equal((byte)1, ack[14]);
        var sp = await ReadParameter(stream, 7);
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(sp.AsSpan(4)));
        await AssertNothingElseSentAsync(stream);

        Assert.Equal(10u, session.GameplayState!.State.CurrentSp);
        Assert.Equal(40u, session.GameplayState.State.CurrentHp);
        var operation = Assert.Single(persistence.Applied);
        var consume = Assert.IsType<RemoveFromRowMutation>(Assert.Single(operation.Mutations));
        Assert.Equal((1u, 1u), (consume.DurableId, consume.Amount));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Pinned 607 Yggdrasilberry `percentheal 100,100;` restores the full maximum of both; its Delay (5000 ms, Reuse_Limit_F)
    // then rejects an immediate second use with no consume (pc_itemcd_check), proving the cooldown gate still runs first.
    [Fact]
    public async Task UsePercentHealItem_RestoresMaximum_ThenDelayGroupRejectsImmediateReuse()
    {
        var state = FullHealthNovice with { CurrentHp = 3, CurrentSp = 1 };
        var (client, stream, session, run, persistence) = await SetupAsync(Holding(607), state);
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));
        await ReadExact(stream, 15);
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian((await ReadParameter(stream, 5)).AsSpan(4)));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian((await ReadParameter(stream, 7)).AsSpan(4)));

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));
        await AssertNothingElseSentAsync(stream);
        Assert.Single(persistence.Applied);
        Assert.Equal(1u, session.Inventory!.Items.Single().Amount);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Pinned 23259 `percentheal 0,5; specialeffect2 EF_HEAL3; sc_start SC_BLESSING,240000,5;`: SP heals by apply_rate(11, 5) = 0,
    // which status_percent_change raises to 1; Blessing starts through the session's status system and sends the same
    // capture-proven icon and STR/INT/DEX resync an NPC-started Blessing sends. specialeffect2 projects nothing (no capture).
    [Fact]
    public async Task UseStatusItem_StartsTheStatusThroughTheSharedStatusPath()
    {
        var state = FullHealthNovice with { CurrentSp = 4 };
        var (client, stream, session, run, persistence) = await SetupAsync(Holding(23259), state);
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));
        await ReadExact(stream, 15);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian((await ReadParameter(stream, 7)).AsSpan(4)));
        var icon = await ReadExact(stream, 29);
        Assert.Equal(PacketConstants.ZcMsgStateChange3, BinaryPrimitives.ReadInt16LittleEndian(icon));
        for (var i = 0; i < 3; i++) await ReadExact(stream, 14); // 0x0141 STR/INT/DEX
        await AssertNothingElseSentAsync(stream);

        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var blessing));
        Assert.Equal((5, 5), (blessing.Val1, blessing.Val2));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Pinned clif_parse_UseItem: a dead character's use request changes nothing.
    [Fact]
    public async Task UseHealItem_WhileDead_RejectsWithoutMutation()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(Holding(501), FullHealthNovice with { CurrentHp = 0 });
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));
        await AssertNothingElseSentAsync(stream);

        Assert.Empty(persistence.Requests);
        Assert.Equal(0u, session.GameplayState!.State.CurrentHp);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Pinned 11557 (`itemheal 325,0;`, item_noequip.txt bit 1): the nouse_mapflag gate rejects it on a normal map before any
    // consume or heal - now socket-testable because script items reach the gate.
    [Fact]
    public async Task UseHealItemRestrictedOnNormalMaps_RejectsWithoutMutation()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(Holding(11557), FullHealthNovice with { CurrentHp = 1 });
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));
        await AssertNothingElseSentAsync(stream);

        Assert.Empty(persistence.Requests);
        Assert.Equal(1u, session.GameplayState!.State.CurrentHp);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
