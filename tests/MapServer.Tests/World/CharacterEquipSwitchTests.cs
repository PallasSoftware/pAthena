using Athena.Net.MapServer.Tests.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;
using Athena.Net.MapServer.Tests.Testing;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

// Equip switch (pinned pc_equipitem(..., equipswitch=true), pc_equipswitch, pc_equipswitch_remove,
// SkillEquipSwitch::castendNoDamageId) and the pc_equipitem position rules it shares with a normal equip, over the REAL
// pinned catalog and CharServer's real operation rules (InMemoryInventoryOperations). Expected values come from the pinned
// item scripts/locations quoted next to each fixture, never from this implementation. Client packets (0x0A97-0x0A9D,
// 0x0ACE) are Protocol-pending-capture; these tests prove the server-authoritative mechanic only.
public sealed class CharacterEquipSwitchTests
{
    private static readonly GameplayRuleServices Rules = TestGameplayRules.Services();
    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items = ItemCatalog.Current.ById;

    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const byte Male = 1;

    // Pinned db/re/item_db_equip.yml: Knife 1201 Dagger Right_Hand (Novice/Swordman/Assassin...), no script.
    private const int Knife = 1201;
    // Katana 1116 2hSword Both_Hand, EquipLevelMin 4, Jobs Swordman/Knight/Crusader.
    private const int Katana = 1116;
    // Guard 2101 Left_Hand shield, every job.
    private const int Guard = 2101;
    // Cotton_Shirt 2301 Armor, no script.
    private const int CottonShirt = 2301;
    // Clip 2607 Both_Accessory, 1 slot, every job, `bonus bMaxSP,10;`.
    private const int Clip = 2607;
    // Ring 2601 Both_Accessory, EquipLevelMin 20, Jobs All except Novice/SuperNovice, `bonus bStr,2;`.
    private const int Ring = 2601;
    // Costume Angel Veil 5915 Costume_Head_Top, no script.
    private const int CostumeAngelVeil = 5915;
    // Playing Pere Card 27107 (item_db_etc.yml) Right_Accessory card, `bonus2 bAddEle,Ele_Neutral,20;`.
    private const int PlayingPereCard = 27107;

    private const uint HandR = 0x000002, HandL = 0x000020, Arms = HandR | HandL, ArmorPos = 0x000010;
    private const uint AccR = 0x000008, AccL = 0x000080, Acc = AccR | AccL, CostumeTop = 0x000400;

    private static CharacterInventoryItem Row(uint durableId, int itemId, uint equip = 0, uint equipSwitch = 0, uint card0 = 0) =>
        new(durableId, durableId - 1, itemId, 1, equip, true, 0, 0, 0,
            ItemInstanceDetail.Empty with { EquipSwitch = equipSwitch, Card0 = card0, Revision = 1 });

    private sealed record Fixture(InMemoryInventoryOperations Operations, CharacterInventoryService Inventory, CharacterEquipmentMutationService Service);

    private static Fixture Setup(params CharacterInventoryItem[] rows)
    {
        var snapshot = new CharacterInventorySnapshot(rows);
        var operations = new InMemoryInventoryOperations(snapshot);
        var inventory = TestInventory.Service(AccountId, CharId, operations, snapshot);
        return new(operations, inventory, new CharacterEquipmentMutationService(inventory, Rules.Stats));
    }

    private static EquipSwitchCooldown Cooldown(out ControllableTimeProvider clock)
    {
        clock = new ControllableTimeProvider();
        return new EquipSwitchCooldown(clock);
    }

    private static InventoryItemRow Durable(Fixture f, uint durableId) => f.Operations.Rows.Single(r => r.DurableId == durableId);

    private static (uint Equip, uint Switch) State(Fixture f, uint durableId) => (Durable(f, durableId).Equip, Durable(f, durableId).EquipSwitch);

    private static Task<(EquipOutcome? Outcome, CharacterInventorySnapshot? Updated)> SwitchAll(Fixture f, EquipSwitchCooldown cooldown, JobClass job = JobClass.Novice, uint level = 99) =>
        f.Service.SwitchAllAsync(cooldown, Items, level, Male, job, CancellationToken.None);

    // ---- registration (pc_equipitem with equipswitch=true) ----

    [Fact]
    public async Task AddToEquipSwitch_RegistersAtResolvedPosition_WithoutWearingIt()
    {
        var f = Setup(Row(1, Knife));

        var (outcome, updated) = await f.Service.AddToEquipSwitchAsync(0, HandR, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, HandR), outcome);
        Assert.Equal((0u, HandR), State(f, 1));
        Assert.Equal(HandR, updated!.Items.Single().Details.EquipSwitch);
        Assert.Equal(InventoryOperationKind.EquipSwitch, f.Operations.Applied.Single().Kind);
    }

    [Fact]
    public async Task AddToEquipSwitch_WornRow_FailsWithoutPersisting()
    {
        // Pinned pc.cpp:12063: `sd->inventory.u.items_inventory[n].equip != 0` fails the switch registration too.
        var f = Setup(Row(1, Knife, equip: HandR));

        var (outcome, _) = await f.Service.AddToEquipSwitchAsync(0, HandR, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Empty(f.Operations.Requests);
    }

    [Fact]
    public async Task AddToEquipSwitch_RunsTheSameEligibilityGateAsEquip()
    {
        // Ring: Jobs exclude Novice -> FAIL; Katana: EquipLevelMin 4 -> FAILLEVEL for a level-1 Swordman.
        var f = Setup(Row(1, Ring), Row(2, Katana));

        var (ring, _) = await f.Service.AddToEquipSwitchAsync(0, AccR, Items, 99, Male, JobClass.Novice, CancellationToken.None);
        var (katana, _) = await f.Service.AddToEquipSwitchAsync(1, Arms, Items, 1, Male, JobClass.Swordman, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, ring!.Value.Result);
        Assert.Equal(EquipMutationResult.FailLevel, katana!.Value.Result);
        Assert.Empty(f.Operations.Requests);
    }

    [Fact]
    public async Task AddToEquipSwitch_UnknownSlot_ReturnsNoOutcome()
    {
        var f = Setup(Row(1, Knife));

        var (outcome, updated) = await f.Service.AddToEquipSwitchAsync(42, HandR, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Null(outcome);
        Assert.Null(updated);
    }

    [Fact]
    public async Task AddToEquipSwitch_DisplacesTheRowAlreadyRegisteredOnThatSlot_InOneOperation()
    {
        // Pinned pc.cpp:12136-12146: the previously registered row leaves the switch set (pc_equipswitch_remove).
        var f = Setup(Row(1, Knife, equipSwitch: HandR), Row(2, Knife));

        var (outcome, _) = await f.Service.AddToEquipSwitchAsync(1, HandR, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal((0u, 0u), State(f, 1));
        Assert.Equal((0u, HandR), State(f, 2));
        Assert.Single(f.Operations.Applied);
    }

    [Fact]
    public async Task AddToEquipSwitch_TwoHander_DisplacesBothRegisteredHands()
    {
        var f = Setup(Row(1, Knife, equipSwitch: HandR), Row(2, Guard, equipSwitch: HandL), Row(3, Katana));

        var (outcome, _) = await f.Service.AddToEquipSwitchAsync(2, Arms, Items, 99, Male, JobClass.Swordman, CancellationToken.None);

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, Arms), outcome);
        Assert.Equal((0u, 0u), State(f, 1));
        Assert.Equal((0u, 0u), State(f, 2));
        Assert.Equal((0u, Arms), State(f, 3));
    }

    [Fact]
    public async Task AddToEquipSwitch_BothAccessorySlotsRequested_PicksOneSlotFromTheSwitchSet()
    {
        // Pinned pc.cpp:12088-12092 with equip_index = equip_switch_index: ACC_R first, ACC_L once ACC_R is registered.
        var f = Setup(Row(1, Clip), Row(2, Clip));

        var (first, _) = await f.Service.AddToEquipSwitchAsync(0, Acc, Items, 99, Male, JobClass.Novice, CancellationToken.None);
        var (second, _) = await f.Service.AddToEquipSwitchAsync(1, Acc, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(AccR, first!.Value.WearLocation);
        Assert.Equal(AccL, second!.Value.WearLocation);
        Assert.Equal((0u, AccR), State(f, 1));
        Assert.Equal((0u, AccL), State(f, 2));
    }

    [Fact]
    public async Task RemoveFromEquipSwitch_ClearsRegistration_NoOpWhenNotRegistered()
    {
        var f = Setup(Row(1, Knife, equipSwitch: HandR), Row(2, Knife));

        var (removed, _) = await f.Service.RemoveFromEquipSwitchAsync(0, CancellationToken.None);
        var (notRegistered, _) = await f.Service.RemoveFromEquipSwitchAsync(1, CancellationToken.None);
        var (unknown, _) = await f.Service.RemoveFromEquipSwitchAsync(42, CancellationToken.None);

        Assert.Equal(new UnequipOutcome(true, HandR), removed);
        Assert.Equal(new UnequipOutcome(false, 0), notRegistered);
        Assert.Null(unknown);
        Assert.Equal((0u, 0u), State(f, 1));
        Assert.Single(f.Operations.Applied);
    }

    // ---- full switch (SkillEquipSwitch -> pc_equipswitch) ----

    [Fact]
    public async Task SwitchAll_EmptySlot_EquipsRegisteredRow()
    {
        var f = Setup(Row(1, Knife, equipSwitch: HandR), Row(2, CottonShirt, equip: ArmorPos));

        var (outcome, updated) = await SwitchAll(f, Cooldown(out _));

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, HandR), outcome);
        Assert.Equal((HandR, 0u), State(f, 1));
        Assert.Equal((ArmorPos, 0u), State(f, 2)); // untouched
        Assert.Equal(HandR, updated!.Items.Single(i => i.DurableId == 1).Equip);
    }

    [Fact]
    public async Task SwitchAll_OccupiedSlot_SwapsTheWornRowIntoTheSwitchSet()
    {
        // The pinned "swap the swap" (pc.cpp:12512-12576): the worn knife is taken off and becomes the new registration.
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Knife, equipSwitch: HandR), Row(3, CottonShirt, equip: ArmorPos));

        var (outcome, _) = await SwitchAll(f, Cooldown(out _));

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, HandR), outcome);
        Assert.Equal((0u, HandR), State(f, 1));
        Assert.Equal((HandR, 0u), State(f, 2));
        Assert.Equal((ArmorPos, 0u), State(f, 3));
        Assert.Single(f.Operations.Applied); // one atomic operation
    }

    [Fact]
    public async Task SwitchAll_TwoHandedLoadout_SwapsBothHandsAndBack()
    {
        // Worn: Knife (R) + Guard (L). Registered: Katana (Arms). Pinned pc_equipswitch on the Katana unequips both hands
        // (unequipped_position = R|L), equips the Katana, and registers Knife at R and Guard at L. Switching again reverses it.
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Guard, equip: HandL), Row(3, Katana, equipSwitch: Arms));
        var cooldown = Cooldown(out var clock);

        var (first, _) = await SwitchAll(f, cooldown, JobClass.Swordman);

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, Arms), first);
        Assert.Equal((0u, HandR), State(f, 1));
        Assert.Equal((0u, HandL), State(f, 2));
        Assert.Equal((Arms, 0u), State(f, 3));

        await clock.AdvanceAsync(EquipSwitchCooldown.Duration);
        var (back, _) = await SwitchAll(f, cooldown, JobClass.Swordman);

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, Arms), back);
        Assert.Equal((HandR, 0u), State(f, 1));
        Assert.Equal((HandL, 0u), State(f, 2));
        Assert.Equal((0u, Arms), State(f, 3));
        Assert.Equal(2, f.Operations.Applied.Count);
    }

    [Fact]
    public async Task SwitchAll_ShieldRegisteredAgainstWornTwoHander_AlsoEquipsTheRegisteredWeapon()
    {
        // Registered only on HAND_L, but the worn two-hander's own mask widens the swap to both hands (all_position).
        var f = Setup(Row(1, Katana, equip: Arms), Row(2, Guard, equipSwitch: HandL), Row(3, Knife, equipSwitch: HandR));

        var (outcome, _) = await SwitchAll(f, Cooldown(out _), JobClass.Swordman);

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, Arms), outcome);
        Assert.Equal((0u, Arms), State(f, 1));
        Assert.Equal((HandL, 0u), State(f, 2));
        Assert.Equal((HandR, 0u), State(f, 3));
    }

    [Fact]
    public async Task SwitchAll_IneligibleRegisteredRow_RejectsTheWholeSwitch_NothingChanges()
    {
        // A Ring registered for a Novice (e.g. registered before a job change): pinned would drop it from both sets; here
        // the whole switch is rejected atomically and the worn Clip stays worn.
        var f = Setup(Row(1, Clip, equip: AccR), Row(2, Knife, equipSwitch: HandR), Row(3, Ring, equipSwitch: AccR));

        var (outcome, updated) = await SwitchAll(f, Cooldown(out _));

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Empty(f.Operations.Requests);
        Assert.Equal((AccR, 0u), State(f, 1));
        Assert.Equal((0u, HandR), State(f, 2));
        Assert.Equal((0u, AccR), State(f, 3));
    }

    [Fact]
    public async Task SwitchAll_NothingRegistered_ReturnsNoOutcome_AndStillSpendsTheCooldown()
    {
        var f = Setup(Row(1, Knife, equip: HandR));
        var cooldown = Cooldown(out _);

        var (outcome, _) = await SwitchAll(f, cooldown);

        Assert.Null(outcome);
        Assert.Empty(f.Operations.Requests);
        Assert.False(cooldown.TryStart()); // pinned clif.cpp:22367 arms the tick before the empty-set check
    }

    [Fact]
    public async Task SwitchAll_DuringCooldown_IsIgnored_ThenAllowedAfterTenSeconds()
    {
        // Pinned db/re/skill_db.yml ALL_EQSWITCH Cooldown: 10000.
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Knife, equipSwitch: HandR));
        var cooldown = Cooldown(out var clock);

        await SwitchAll(f, cooldown);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(9999));
        var (blocked, _) = await SwitchAll(f, cooldown);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        var (allowed, _) = await SwitchAll(f, cooldown);

        Assert.Null(blocked);
        Assert.Equal(EquipMutationResult.Success, allowed!.Value.Result);
        Assert.Equal((HandR, 0u), State(f, 1)); // switched twice: back to the original knife
        Assert.Equal((0u, HandR), State(f, 2));
    }

    // ---- single switch (clif_parse_equipswitch_request_single) ----

    [Fact]
    public async Task SwitchSingle_RegisteredRow_SwapsOnlyThatRow()
    {
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Knife, equipSwitch: HandR), Row(3, Clip, equipSwitch: AccR));

        var (outcome, _) = await f.Service.SwitchSingleAsync(1, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, HandR), outcome);
        Assert.Equal((0u, HandR), State(f, 1));
        Assert.Equal((HandR, 0u), State(f, 2));
        Assert.Equal((0u, AccR), State(f, 3)); // the other registration is untouched
    }

    [Fact]
    public async Task SwitchSingle_UnregisteredRow_RegistersItAtItsOwnEquipPoint()
    {
        var f = Setup(Row(1, Guard));

        var (outcome, _) = await f.Service.SwitchSingleAsync(0, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, HandL), outcome);
        Assert.Equal((0u, HandL), State(f, 1));
    }

    // ---- interaction with a normal equip and other item actions ----

    [Fact]
    public async Task Equip_RegisteredRow_LeavesTheSwitchSet()
    {
        // Pinned pc.cpp:12161: pc_equipswitch_remove(sd, n) after a normal equip.
        var f = Setup(Row(1, Knife, equipSwitch: HandR));

        var (outcome, _) = await f.Service.EquipAsync(0, HandR, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal((HandR, 0u), State(f, 1));
    }

    [Fact]
    public void StorageOrCartTransfer_OfARegisteredRow_IsRefused()
    {
        // Pinned clif_parse_MoveToKafra (clif.cpp:13634) / pc_putitemtocart (pc.cpp:6699): MSI_SWAP_EQUIPITEM_UNREGISTER_FIRST.
        var row = Row(1, Knife, equipSwitch: HandR);

        var storage = ContainerPlanner.PlanTransferIn(row, 1, [], 600, Items, InventoryContainer.Storage);
        var cart = ContainerPlanner.PlanTransferIn(row, 1, [], 100, Items, InventoryContainer.Cart);

        Assert.Equal(ContainerPlanner.StorageTransferResult.NotStorable, storage.Result);
        Assert.Equal(ContainerPlanner.StorageTransferResult.NotStorable, cart.Result);
    }

    // ---- bonus recalculation ----

    [Fact]
    public async Task SwitchAll_RecalculatesEquipmentBonuses_AndSwitchingBackRestoresThem()
    {
        // Worn Clip (bMaxSP,10) at ACC_R, Ring (bStr,2) registered at ACC_R, Swordman level 99.
        var f = Setup(Row(1, Clip, equip: AccR), Row(2, Ring, equipSwitch: AccR));
        var cooldown = Cooldown(out var clock);

        Assert.True(Rules.Stats.TryResolveEquipment(f.Inventory.Snapshot, Items, out var before));
        var (_, switched) = await SwitchAll(f, cooldown, JobClass.Swordman);
        Assert.True(Rules.Stats.TryResolveEquipment(switched!, Items, out var during));
        await clock.AdvanceAsync(EquipSwitchCooldown.Duration);
        var (_, back) = await SwitchAll(f, cooldown, JobClass.Swordman);
        Assert.True(Rules.Stats.TryResolveEquipment(back!, Items, out var after));

        Assert.Equal((0, 10), (before.ParamBonus.Strength, before.ParamBonus.MaxSp));
        Assert.Equal((2, 0), (during.ParamBonus.Strength, during.ParamBonus.MaxSp));
        Assert.Equal((0, 10), (after.ParamBonus.Strength, after.ParamBonus.MaxSp));
    }

    // ---- persistence semantics ----

    [Fact]
    public async Task SwitchAll_PersistenceUnavailable_ReportsFailure_AndChangesNothing()
    {
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Knife, equipSwitch: HandR));
        f.Operations.DropRequests = 3; // every attempt of the service's retry budget is lost

        var (outcome, updated) = await SwitchAll(f, Cooldown(out _));

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Empty(f.Operations.Applied);
        Assert.Equal((HandR, 0u), State(f, 1));
        Assert.Equal((0u, HandR), State(f, 2));
        Assert.Equal(HandR, f.Inventory.Snapshot.Items.Single(i => i.DurableId == 1).Equip);
    }

    [Fact]
    public async Task SwitchAll_LostAcknowledgement_IsReplayedNotAppliedTwice()
    {
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Knife, equipSwitch: HandR));
        f.Operations.LoseAnswers = 1;

        var (outcome, updated) = await SwitchAll(f, Cooldown(out _));

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Single(f.Operations.Applied);
        Assert.Equal(2, f.Operations.Requests.Count);
        Assert.Equal(f.Operations.Requests[0].OperationId, f.Operations.Requests[1].OperationId); // same id -> CharServer replay
        Assert.Equal((0u, HandR), State(f, 1));
        Assert.Equal((HandR, 0u), State(f, 2));
        Assert.Equal(HandR, updated!.Items.Single(i => i.DurableId == 2).Equip);
    }

    [Fact]
    public async Task SwitchAll_RowChangedUnderneath_ConflictsWithoutPartialApplication_ThenSucceedsAfterReload()
    {
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Knife, equipSwitch: HandR));
        var cooldown = Cooldown(out var clock);
        // Another writer (e.g. a second session) bumps row 2's revision behind this session's cached snapshot.
        var stale = Durable(f, 2);
        await f.Operations.ExecuteAsync(new InventoryOperationRequest(AccountId, CharId, Guid.NewGuid(), InventoryOperationKind.Update,
            [new UpdateRowMutation(InventoryContainer.Inventory, 2, stale.Revision, stale with { Favorite = 1 })]), CancellationToken.None);

        var (conflicted, _) = await SwitchAll(f, cooldown);

        Assert.Equal(EquipMutationResult.Fail, conflicted!.Value.Result);
        Assert.Equal((HandR, 0u), State(f, 1));
        Assert.Equal((0u, HandR), State(f, 2));

        await clock.AdvanceAsync(EquipSwitchCooldown.Duration);
        var (retried, _) = await SwitchAll(f, cooldown);

        Assert.Equal(EquipMutationResult.Success, retried!.Value.Result);
        Assert.Equal((0u, HandR), State(f, 1));
        Assert.Equal((HandR, 0u), State(f, 2));
        Assert.Equal(1, Durable(f, 2).Favorite); // the other writer's change survived
    }

    [Fact]
    public async Task ConcurrentSwitchAndUnequip_AreSerialized_NeverLeavingAnInvalidLoadout()
    {
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Knife, equipSwitch: HandR), Row(3, Guard, equip: HandL));

        await Task.WhenAll(
            SwitchAll(f, Cooldown(out _)),
            f.Service.UnequipAsync(2, CancellationToken.None),
            f.Service.SwitchSingleAsync(1, Items, 99, Male, JobClass.Novice, CancellationToken.None));

        var rows = f.Operations.Rows;
        Assert.All(rows, r => Assert.False(r.Equip != 0 && r.EquipSwitch != 0)); // never worn and registered at once
        foreach (var slot in EquipSlots.Bitmask.Values)
        {
            Assert.True(rows.Count(r => (r.Equip & slot) != 0) <= 1);
            Assert.True(rows.Count(r => (r.EquipSwitch & slot) != 0) <= 1);
        }
        Assert.Equal(f.Operations.Applied.Count, f.Operations.Requests.Count); // no request was rejected as stale
    }

    [Fact]
    public async Task Reconnect_ReloadedFromDurableRows_CanSwitchBack()
    {
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, Knife, equipSwitch: HandR));
        await SwitchAll(f, Cooldown(out _));

        // A new session (reconnect): the snapshot is rebuilt from CharServer's rows only; the switch set comes with them.
        var reloaded = CharacterInventorySnapshot.FromRows(f.Operations.Rows);
        var inventory = TestInventory.Service(AccountId, CharId, f.Operations, reloaded);
        var service = new CharacterEquipmentMutationService(inventory, Rules.Stats);

        var (outcome, _) = await service.SwitchAllAsync(Cooldown(out _), Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal((HandR, 0u), State(f, 1));
        Assert.Equal((0u, HandR), State(f, 2));
    }

    // ---- pc_equipitem position rules shared with a normal equip (S4) ----

    [Fact]
    public async Task Equip_BothAccessorySlotsRequested_WearsOneSlotPerAccessory()
    {
        // Pinned pc.cpp:12088-12092: ACC_R unless ACC_R is taken and ACC_L free. A Both_Accessory item never takes both.
        var f = Setup(Row(1, Clip), Row(2, Clip));

        var (first, _) = await f.Service.EquipAsync(0, Acc, Items, 99, Male, JobClass.Novice, CancellationToken.None);
        var (second, _) = await f.Service.EquipAsync(1, Acc, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(AccR, first!.Value.WearLocation);
        Assert.Equal(AccL, second!.Value.WearLocation);
        Assert.Equal((AccR, 0u), State(f, 1));
        Assert.Equal((AccL, 0u), State(f, 2));
    }

    [Fact]
    public async Task Equip_AccessoryWithARightAccessoryCard_IsForcedToTheRightSlot()
    {
        // Pinned pc.cpp:12093-12107: a compounded card whose own location is ACC_R/ACC_L forces the position.
        var f = Setup(Row(1, Clip, equip: AccR), Row(2, Clip, card0: PlayingPereCard));

        var (outcome, _) = await f.Service.EquipAsync(1, Acc, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(AccR, outcome!.Value.WearLocation);
        Assert.Equal((0u, 0u), State(f, 1)); // displaced from ACC_R
        Assert.Equal((AccR, 0u), State(f, 2));
    }

    [Fact]
    public async Task Equip_DaggerByAnAssassin_MayGoToTheLeftHand_ButNotForANovice()
    {
        // Pinned pc_equippoint_sub (pc.cpp:1474-1480): MAPID_ASSASSIN wears a Right_Hand dagger as EQP_ARMS.
        var assassin = Setup(Row(1, Knife), Row(2, Knife));
        var (first, _) = await assassin.Service.EquipAsync(0, Arms, Items, 99, Male, JobClass.Assassin, CancellationToken.None);
        var (second, _) = await assassin.Service.EquipAsync(1, Arms, Items, 99, Male, JobClass.Assassin, CancellationToken.None);

        Assert.Equal(HandR, first!.Value.WearLocation);
        Assert.Equal(HandL, second!.Value.WearLocation);

        var novice = Setup(Row(1, Knife));
        var (leftHand, _) = await novice.Service.EquipAsync(0, HandL, Items, 99, Male, JobClass.Novice, CancellationToken.None);
        Assert.Equal(EquipMutationResult.Fail, leftHand!.Value.Result);
    }

    [Fact]
    public async Task Equip_CostumeHeadgear_DisplacesTheCostumeAlreadyWorn()
    {
        var f = Setup(Row(1, CostumeAngelVeil, equip: CostumeTop), Row(2, CostumeAngelVeil));

        var (outcome, _) = await f.Service.EquipAsync(1, CostumeTop, Items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(CostumeTop, outcome!.Value.WearLocation);
        Assert.Equal((0u, 0u), State(f, 1));
        Assert.Equal((CostumeTop, 0u), State(f, 2));
    }

    [Fact]
    public async Task Equip_BindOnEquipItem_BecomesCharacterBound_ButRegisteringItDoesNot()
    {
        // No pinned item sets Flags.BindOnEquip; the rule is data-driven, so a flag-modified real definition exercises it.
        var knife = Items[Knife];
        var bindOnEquip = knife with { Meta = knife.Meta! with { Data = knife.Meta.Data with { Flags = knife.Meta.Data.Flags | PackItemFlags.BindOnEquip } } };
        var items = new Dictionary<int, ItemDefinition>(Items) { [Knife] = bindOnEquip };
        var f = Setup(Row(1, Knife), Row(2, Knife));

        await f.Service.AddToEquipSwitchAsync(1, HandR, items, 99, Male, JobClass.Novice, CancellationToken.None);
        await f.Service.EquipAsync(0, HandR, items, 99, Male, JobClass.Novice, CancellationToken.None);

        Assert.Equal(4, Durable(f, 1).Bound); // pinned default_bind_on_equip: 4 (BOUND_CHAR)
        Assert.Equal(0, Durable(f, 2).Bound);
    }

    // ---- ammo (pinned pc_isequip EQP_AMMO branch, ammo_check_weapon: yes) ----

    // Arrow 1750 / Silver_Arrow 1751 (AMMO_ARROW), Bow 1701 (Both_Hand bow, Archer), Bullet 13200 (AMMO_BULLET),
    // Venom_Knife 1771 (AMMO_DAGGER, Assassin).
    private const uint AmmoPos = 0x8000;

    [Fact]
    public async Task Arrow_EquipsAtTheAmmoSlotWhatevertheRequest_OnlyWithABow()
    {
        var bowless = Setup(Row(1, 1750));
        var (refused, _) = await bowless.Service.EquipAsync(0, 0, Items, 99, Male, JobClass.Archer, CancellationToken.None);
        Assert.Equal(EquipMutationResult.Fail, refused!.Value.Result);

        var f = Setup(Row(1, 1701, equip: Arms), Row(2, 1750), Row(3, 1751));
        var (first, _) = await f.Service.EquipAsync(1, 0, Items, 99, Male, JobClass.Archer, CancellationToken.None); // the client sends no position
        var (second, _) = await f.Service.EquipAsync(2, 0, Items, 99, Male, JobClass.Archer, CancellationToken.None);

        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, AmmoPos), first);
        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, AmmoPos), second);
        Assert.Equal((0u, AmmoPos), (State(f, 2).Equip, State(f, 3).Equip)); // the new stack displaces the old one
    }

    [Fact]
    public async Task Bullet_WithoutAGun_IsRefused()
    {
        var f = Setup(Row(1, Knife, equip: HandR), Row(2, 13200));
        var (outcome, _) = await f.Service.EquipAsync(1, AmmoPos, Items, 99, Male, JobClass.Gunslinger, CancellationToken.None);
        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Empty(f.Operations.Requests);
    }

    [Fact]
    public async Task VenomKnife_RequiresAsVenomKnife()
    {
        var learned = new CharacterSkillSnapshot([new CharacterLearnedSkill(1004, 1, CharSkillFlag.Permanent)]);
        var without = Setup(Row(1, 1771));
        var with = Setup(Row(1, 1771));

        var (refused, _) = await without.Service.EquipAsync(0, 0, Items, 99, Male, JobClass.Assassin, CancellationToken.None);
        var (accepted, _) = await with.Service.EquipAsync(0, 0, Items, 99, Male, JobClass.Assassin, CancellationToken.None, learned);

        Assert.Equal(EquipMutationResult.Fail, refused!.Value.Result);
        Assert.Equal(new EquipOutcome(EquipMutationResult.Success, AmmoPos), accepted);
    }

    [Fact]
    public async Task Ammo_IsNeverRegisteredForAnEquipSwitch()
    {
        var f = Setup(Row(1, 1701, equip: Arms), Row(2, 1750));
        var (outcome, _) = await f.Service.AddToEquipSwitchAsync(1, AmmoPos, Items, 99, Male, JobClass.Archer, CancellationToken.None);
        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
    }

    // ---- job tier (pinned pc_isItemClass, RENEWAL branch) ----

    [Theory]
    [InlineData(JobClass.Knight, false)]       // normal tier: Classes has no Normal
    [InlineData(JobClass.BabyKnight, false)]   // baby: no Baby
    [InlineData(JobClass.LordKnight, true)]    // ITEMJ_UPPER, JOBL_UPPER without JOBL_THIRD
    [InlineData(JobClass.RuneKnight, true)]    // ITEMJ_THIRD (All_Third), JOBL_THIRD only
    [InlineData(JobClass.DragonKnight, true)]  // ITEMJ_FOURTH
    public async Task ClassRestrictedWeapon_FollowsThePinnedJobTierRule(JobClass job, bool allowed)
    {
        // Altas_Weapon 1175: Jobs Swordman/Knight/Crusader, Classes Upper + All_Third + Fourth, Both_Hand, EquipLevelMin 55.
        var f = Setup(Row(1, 1175));
        var (outcome, _) = await f.Service.EquipAsync(0, Arms, Items, 99, Male, job, CancellationToken.None);
        Assert.Equal(allowed ? EquipMutationResult.Success : EquipMutationResult.Fail, outcome!.Value.Result);
    }
}
