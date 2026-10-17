using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.Shared.Inventory;
using MapServer.Tests.Testing;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

// Packet-level generated-script integration tests for the real pinned Sailor#intro_npc04
// (legacy/rathena/npc/re/jobs/novice/academy.txt:334-379), mirroring
// GeneratedLuminIntegrationTests/GeneratedCaptainCaroccIntegrationTests exactly: the real
// GeneratedScriptRegistry entity, a real MapClientSession over a real socket pair, and only
// quest/gameplay/inventory persistence as test doubles. Uses server-owned fixture inventory state
// only - never a captured actor ID and never the capture's own reward item (12325); the reward is
// pinned rAthena's real item 611 throughout, per this task's explicit discrepancy policy.
public sealed class GeneratedSailorIntegrationTests
{
    private const string EntityId = "npc:int_land:sailor#intro_npc04";
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const uint Quest21008 = 21008;
    private const int WoodItemId = 6008;
    private const int MagnifierItemId = 611;

    private sealed class RecordingQuestPersistence(CharacterQuestStatus initialState) : ICharacterQuestPersistence
    {
        public CharacterQuestStatus State { get; private set; } = initialState;
        public List<CharacterQuestStatus> Mutations { get; } = [];
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken) =>
            Task.FromResult<CharacterQuestStatus?>(questId == Quest21008 ? State : CharacterQuestStatus.Absent);
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken)
        {
            Assert.Equal(Quest21008, questId);
            State = state;
            Mutations.Add(state);
            return Task.FromResult(true);
        }
    }

    private sealed class FixedGameplayStatePersistence : ICharacterGameplayStatePersistence
    {
        // BaseExperience/JobExperience start at 0 so the granted 100/100 EXP is directly observable.
        private static readonly CharacterGameplayState State = new(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(State);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static byte[] ActorPacket(short type, uint id, int length) { var packet = new byte[length]; BinaryPrimitives.WriteInt16LittleEndian(packet, type); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), id); packet[^1] = 0xaa; return packet; }
    private static async Task<byte[]> ReadExact(Stream stream, int length) { var data = new byte[length]; await stream.ReadExactlyAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); return data; }
    private static async Task<byte[]> ReadDynamic(Stream stream) { var header = await ReadExact(stream, 4); var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)); return [.. header, .. await ReadExact(stream, length - 4)]; }
    private static string Message(byte[] packet) => System.Text.Encoding.ASCII.GetString(packet.AsSpan(8));
    private static void AssertNext(byte[] packet) => Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(packet));
    private static void AssertClose(byte[] packet) => Assert.Equal((short)0x00b6, BinaryPrimitives.ReadInt16LittleEndian(packet));

    private sealed class SailorFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly MapClientSession _session;
        private readonly Task _run;

        private SailorFixture(TcpListener listener, TcpClient client, MapClientSession session, Task run, NetworkStream stream, uint actorId,
            RecordingQuestPersistence questPersistence, InMemoryInventoryOperations inventory)
        {
            _listener = listener; _client = client; _session = session; _run = run; Stream = stream; ActorId = actorId;
            QuestPersistence = questPersistence; Inventory = inventory;
        }

        public NetworkStream Stream { get; }
        public uint ActorId { get; }
        public RecordingQuestPersistence QuestPersistence { get; }
        // The CharServer stand-in: real operation rules over in-memory rows, with failure hooks.
        public InMemoryInventoryOperations Inventory { get; }
        public MapClientSession Session => _session;

        public static Task<SailorFixture> StartAsync(CharacterQuestStatus questState, CharacterInventorySnapshot initialInventory) =>
            StartAsync(questState, initialInventory, configureInventory: null);

        // configureInventory lets a test make the CharServer stand-in unreachable for selected operations (delitem removals / getitem
        // grants), while every other fixture wiring (quest/gameplay persistence, real MapClientSession over a real socket pair) stays
        // identical to the happy-path tests.
        public static async Task<SailorFixture> StartAsync(
            CharacterQuestStatus questState,
            CharacterInventorySnapshot initialInventory,
            Action<InMemoryInventoryOperations>? configureInventory)
        {
            var entity = Assert.Single(GeneratedScriptRegistry.Entities, item => item.Id == EntityId);
            Assert.Equal(new WorldActorComponent("Sailor#intro_npc04", "int_land", 58, 69, 5, 100, 0), entity.Actor);
            var registry = new WorldMapRegistry([], [entity]);
            var actor = Assert.Single(registry.GetVisibleWarpActors("int_land", 58, 69));
            Assert.True(registry.TryGetInteraction(actor.ActorId, "int_land", out _, out _));

            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var serverClient = await listener.AcceptTcpClientAsync(); await connect;

            var questPersistence = new RecordingQuestPersistence(questState);
            var inventory = new InMemoryInventoryOperations(initialInventory);
            configureInventory?.Invoke(inventory);

            var session = new MapClientSession(1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true, "int_land", 58, 69, registry,
                questPersistence: questPersistence, accountId: AccountId, charId: CharId, gameplayStatePersistence: new FixedGameplayStatePersistence(),
                inventoryOperations: inventory);
            var run = session.RunAsync(CancellationToken.None);
            await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land", 58, 69, 5, 0, 0));
            var stream = client.GetStream();
            await ReadExact(stream, 29); // authenticated iRO bootstrap
            var skillListHeader = await ReadExact(stream, 4); // 0x0B32 header
            await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4); // 0x0B32 body
            return new(listener, client, session, run, stream, actor.ActorId, questPersistence, inventory);
        }

        public async Task LoadAndAssertSpawnAsync()
        {
            await Stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
            await ReadExact(Stream, 15); // self weapon appearance
            var inventoryStart = await ReadExact(Stream, 6); // 0x0B08 inventory start
            Assert.Equal((short)0x0b08, BinaryPrimitives.ReadInt16LittleEndian(inventoryStart));
            // A non-empty stackable-item fixture (this test's preloaded Wood row) sends one
            // dynamic-length 0x0B09 normal-item-list packet before the fixed 4-byte 0x0B0B
            // inventory-end packet (pinned clif_inventorylist ordering, see MapClientSession's own
            // SendInventoryAndEquipListAsync doc comment); an empty fixture sends 0x0B0B directly.
            var idPeek = await ReadExact(Stream, 2);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(idPeek);
            if (opcode == 0x0b09)
            {
                var lengthBytes = await ReadExact(Stream, 2);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
                await ReadExact(Stream, length - 4);
                await ReadExact(Stream, 4); // 0x0B0B inventory end
            }
            else
            {
                Assert.Equal((short)0x0b0b, opcode);
                await ReadExact(Stream, 2); // remaining 0x0B0B bytes (type/flag)
            }

            var spawn = await ReadDynamic(Stream);
            Assert.Equal(ActorId, BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5)));
            Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(spawn.AsSpan(23)));
        }

        public async Task WaitForScriptCompletionAsync()
        {
            for (var attempt = 0; attempt < 100 && _session.ActiveGeneratedScriptEntityId is not null; attempt++) await Task.Delay(10);
            Assert.Null(_session.ActiveGeneratedScriptEntityId);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Close();
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
            await _session.DisposeAsync();
            _listener.Stop();
        }
    }

    // Branch 1: quest 21008 Active, Wood count < 2 -> insufficient-material dialogue, closes, no
    // item removal, no reward, no completequest, no EXP.
    [Fact]
    public async Task QuestActive_InsufficientWood_ShowsInsufficientDialogueAndMutatesNothing()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, WoodItemId, 1, 0, true, 0, 0, 0)]);
        await using var fixture = await SailorFixture.StartAsync(CharacterQuestStatus.Active, inventory);

        await fixture.LoadAndAssertSpawnAsync();
        await fixture.Stream.WriteAsync(ActorPacket(0x0090, fixture.ActorId, 8));

        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("These Porings stole my materials to repair the ship. Can you help me to get them back?\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("There are plenty of Porings on this island.\0", Message(await ReadDynamic(fixture.Stream)));
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("I would really appreciate it if you help me.\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("I am not allowed to set foot on this island.\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("That's why I ask for your help~\0", Message(await ReadDynamic(fixture.Stream)));
        AssertClose(await ReadExact(fixture.Stream, 6));
        await fixture.Stream.WriteAsync(ActorPacket(0x0146, fixture.ActorId, 7));
        await fixture.WaitForScriptCompletionAsync();

        Assert.Empty(fixture.Inventory.Requests);
        Assert.Empty(fixture.QuestPersistence.Mutations);
        Assert.Equal(1u, fixture.Session.Inventory!.Items.Single(i => i.ItemId == WoodItemId).Amount);
    }

    // Branch 2: quest 21008 Active, Wood count >= 2 -> success dialogue, exactly 2x Wood(6008)
    // consumed, exactly 100 base + 100 job EXP granted, exactly 5x Magnifier(611) granted, quest
    // 21008 becomes Completed, correct stock-iRO sync, later Magnifier dialogue continues/closes.
    [Fact]
    public async Task QuestActive_SufficientWood_CompletesQuestConsumesWoodAndGrantsMagnifiers()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, WoodItemId, 2, 0, true, 0, 0, 0)]);
        await using var fixture = await SailorFixture.StartAsync(CharacterQuestStatus.Active, inventory);

        await fixture.LoadAndAssertSpawnAsync();
        await fixture.Stream.WriteAsync(ActorPacket(0x0090, fixture.ActorId, 8));

        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Unbelievable, perfect! Any chance you want to join my crew?\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Enough talking!!\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Come on, we're ready to set sail!\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Thank you so much!\0", Message(await ReadDynamic(fixture.Stream)));
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("If you want to sail with us to Izlude, jump on board!\0", Message(await ReadDynamic(fixture.Stream)));

        // delitem 6008,2 -> ZC_DELETE_ITEM_FROM_BODY (0x07FA, sailor-packet-export.txt frame 7291)
        // is sent immediately, before the getexp burst - matching the generated script's own
        // statement order (delitem -> getexp -> getitem -> completequest). clientIndex is derived
        // from the fixture's own single preloaded Wood row (SlotIndex 0 -> clientIndex 2), never
        // hardcoded to the capture's own clientIndex=4.
        var deleteItem = await ReadExact(fixture.Stream, 8);
        Assert.Equal((short)0x07fa, BinaryPrimitives.ReadInt16LittleEndian(deleteItem));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(deleteItem.AsSpan(2))); // deleteReason
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(deleteItem.AsSpan(4))); // clientIndex = SlotIndex(0) + 2
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(deleteItem.AsSpan(6))); // amount consumed

        // getexp 100,100 -> a variable-length burst of 0x00B0 (8-byte)/0x0ACB (12-byte) parameter
        // packets (IroCharacterProgressionPackets.Build - exact count/fields depend on whether a
        // base/job level-up was also crossed, which is not this test's concern), read generically
        // until the getitem reward's 0x0B41 pickup-ack arrives.
        byte[] pickup;
        while (true)
        {
            var opcodeBytes = await ReadExact(fixture.Stream, 2);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(opcodeBytes);
            if (opcode == (short)0x0b41) { pickup = [.. opcodeBytes, .. await ReadExact(fixture.Stream, 68)]; break; }
            var remaining = opcode switch
            {
                0x00b0 => 6,
                0x0acb => 10,
                0x0acc => PacketConstants.ZcNotifyExperienceLength - 2,
                0x019b => PacketConstants.ZcNotifyEffectLength - 2,
                _ => -1,
            };
            if (remaining < 0) Assert.Fail($"Unexpected packet 0x{opcode:x4} while draining the getexp burst.");
            await ReadExact(fixture.Stream, remaining);
        }

        // getitem 611,5 -> reuses the existing 0x0B41 ZC_ITEM_PICKUP_ACK generation. Occurs after
        // the 0x07FA delitem packet above and before the completequest 0x02B4 packet below,
        // preserving the pinned delitem -> getexp -> getitem -> completequest ordering.
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(4)));
        Assert.Equal(MagnifierItemId, BinaryPrimitives.ReadInt32LittleEndian(pickup.AsSpan(6)));

        // completequest(21008) also sends the pinned quest-log removal packet (0x02B4, 6 bytes) -
        // matching pinned quest completion's "persists Completed server-side but removes from the
        // client's visible quest log" behavior already documented for Lumin's own quest 7471.
        var removeQuest = await ReadExact(fixture.Stream, 6);
        Assert.Equal((short)0x02b4, BinaryPrimitives.ReadInt16LittleEndian(removeQuest));
        Assert.Equal(Quest21008, BinaryPrimitives.ReadUInt32LittleEndian(removeQuest.AsSpan(2)));

        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Oh, and take these ^4d4dffMagnifiers^000000.\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("When you hunt monsters you can find ^4d4dffunidentified items^000000.\0", Message(await ReadDynamic(fixture.Stream)));
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Using a Magnifier will identify the item.\0", Message(await ReadDynamic(fixture.Stream)));
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Maybe you already have some unidentified items from your fights with those Porings?\0", Message(await ReadDynamic(fixture.Stream)));
        AssertClose(await ReadExact(fixture.Stream, 6));
        await fixture.Stream.WriteAsync(ActorPacket(0x0146, fixture.ActorId, 7));
        await fixture.WaitForScriptCompletionAsync();

        Assert.Equal([CharacterQuestStatus.Completed], fixture.QuestPersistence.Mutations);
        Assert.Single(fixture.Inventory.Applied, request => request.Mutations.OfType<RemoveFromRowMutation>().Any(remove => remove.Amount == 2));
        Assert.Single(fixture.Inventory.Applied, request => request.Mutations.OfType<AddNewRowMutation>().Any(add => add.Row.ItemId == MagnifierItemId && add.Row.Amount == 5));
        Assert.DoesNotContain(fixture.Session.Inventory!.Items, i => i.ItemId == WoodItemId); // Exactly 2 consumed from a 2-stack -> row deleted.
        Assert.Equal(5u, fixture.Session.Inventory!.Items.Single(i => i.ItemId == MagnifierItemId).Amount);
    }

    // Gap 2, scenario A: sufficient Wood, but the delitem CharServer consume itself fails
    // (injected FailingInventoryPersistence). ScriptContext.DeleteItemAsync must throw
    // ScriptMutationFailedException, stopping the remaining generated statement sequence entirely
    // before getexp/getitem/completequest ever run - no fake success reaches the client.
    [Fact]
    public async Task QuestActive_SufficientWood_DelitemPersistenceFailure_StopsBeforeAnyReward()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, WoodItemId, 2, 0, true, 0, 0, 0)]);
        await using var fixture = await SailorFixture.StartAsync(
            CharacterQuestStatus.Active,
            inventory,
            inventory => inventory.DropWhen = request => request.Mutations.OfType<RemoveFromRowMutation>().Any());

        await fixture.LoadAndAssertSpawnAsync();
        await fixture.Stream.WriteAsync(ActorPacket(0x0090, fixture.ActorId, 8));

        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Unbelievable, perfect! Any chance you want to join my crew?\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Enough talking!!\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Come on, we're ready to set sail!\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Thank you so much!\0", Message(await ReadDynamic(fixture.Stream)));
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("If you want to sail with us to Izlude, jump on board!\0", Message(await ReadDynamic(fixture.Stream)));

        // delitem fails: no 0x07FA, and the script-dispatch catch site's minimal client-side close
        // (matching the existing generic-exception precedent) is the only thing that follows -
        // never the success-branch's later NextAsync/Magnifier dialogue/completequest packets.
        AssertClose(await ReadExact(fixture.Stream, 6));
        await fixture.WaitForScriptCompletionAsync();

        Assert.Single(fixture.Inventory.Requests.Select(request => request.OperationId).Distinct()); // one removal attempted (retried), never applied
        Assert.Empty(fixture.Inventory.Applied);
        Assert.DoesNotContain(fixture.Inventory.Requests, request => request.Mutations.Any(mutation => mutation is AddNewRowMutation)); // getitem never ran
        Assert.Empty(fixture.QuestPersistence.Mutations); // completequest never ran
        Assert.Equal(2u, fixture.Session.Inventory!.Items.Single(i => i.ItemId == WoodItemId).Amount); // unchanged
        Assert.DoesNotContain(fixture.Session.Inventory!.Items, i => i.ItemId == MagnifierItemId); // no Magnifier granted
    }

    // Gap 2, scenario B: delitem succeeds, but the getitem CharServer add fails. Already-applied
    // earlier statements (Wood removal, EXP grant) are NOT rolled back (this project's documented
    // "no distributed idempotency" stance) - asserted as an explicit expectation, not merely
    // unchecked - but completequest must never run and no fake Magnifier pickup ack is sent.
    [Fact]
    public async Task QuestActive_SufficientWood_GetitemPersistenceFailure_StopsBeforeCompleteQuest()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, WoodItemId, 2, 0, true, 0, 0, 0)]);
        await using var fixture = await SailorFixture.StartAsync(
            CharacterQuestStatus.Active,
            inventory,
            inventory => inventory.DropWhen = request => request.Mutations.Any(mutation => mutation is AddNewRowMutation or AddToStackMutation));

        await fixture.LoadAndAssertSpawnAsync();
        await fixture.Stream.WriteAsync(ActorPacket(0x0090, fixture.ActorId, 8));

        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Unbelievable, perfect! Any chance you want to join my crew?\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Enough talking!!\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Come on, we're ready to set sail!\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("Thank you so much!\0", Message(await ReadDynamic(fixture.Stream)));
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("If you want to sail with us to Izlude, jump on board!\0", Message(await ReadDynamic(fixture.Stream)));

        // delitem 6008,2 succeeds -> 0x07FA is sent, exactly as the full-success path.
        var deleteItem = await ReadExact(fixture.Stream, 8);
        Assert.Equal((short)0x07fa, BinaryPrimitives.ReadInt16LittleEndian(deleteItem));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(deleteItem.AsSpan(4)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(deleteItem.AsSpan(6)));

        // getexp 100,100 still runs (delitem already succeeded before the failure point) -> drain
        // its 0x00B0/0x0ACB burst, then getitem fails: no 0x0B41, and the script-dispatch catch
        // site's minimal close follows immediately - never completequest's 0x02B4 or the trailing
        // Magnifier dialogue.
        while (true)
        {
            var opcodeBytes = await ReadExact(fixture.Stream, 2);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(opcodeBytes);
            if (opcode == (short)0x00b6) { break; } // ZC_NPC_CLOSE - the script aborted here.
            var remaining = opcode switch
            {
                0x00b0 => 6,
                0x0acb => 10,
                0x0acc => PacketConstants.ZcNotifyExperienceLength - 2,
                0x019b => PacketConstants.ZcNotifyEffectLength - 2,
                _ => -1,
            };
            if (remaining < 0) Assert.Fail($"Unexpected packet 0x{opcode:x4} while draining the getexp burst.");
            await ReadExact(fixture.Stream, remaining);
        }
        await fixture.WaitForScriptCompletionAsync();

        Assert.Single(fixture.Inventory.Applied, request => request.Mutations.OfType<RemoveFromRowMutation>().Any(remove => remove.Amount == 2)); // delitem applied, not rolled back
        Assert.Single(fixture.Inventory.Requests.Where(request => request.Mutations.Any(mutation => mutation is AddNewRowMutation)).Select(request => request.OperationId).Distinct()); // getitem attempted (retried), but never confirmed
        Assert.Empty(fixture.QuestPersistence.Mutations); // completequest never ran
        Assert.DoesNotContain(fixture.Session.Inventory!.Items, i => i.ItemId == WoodItemId); // delitem's removal stands (no rollback)
        Assert.DoesNotContain(fixture.Session.Inventory!.Items, i => i.ItemId == MagnifierItemId); // no fake Magnifier grant
    }

    // Branch 3: quest 21008 NOT Active -> fallback "ship heading to Izlude" branch, no mutation at all.
    [Fact]
    public async Task QuestNotActive_ShowsFallbackDialogue_NoMutationAtAll()
    {
        var inventory = new CharacterInventorySnapshot([]);
        await using var fixture = await SailorFixture.StartAsync(CharacterQuestStatus.Absent, inventory);

        await fixture.LoadAndAssertSpawnAsync();
        await fixture.Stream.WriteAsync(ActorPacket(0x0090, fixture.ActorId, 8));

        Assert.Equal("[Sailor]\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("The ship is heading to Izlude soon~!\0", Message(await ReadDynamic(fixture.Stream)));
        Assert.Equal("I'll let you know when we set sail~!\0", Message(await ReadDynamic(fixture.Stream)));
        AssertClose(await ReadExact(fixture.Stream, 6));
        await fixture.Stream.WriteAsync(ActorPacket(0x0146, fixture.ActorId, 7));
        await fixture.WaitForScriptCompletionAsync();

        Assert.Empty(fixture.Inventory.Requests);
        Assert.Empty(fixture.QuestPersistence.Mutations);
        Assert.Empty(fixture.Session.Inventory!.Items);
    }
}
