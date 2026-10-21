using Athena.Net.MapServer.Tests.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterEquipmentMutationServiceTests
{
    private static readonly GameplayRuleServices Rules = TestGameplayRules.Services();

    private const uint AccountId = 7;
    private const uint CharId = 9;

    private static readonly WeaponItemDefinition Knife = new(
        1201, "Knife", "Knife", Stackable: false, ClientViewId: 1201, Attack: 17, WeaponLevel: 1, WeaponType.Dagger, Range: 1, EquipLocation: 0x000002,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static readonly ArmorItemDefinition CottonShirt = new(
        2301, "Cotton_Shirt", "Cotton Shirt", Stackable: false, ClientViewId: 2301, Defense: 10, EquipLocation: 0x000010,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    // Real two-handed weapon (pinned db/re/item_db_equip.yml:542-555): Locations.Both_Hand ->
    // EQP_ARMS = EQP_HAND_R|EQP_HAND_L (0x000002|0x000020).
    private static readonly WeaponItemDefinition Katana = new(
        1116, "Katana", "Katana", Stackable: false, ClientViewId: 1116, Attack: 60, WeaponLevel: 1, WeaponType.TwoHandSword, Range: 1, EquipLocation: 0x000002 | 0x000020,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    // Buckler-style left-hand item (EQP_HAND_L) to prove a two-hander's equip clears a shield too.
    private static readonly WeaponItemDefinition Shield = new(
        1201, "Guard", "Guard", Stackable: false, ClientViewId: 1201, Attack: 0, WeaponLevel: 1, WeaponType.Dagger, Range: 1, EquipLocation: 0x000020,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static readonly EtcItemDefinition Wood = new(
        6008, "Wood", "Wood", Stackable: true, ClientViewId: 6008,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_etc.yml", 1));

    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items =
        new Dictionary<int, ItemDefinition> { [Knife.Id] = Knife, [CottonShirt.Id] = CottonShirt, [Wood.Id] = Wood, [Katana.Id] = Katana };

    private static readonly IReadOnlyDictionary<int, ItemDefinition> TwoHandItems =
        new Dictionary<int, ItemDefinition> { [1201] = Shield, [Katana.Id] = Katana };

    private static (InMemoryInventoryOperations Operations, CharacterInventoryService Inventory, CharacterEquipmentMutationService Service) Setup(CharacterInventorySnapshot inventory)
    {
        var operations = new InMemoryInventoryOperations(inventory);
        var service = global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, inventory);
        return (operations, service, new CharacterEquipmentMutationService(service));
    }

    [Fact]
    public async Task EquipAsync_UnequippedKnife_PersistsRightHandAndReturnsSuccess()
    {
        var (operations, _, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]));

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal(0x000002u, outcome.Value.WearLocation);
        Assert.Equal(0x000002u, updated!.Items[0].Equip);
        Assert.Equal([(1u, 0x000002u)], operations.EquipUpdates);
        Assert.Equal(0x000002u, operations.Rows.Single().Equip); // durable state agrees
    }

    [Fact]
    public async Task EquipAsync_AlreadyEquippedItem_ReturnsFailWithoutPersisting()
    {
        var (operations, _, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]));

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task EquipAsync_PositionNotInEquipLocation_ReturnsFail()
    {
        // Armor's EquipLocation is EQP_ARMOR (0x10); requesting EQP_HAND_R (0x2) should fail.
        var (operations, _, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 2301, 1, 0, true, 0, 0, 0)]));

        var (outcome, _) = await service.EquipAsync(0, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task EquipAsync_NonEquippableItem_ReturnsFail()
    {
        var (_, _, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 6008, 1, 0, true, 0, 0, 0)]));

        var (outcome, _) = await service.EquipAsync(0, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
    }

    [Fact]
    public async Task EquipAsync_UnknownSlotIndex_ReturnsNullOutcome()
    {
        var (_, _, service) = Setup(new([]));

        var (outcome, updated) = await service.EquipAsync(5, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Null(outcome);
        Assert.Null(updated);
    }

    [Fact]
    public async Task EquipAsync_SlotConflict_UnequipsAndEquipsInOneAtomicOperation()
    {
        // Two knives; slot 0 already equipped, equip slot 1 into the same right hand.
        var (operations, _, service) = Setup(new(
        [
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0),
            new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, 1201, 1, 0, true, 0, 0, 0),
        ]));

        var (outcome, updated) = await service.EquipAsync(1, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal(0u, updated!.Items[0].Equip); // old slot unequipped
        Assert.Equal(0x000002u, updated.Items[1].Equip); // new slot equipped
        // ONE request: the unequip and the equip are applied together or not at all (previously two separate writes).
        Assert.Single(operations.Requests);
        Assert.Equal([(1u, 0u), (2u, 0x000002u)], operations.EquipUpdates);
    }

    [Fact]
    public async Task EquipAsync_TwoHandedWeapon_UnequipsBothCurrentWeaponAndShieldAtomically()
    {
        // Pinned pc.cpp:12147-12157: equipping a Both_Hand weapon (EQP_ARMS) must clear whatever
        // currently occupies EITHER hand, even when those are two separate inventory rows.
        var (operations, _, service) = Setup(new(
        [
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201 /* right-hand knife */, 1, 0x000002, true, 0, 0, 0),
            new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, 1201 /* left-hand shield */, 1, 0x000020, true, 0, 0, 0),
            new CharacterInventoryItem(DurableId: 3, SlotIndex: 2, Katana.Id, 1, 0, true, 0, 0, 0),
        ]));

        var (outcome, updated) = await service.EquipAsync(2, requestedPosition: 0x000002 | 0x000020, TwoHandItems, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal(0x000002u | 0x000020u, outcome.Value.WearLocation);
        Assert.Equal(0u, updated!.Items[0].Equip); // right-hand weapon unequipped
        Assert.Equal(0u, updated.Items[1].Equip); // left-hand shield unequipped
        Assert.Equal(0x000002u | 0x000020u, updated.Items[2].Equip); // katana occupies both hands
        Assert.Single(operations.Requests); // one atomic operation
        Assert.Equal(
            new HashSet<(uint, uint)> { (1u, 0u), (2u, 0u), (3u, 0x000002u | 0x000020u) },
            new HashSet<(uint, uint)>(operations.EquipUpdates));
    }

    [Fact]
    public async Task EquipAsync_PersistenceFailure_ReturnsFailWithoutMutatingSnapshot()
    {
        var (operations, inventory, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]));
        operations.DropRequests = 10; // CharServer never answers

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Equal(0u, inventory.Snapshot.Items[0].Equip);
        Assert.Equal(0u, operations.Rows.Single().Equip);
    }

    [Fact]
    public async Task EquipAsync_LostAcknowledgement_IsReplayedNotAppliedTwice()
    {
        var (operations, inventory, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]));
        operations.LoseAnswers = 1; // the equip IS durable, but its answer is lost

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal(0x000002u, updated!.Items[0].Equip);
        Assert.Equal(2, operations.Requests.Count);                                  // sent twice with the same OperationId
        Assert.Equal(operations.Requests[0].OperationId, operations.Requests[1].OperationId);
        Assert.Single(operations.Applied);                                           // applied once
        Assert.Equal(1u, operations.Rows.Single().Revision);                         // one revision bump (0 -> 1), not two
        Assert.Equal(0x000002u, inventory.Snapshot.Items[0].Equip);
    }

    [Fact]
    public async Task EquipAsync_StaleRevision_FailsAndReloadsTheAuthoritativeRows()
    {
        var (operations, inventory, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]));
        // Another writer changed the durable row after this session's snapshot was taken.
        var other = global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, inventory.Snapshot);
        await other.ExecuteAsync(Athena.Net.Shared.Inventory.InventoryOperationKind.Update, snapshot =>
            [new Athena.Net.Shared.Inventory.UpdateRowMutation(Athena.Net.Shared.Inventory.InventoryContainer.Inventory, 1, 0, snapshot.Items[0].ToRow() with { Favorite = 1 })], CancellationToken.None);

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Equal((byte)1, inventory.Snapshot.Items[0].Favorite);    // the session re-read the truth
        Assert.Equal(0u, operations.Rows.Single().Equip);
    }

    [Fact]
    public async Task UnequipAsync_EquippedKnife_PersistsZeroAndReturnsSuccess()
    {
        var (operations, _, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]));

        var (outcome, updated) = await service.UnequipAsync(0, CancellationToken.None);

        Assert.True(outcome!.Value.Success);
        Assert.Equal(0x000002u, outcome.Value.WearLocation); // reports what WAS equipped
        Assert.Equal(0u, updated!.Items[0].Equip);
        Assert.Equal([(1u, 0u)], operations.EquipUpdates);
    }

    // Equip / unequip drive the hard-DEF and weapon contribution (reference-verified loadouts: VF-05); the stats are
    // always rebuilt from the committed inventory, never patched incrementally.
    [Fact]
    public async Task EquipThenUnequipArmor_ChangesHardDefenseAccordingly()
    {
        var (_, inventory, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 2301, 1, 0, true, 0, 0, 0)]));

        Assert.True(Rules.Stats.TryResolveEquipment(inventory.Snapshot, Items, out var before));
        var (_, equipped) = await service.EquipAsync(0, requestedPosition: 0x000010, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);
        Assert.True(Rules.Stats.TryResolveEquipment(equipped!, Items, out var wearing));
        var (_, unequipped) = await service.UnequipAsync(0, CancellationToken.None);
        Assert.True(Rules.Stats.TryResolveEquipment(unequipped!, Items, out var after));

        Assert.Equal((0, 10, 0), (before.HardDefense, wearing.HardDefense, after.HardDefense));
    }

    [Fact]
    public async Task UnequipAsync_AlreadyUnequippedItem_ReturnsFailureWithoutPersisting()
    {
        var (operations, _, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]));

        var (outcome, updated) = await service.UnequipAsync(0, CancellationToken.None);

        Assert.False(outcome!.Value.Success);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task UnequipAsync_UnknownSlotIndex_ReturnsFailureWithoutPersisting()
    {
        var (operations, _, service) = Setup(new([]));

        var (outcome, updated) = await service.UnequipAsync(5, CancellationToken.None);

        Assert.False(outcome!.Value.Success);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task UnequipAsync_PersistenceFailure_ReturnsFailureWithoutMutatingSnapshot()
    {
        var (operations, inventory, service) = Setup(new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]));
        operations.DropRequests = 10;

        var (outcome, updated) = await service.UnequipAsync(0, CancellationToken.None);

        Assert.False(outcome!.Value.Success);
        Assert.Null(updated);
        Assert.Equal(0x000002u, inventory.Snapshot.Items[0].Equip);
    }

    // ===== S4 equipment eligibility (pinned pc_isequip, pc.cpp:1850-1908) - real generated items,
    // never hand-built ItemMetadata, so a pack-format/loader regression would also be caught here. =====

    private static readonly IReadOnlyDictionary<int, ItemDefinition> RealItems = ItemCatalog.Current.ById;
    private const int Sword = 1101; // EquipLevelMin: 2, pinned db/re/item_db_equip.yml.
    private const int SinisterViollin = 1900; // Gender: Male, EquipLevelMin: 160, pinned db/re/item_db_equip.yml.

    [Fact]
    public async Task EquipAsync_BelowEquipLevelMin_ReturnsFailLevel_NeverPersists()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, Sword, 1, 0, true, 0, 0, 0)]);
        var operations = new InMemoryInventoryOperations(initial);
        var service = new CharacterEquipmentMutationService(global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, initial));

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, RealItems, characterBaseLevel: 1, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.FailLevel, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task EquipAsync_AtOrAboveEquipLevelMin_Succeeds()
    {
        var operations = new InMemoryInventoryOperations(new([new CharacterInventoryItem(1, 0, Sword, 1, 0, true, 0, 0, 0)]));
        var service = new CharacterEquipmentMutationService(global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, CharacterInventorySnapshot.FromRows(operations.Rows)));

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, RealItems, characterBaseLevel: 2, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.NotNull(updated);
    }

    [Fact]
    public async Task EquipAsync_WrongSex_ReturnsFail_NeverPersists()
    {
        var operations = new InMemoryInventoryOperations(new([new CharacterInventoryItem(1, 0, SinisterViollin, 1, 0, true, 0, 0, 0)]));
        var service = new CharacterEquipmentMutationService(global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, CharacterInventorySnapshot.FromRows(operations.Rows)));

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, RealItems, characterBaseLevel: 160, characterSex: 0 /* SEX_FEMALE */, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task EquipAsync_MatchingSex_Succeeds()
    {
        var operations = new InMemoryInventoryOperations(new([new CharacterInventoryItem(1, 0, SinisterViollin, 1, 0, true, 0, 0, 0)]));
        var service = new CharacterEquipmentMutationService(global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, CharacterInventorySnapshot.FromRows(operations.Rows)));

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, RealItems, characterBaseLevel: 160, characterSex: 1 /* SEX_MALE */, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.NotNull(updated);
    }

    // Pinned pc.cpp:1890-1891: attribute == 1 ("broken") blocks equip outright - a generic row-state
    // check, never item-id-specific, so a hand-built fixture (any item, any Attribute=1) suffices.
    [Fact]
    public async Task EquipAsync_BrokenItem_ReturnsFail_NeverPersists()
    {
        var brokenRow = new CharacterInventoryItem(1, 0, Knife.Id, 1, 0, true, 0, 0, 0,
            Detail: new ItemInstanceDetail(Attribute: 1, 0, 0, 0, 0, 0, default, default, default, default, default, 0, 0, 0, 0));
        var (operations, _, service) = Setup(new([brokenRow]));

        var (outcome, updated) = await service.EquipAsync(0, requestedPosition: 0x000002, Items, characterBaseLevel: 99, characterSex: ItemMetadata.BothSexes, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }
}
