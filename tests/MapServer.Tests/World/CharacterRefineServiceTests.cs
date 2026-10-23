using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

// Pinned clif_parse_refineui_refine (clif.cpp:22644-22810), server-authoritative mutation only - the
// client Refine UI packets have no capture evidence yet (see CharacterRefineService's own doc comment).
public sealed class CharacterRefineServiceTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const int Sword = 1101; // WeaponLevel: 1, Refineable.
    private const int Phracon = 1010; // Weapon Level 1's real pinned "Normal" cost material.

    private static (InMemoryInventoryOperations Operations, CharacterRefineService Service) Setup(CharacterInventorySnapshot inventory, uint zeny, int roll)
    {
        var operations = new InMemoryInventoryOperations(inventory, zeny);
        var inventoryService = TestInventory.Service(AccountId, CharId, operations, inventory);
        return (operations, new CharacterRefineService(inventoryService, () => roll));
    }

    private static IReadOnlyDictionary<int, ItemDefinition> Items => ItemCatalog.Current.ById;

    [Fact]
    public async Task RefineAsync_SuccessRoll_IncrementsRefineAndConsumesMaterialAndZenyAtomically()
    {
        var sword = new CharacterInventoryItem(1, 0, Sword, 1, 0, true, 0, 0, 0);
        var material = new CharacterInventoryItem(2, 1, Phracon, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([sword, material]), zeny: 1000, roll: 0); // roll 0 always beats a positive Rate.

        var (result, updated) = await service.RefineAsync(sword.DurableId, "Normal", expectedZeny: 1000, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.Success, result);
        Assert.Equal(1, updated!.Items.Single(i => i.DurableId == sword.DurableId).Refine);
        Assert.DoesNotContain(updated.Items, i => i.ItemId == Phracon); // the one Phracon was consumed.
        Assert.Equal(950u, operations.Zeny); // pinned Weapon Level 1 Refine 1 Normal Price: 50 (direct inspection of db/re/refine.yml).
        Assert.Single(operations.Requests); // one atomic operation for the material consumption + refine + zeny deduction.
    }

    // Pinned Weapon Level 1 Refine 8 (refine 7->8 attempt), Type Normal: Rate 6000, Price 50,
    // BreakingRate 10000 (direct inspection of db/re/refine.yml, not re-derived from the generated table
    // under test) - a failure at this level/type ALWAYS breaks (BreakingRate 10000 = 100%).
    [Fact]
    public async Task RefineAsync_FailureRollAtABreakingLevel_DeletesTheItem()
    {
        var sword = new CharacterInventoryItem(1, 0, Sword, 1, 0, true, Refine: 7, 0, 0);
        var material = new CharacterInventoryItem(2, 1, Phracon, 1, 0, true, 0, 0, 0);
        // roll(success) > Rate(6000) fails the `Rate >= roll` success check; roll(breaking) < BreakingRate(10000) always breaks.
        var operations = new InMemoryInventoryOperations(new([sword, material]), 1000);
        var inventoryService = TestInventory.Service(AccountId, CharId, operations, new([sword, material]));
        var rolls = new Queue<int>([6001, 0]);
        var service = new CharacterRefineService(inventoryService, () => rolls.Dequeue());

        var (result, updated) = await service.RefineAsync(sword.DurableId, "Normal", expectedZeny: 1000, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.Broken, result);
        Assert.DoesNotContain(updated!.Items, i => i.DurableId == sword.DurableId); // the sword itself is deleted.
        Assert.DoesNotContain(updated.Items, i => i.ItemId == Phracon); // material still consumed.
        Assert.Equal(950u, operations.Zeny); // zeny still spent (pinned clif.cpp:22746-22751).
    }

    // Pinned Weapon Level 1 Refine 8, Type HD: Rate 6000, Price 20000, DowngradeAmount 1, NO BreakingRate -
    // a failure at this level/type downgrades by 1 instead of breaking.
    [Fact]
    public async Task RefineAsync_FailureRollAtADowngradingLevel_LowersRefineByTheConfiguredAmount()
    {
        var sword = new CharacterInventoryItem(1, 0, Sword, 1, 0, true, Refine: 7, 0, 0);
        var hdOridecon = new CharacterInventoryItem(2, 1, 6240, 1, 0, true, 0, 0, 0); // HD_Oridecon's real item_db Id.
        var (operations, service) = Setup(new([sword, hdOridecon]), zeny: 20000, roll: 6001); // roll(success) > Rate(6000) fails.

        var (result, updated) = await service.RefineAsync(sword.DurableId, "HD", expectedZeny: 20000, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.Downgraded, result);
        Assert.Equal(6, updated!.Items.Single(i => i.DurableId == sword.DurableId).Refine); // 7 - 1.
        Assert.Equal(0u, operations.Zeny); // 20000 - 20000.
    }

    [Fact]
    public async Task RefineAsync_InsufficientZeny_RejectsTheWholeAttempt_NothingPersisted()
    {
        var sword = new CharacterInventoryItem(1, 0, Sword, 1, 0, true, 0, 0, 0);
        var material = new CharacterInventoryItem(2, 1, Phracon, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([sword, material]), zeny: 10, roll: 0); // pinned Price is 50.

        var (result, updated) = await service.RefineAsync(sword.DurableId, "Normal", expectedZeny: 10, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.InsufficientZeny, result);
        Assert.Null(updated);
        Assert.Equal(10u, operations.Zeny);
        Assert.Equal(1, operations.AmountOf(Phracon)); // material NOT consumed.
    }

    [Fact]
    public async Task RefineAsync_InsufficientMaterial_RejectsTheWholeAttempt_ZenyUntouched()
    {
        var sword = new CharacterInventoryItem(1, 0, Sword, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([sword]), zeny: 1000, roll: 0); // no Phracon at all.

        var (result, updated) = await service.RefineAsync(sword.DurableId, "Normal", expectedZeny: 1000, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.InsufficientMaterial, result);
        Assert.Null(updated);
        Assert.Equal(1000u, operations.Zeny); // never charged - the pinned ordering pays zeny only after confirming the material exists.
    }

    [Fact]
    public async Task RefineAsync_UnrefineableItem_IsRejected()
    {
        var unidentifiedPotion = new CharacterInventoryItem(1, 0, 501, 1, 0, true, 0, 0, 0); // a plain consumable - never equippable/refineable.
        var (operations, service) = Setup(new([unidentifiedPotion]), zeny: 1000, roll: 0);

        var (result, updated) = await service.RefineAsync(unidentifiedPotion.DurableId, "Normal", expectedZeny: 1000, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.NotRefineable, result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task RefineAsync_UnknownRow_IsRejected()
    {
        var (operations, service) = Setup(new([]), zeny: 1000, roll: 0);

        var (result, updated) = await service.RefineAsync(targetDurableId: 999, "Normal", expectedZeny: 1000, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.UnknownRow, result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task RefineAsync_RefineLevelBeyondMaxRefine_IsRejected_NotSilentlyAccepted()
    {
        // MAX_REFINE is 20; a Sword already at refine 20 has no RefineLevels[Level: 21] entry at all in the
        // pinned data, matching GeneratedRefineBonus.Cost's own null-for-"no further refine possible" contract.
        var maxed = new CharacterInventoryItem(1, 0, Sword, 1, 0, true, Refine: 20, 0, 0);
        var material = new CharacterInventoryItem(2, 1, Phracon, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([maxed, material]), zeny: 1000, roll: 0);

        var (result, updated) = await service.RefineAsync(maxed.DurableId, "Normal", expectedZeny: 1000, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.MaxRefine, result);
        Assert.Null(updated);
        Assert.Equal(1000u, operations.Zeny);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task RefineAsync_PersistenceFailure_ReturnsPersistenceFailedWithoutMutatingSnapshot()
    {
        var sword = new CharacterInventoryItem(1, 0, Sword, 1, 0, true, 0, 0, 0);
        var material = new CharacterInventoryItem(2, 1, Phracon, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([sword, material]), zeny: 1000, roll: 0);
        operations.DropRequests = 100;

        var (result, updated) = await service.RefineAsync(sword.DurableId, "Normal", expectedZeny: 1000, Items, CancellationToken.None);

        Assert.Equal(CharacterRefineService.RefineAttemptResult.PersistenceFailed, result);
        Assert.Null(updated);
    }
}
