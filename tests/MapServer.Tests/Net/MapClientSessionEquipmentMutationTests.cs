using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.Net;

// Wire-level integration test using MapClientSession's real socket path (RunAsync/
// HandlePacketAsync) and the real CharacterEquipmentMutationService - only CharServer
// persistence is a test double, matching the established pattern in
// MapClientSessionMonsterCombatTests.
public sealed class MapClientSessionEquipmentMutationTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    // Verified stock-iRO wire length (ai/iro-2026-wire.md, "Verified equip/unequip request
    // framing"): one opaque trailing byte beyond the pinned rAthena 8-byte shape.
    private static byte[] EquipRequestPacket(ushort clientIndex, uint position)
    {
        var packet = new byte[PacketConstants.IroCzReqWearEquipLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzReqWearEquip);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), position);
        return packet;
    }

    // Verified stock-iRO wire length: one opaque trailing byte beyond the pinned rAthena
    // 4-byte shape.
    private static byte[] UnequipRequestPacket(ushort clientIndex)
    {
        var packet = new byte[PacketConstants.IroCzReqTakeoffEquipLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzReqTakeoffEquip);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, InMemoryInventoryOperations Persistence)> SetupAsync(
        CharacterInventorySnapshot initialInventory, ushort jobClass = 0 /* JOB_NOVICE */, ICharacterGameplayStatePersistence? gameplayPersistence = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var gameplayState = new CharacterGameplayState(CharId, 1, jobClass, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        gameplayPersistence ??= new StubGameplayStatePersistence(gameplayState);
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
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);

        return (client, stream, session, run, inventoryPersistence);
    }

    private sealed class StubGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == AccountId && c == CharId ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(null);
    }

    private sealed class AcceptingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public volatile CharacterGameplayState State = state;
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(State);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) =>
            Task.FromResult<CharacterGameplayState?>(State = u with { Version = e.Version + 1 });
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    // Pinned status_calc_pc / status_calc_maxsp_pc: 2607 Clip `bonus bMaxSP,10;` raises the persisted maximum on equip and
    // takes it away on unequip; the current value is never raised by the maximum (Novice lvl 1, INT 1: 11 SP bare).
    [Fact]
    public async Task EquipAndUnequipClip_RecalculatesAndPersistsMaxSp_WithoutHealing()
    {
        var gameplay = new AcceptingGameplayStatePersistence(new(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 2607, 1, 0, true, 0, 0, 0)]);
        var (client, _, session, run, _) = await SetupAsync(initial, gameplayPersistence: gameplay);
        using var _client = client;

        await client.GetStream().WriteAsync(EquipRequestPacket(2, 0x000008));
        await WaitForAsync(() => gameplay.State.MaxSp == 21);
        Assert.Equal((40u, 40u, 11u), (gameplay.State.MaxHp, gameplay.State.CurrentHp, gameplay.State.CurrentSp));

        await client.GetStream().WriteAsync(UnequipRequestPacket(2));
        await WaitForAsync(() => gameplay.State.MaxSp == 11);
        Assert.Equal(11u, session.GameplayState!.State.MaxSp);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A stored maximum that no longer matches the pinned formula (e.g. an item's bonus was lost while offline) is rebuilt at
    // login and the current value clamped down to it, like pinned status_calc_pc on load.
    [Fact]
    public async Task Login_RebuildsAStaleStoredMaximum_AndClampsTheCurrentValue()
    {
        var gameplay = new AcceptingGameplayStatePersistence(new(CharId, 1, 0, 1, 1, 0, 0, 99, 30, 99, 30, 48, 0, 1, 1, 1, 1, 1, 1));
        var (client, _, _, run, _) = await SetupAsync(new CharacterInventorySnapshot([]), gameplayPersistence: gameplay);
        using var _client = client;

        await WaitForAsync(() => gameplay.State.MaxHp == 40);
        Assert.Equal((40u, 11u, 11u), (gameplay.State.CurrentHp, gameplay.State.MaxSp, gameplay.State.CurrentSp));

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnequipEquippedKnife_PersistsAppearanceUpdatesAndAcks()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;

        await stream.WriteAsync(UnequipRequestPacket(2)); // client_index = slotIndex(0) + 2

        // Pinned ordering: appearance (0x01D7) sent BEFORE the unequip ack.
        var appearance = await ReadExact(stream, 15);
        Assert.Equal((short)0x01d7, BinaryPrimitives.ReadInt16LittleEndian(appearance));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(7))); // unarmed

        var ack = await ReadExact(stream, 9);
        Assert.Equal((short)0x099a, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2)));
        Assert.Equal(0x000002u, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4)));
        Assert.Equal((byte)0, ack[8]); // success, inverted flag

        Assert.Single(persistence.EquipUpdates);
        Assert.Equal((1u, 0u), persistence.EquipUpdates[0]);
        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EquipUnequippedKnife_PersistsAppearanceUpdatesAndAcks()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;

        await stream.WriteAsync(EquipRequestPacket(2, 0x000002));

        // Pinned ordering: ack (0x0999) sent BEFORE the appearance update.
        var ack = await ReadExact(stream, 11);
        Assert.Equal((short)0x0999, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2)));
        Assert.Equal(0x000002u, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4)));
        Assert.Equal((byte)0, ack[10]); // EquipAckResultOk, NOT inverted

        var appearance = await ReadExact(stream, 15);
        Assert.Equal((short)0x01d7, BinaryPrimitives.ReadInt16LittleEndian(appearance));
        Assert.Equal(1201u, BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(7))); // Knife.ClientViewId

        Assert.Single(persistence.EquipUpdates);
        Assert.Equal((1u, 0x000002u), persistence.EquipUpdates[0]);
        Assert.Equal(1201, session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // S4 equipment eligibility (pinned pc_isequip, pc.cpp:1882-1883): a real generated item (1101
    // Sword, EquipLevelMin: 2) requested by a character below that level must be rejected with the
    // FailLevel ack (0x0999 result byte 1), never persisted - proven through the REAL 0x0998 wire
    // path, not just the service layer (CharacterEquipmentMutationServiceTests already covers that).
    [Fact]
    public async Task EquipRealItemBelowItsEquipLevelMin_SendsFailLevelAck_NeverPersists()
    {
        const int sword = 1101; // EquipLevelMin: 2, pinned db/re/item_db_equip.yml.
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, sword, 1, 0, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial); // BaseLevel=1 in this fixture's own gameplay state.
        using var _ = client;

        await stream.WriteAsync(EquipRequestPacket(2, 0x000002));

        var ack = await ReadExact(stream, 11);
        Assert.Equal((short)0x0999, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal(PacketConstants.EquipAckResultFailLevel, ack[10]);

        Assert.Empty(persistence.EquipUpdates);
        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // S4 job-class equipment eligibility (pinned pc_job_can_use_item, pc.cpp:1771-1790): a real
    // generated item (530040 1LvJavelin, Jobs: Crusader/Knight/Swordman only) requested by a Novice
    // job class must be rejected with the plain Fail ack (0x0999 result byte 2 - not FailLevel,
    // since job-class and level are independent pinned gates), never persisted - proven through the
    // REAL 0x0998 wire path, not just the service layer.
    [Fact]
    public async Task EquipRealItemWithIneligibleJobClass_SendsFailAck_NeverPersists()
    {
        const int javelin = 530040; // Jobs: Crusader/Knight/Swordman only; no EquipLevelMin (isolates job-class from the level gate).
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, javelin, 1, 0, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial, jobClass: 0 /* JOB_NOVICE */);
        using var _ = client;

        await stream.WriteAsync(EquipRequestPacket(2, 0x000002));

        var ack = await ReadExact(stream, 11);
        Assert.Equal((short)0x0999, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal(PacketConstants.EquipAckResultFail, ack[10]);

        Assert.Empty(persistence.EquipUpdates);
        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Defect 4 regression: live monster-vs-player combat must reflect a REFINED armor's DEF
    // contribution, not just its plain Defense field. Pinned status.cpp:4096
    // `base_status->def += (refinedef+50)/100` - PlayerCombatSnapshot.HardDefense must be
    // IStatRules.CalculateDerivedStats's OWN HardDefense result (which includes this), never
    // EquipmentStats.HardDefense directly (armor DEF alone, pre-refine). Real pinned item
    // Cotton_Shirt (2301, Defense: 10, ArmorLevel: 1) refined to +3: pinned Armor Level 1 Refine 3
    // Bonus=300 (direct inspection of db/re/refine.yml), so HardDefense must be 10 + (300+50)/100 =
    // 10+3 = 13, proven through the REAL TryGetCombatSnapshotAsync path the monster-attack pipeline
    // actually calls - not just CalculateDerivedStats in isolation.
    [Fact]
    public async Task RefinedArmor_IncreasesLiveCombatSnapshotHardDefense()
    {
        const int cottonShirt = 2301; // Defense: 10, ArmorLevel: 1, Refineable (no explicit field, defaults true).
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, cottonShirt, 1, 0, true, Refine: 3, 0, 0)]);
        var (client, stream, session, run, _) = await SetupAsync(initial);
        using var _2 = client;

        await stream.WriteAsync(EquipRequestPacket(2, 0x000010)); // EQP_ARMOR.
        var ack = await ReadExact(stream, 11);
        Assert.Equal(PacketConstants.EquipAckResultOk, ack[10]);
        var appearance = await ReadExact(stream, 15);
        Assert.Equal((short)0x01d7, BinaryPrimitives.ReadInt16LittleEndian(appearance));

        var snapshot = await session.TryGetCombatSnapshotAsync(CancellationToken.None);

        // Pinned Armor Level 1 Refine 3 Bonus=300 (direct inspection of db/re/refine.yml, not
        // re-derived from the generated table under test): HardDefense = 10 + (300+50)/100 = 13.
        // Before the Defect 4 fix this read equipmentStats.HardDefense directly (= 10, the refine
        // bonus silently dropped from the live monster-vs-player damage path).
        Assert.NotNull(snapshot);
        Assert.Equal(10 + (300 + 50) / 100, snapshot!.Value.HardDefense);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A2 end-to-end regression: before the A1 MATK fix, CalculateDerivedStats threw NotSupportedException
    // for ANY equipped weapon with nonzero MagicAttack (the old - wrong - "players have no weapon-matk
    // term" conclusion treated a real stat as unmodeled). CanResolveAfter validated only
    // TryResolveEquipment, which does NOT inspect MagicAttack at all, so such a weapon could equip
    // successfully (ack OK, persisted) and then crash the very next TryGetCombatSnapshotAsync call - the
    // exact "successful resolution but unsafe downstream calculation" gap the correctness review raised.
    // Real pinned item Rod (1601: Novice-equippable, EquipLevelMin 1, no script, Attack 15, MagicAttack
    // 30, Refineable) proves both that the equip now succeeds AND that the full live combat-snapshot
    // path completes without throwing.
    [Fact]
    public async Task EquipWeaponWithNonzeroMagicAttack_SucceedsAndCombatSnapshotDoesNotThrow()
    {
        const int rod = 1601;
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, rod, 1, 0, true, Refine: 0, 0, 0)]);
        var (client, stream, session, run, _) = await SetupAsync(initial);
        using var _2 = client;

        await stream.WriteAsync(EquipRequestPacket(2, 0x000002)); // EQP_HAND_R.
        var ack = await ReadExact(stream, 11);
        Assert.Equal(PacketConstants.EquipAckResultOk, ack[10]);
        await ReadExact(stream, 15); // 0x01D7 self weapon-look appearance update (equipping a weapon slot).

        var exception = await Record.ExceptionAsync(() => session.TryGetCombatSnapshotAsync(CancellationToken.None));
        Assert.Null(exception);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Defect 3 regression: an item whose script TryResolveEquipment cannot fully account for must
    // be rejected AT EQUIP TIME (CharacterEquipmentMutationService.CanResolveAfter), never persisted
    // as a "successful" equip that later makes the player untargetable to every monster. A Clip
    // (2607, every job, no EquipLevelMin, `bonus bMaxSP,10;`) carrying Vitata Card (4053:
    // `skill "AL_HEAL",1; bonus bUseSPrate,25;` - the skill grant is Feature 18) passes every OTHER
    // eligibility gate and must still be refused.
    [Fact]
    public async Task EquipRealItemWithUnresolvableScript_SendsFailAck_NeverPersists()
    {
        const int clip = 2607, vitataCard = 4053;
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, clip, 1, 0, true, 0, 0, 0,
            ItemInstanceDetail.Empty with { Card0 = vitataCard })]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;

        await stream.WriteAsync(EquipRequestPacket(2, 0x000008));

        var ack = await ReadExact(stream, 11);
        Assert.Equal((short)0x0999, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal(PacketConstants.EquipAckResultFail, ack[10]);

        Assert.Empty(persistence.EquipUpdates);
        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Defect 3 invariant: an otherwise-valid player must never become untargetable to every monster
    // because of a PRE-EXISTING unsupported equip state (one persisted before CanResolveAfter
    // existed, or restored directly in the database - CanResolveAfter makes this unreachable for any
    // NEW equip going forward, but cannot retroactively fix already-persisted data).
    // TryGetCombatSnapshotAsync must degrade gracefully (no equipment DEF/weapon contribution, loudly
    // diagnosed) rather than returning null and silently excluding the player from every monster's
    // valid-target set forever.
    [Fact]
    public async Task PreExistingUnresolvableEquippedItem_StillProducesATargetableCombatSnapshot()
    {
        // Cutlas (1135): `skill "SM_BASH",5; bonus bStr,2; bonus bDef,1;` - the skill grant is Feature 18, so the row is
        // unresolvable; resolved, its bDef would make HardDefense 1. Equip bits set directly in the INITIAL snapshot, bypassing
        // CharacterEquipmentMutationService entirely - data already equipped before the CanResolveAfter gate existed.
        const int cutlas = 1135;
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, cutlas, 1, 0x000002, true, 0, 0, 0)]);
        var (client, stream, session, run, _) = await SetupAsync(initial);
        using var _2 = client;

        var snapshot = await session.TryGetCombatSnapshotAsync(CancellationToken.None);

        Assert.NotNull(snapshot); // never untargetable because of this pre-existing condition.
        Assert.Equal(0, snapshot!.Value.HardDefense); // degraded: no equipment contribution, not fabricated either.

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnequipAlreadyUnequippedItem_SendsFailureAckWithoutMutatingState()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;

        await stream.WriteAsync(UnequipRequestPacket(2));

        var ack = await ReadExact(stream, 9);
        Assert.Equal((short)0x099a, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((byte)1, ack[8]); // failure, inverted flag

        Assert.Empty(persistence.EquipUpdates);
        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EquipPersistenceFailure_SendsFailureAckWithoutMutatingState()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;
        persistence.DropRequests = 100; // CharServer never answers: the equip is never confirmed

        await stream.WriteAsync(EquipRequestPacket(2, 0x000002));

        var ack = await ReadExact(stream, 11);
        Assert.Equal((byte)2, ack[10]); // EquipAckResultFail

        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
