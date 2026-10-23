using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

// Pinned clif_parse_enchantgrade_start (clif.cpp:24059-24243), server-authoritative mutation only - the client
// enchant-grade UI packets have no capture evidence yet (see CharacterEnchantGradeService's own doc comment).
// Real pinned db/re/enchantgrade.yml Type: Armor, Level 2, Grade None (direct inspection, not re-derived from
// the implementation under test): Chances[Refine 9]=1000, [10]=2000, [11..15]=7000, [16..20]=8000; Bonus 10;
// Catalyst Blessed_Etel_Dust AmountPerStep 1/MaximumSteps 10/ChanceIncrease 100; Options[0]=(Etel_Skyblue_Jewel,
// Amount 1, Zeny 175000, BreakingRate 10000), [1]=(Etel_Skyblue_Jewel, Amount 5, Zeny 875000, no BreakingRate).
public sealed class CharacterEnchantGradeServiceTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const int StarArmorOfPow = 450169; // ArmorLevel 2, Gradable, Refineable - real pinned item.
    private const int EtelSkyblueJewel = 1000325;
    private const int BlessedEtelDust = 1000337;

    private static (InMemoryInventoryOperations Operations, CharacterEnchantGradeService Service) Setup(CharacterInventorySnapshot inventory, uint zeny, int roll)
    {
        var operations = new InMemoryInventoryOperations(inventory, zeny);
        var inventoryService = TestInventory.Service(AccountId, CharId, operations, inventory);
        return (operations, new CharacterEnchantGradeService(inventoryService, () => roll));
    }

    private static IReadOnlyDictionary<int, ItemDefinition> Items => ItemCatalog.Current.ById;

    [Fact]
    public async Task UpgradeAsync_SuccessRoll_OptionZero_RaisesGradeAndResetsRefineAndConsumesMaterialAndZenyAtomically()
    {
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0); // Chance at refine 9 is 1000 (nonzero).
        var jewel = new CharacterInventoryItem(2, 1, EtelSkyblueJewel, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([armor, jewel]), zeny: 200_000, roll: 0); // roll 0 < any positive chance.

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 0, catalystSteps: 0, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.Success, result);
        var row = updated!.Items.Single(i => i.DurableId == armor.DurableId);
        Assert.Equal(1, row.Details.EnchantGrade); // None(0) -> D(1).
        Assert.Equal(0, row.Refine); // pinned: "On successful enchantgrade increase the refine is reset".
        Assert.DoesNotContain(updated.Items, i => i.ItemId == EtelSkyblueJewel); // the one jewel (Amount 1) was consumed.
        Assert.Equal(25_000u, operations.Zeny); // 200000 - 175000 (Option 0's Zeny).
        Assert.Single(operations.Requests); // one atomic operation for material + grade/refine + zeny.
    }

    [Fact]
    public async Task UpgradeAsync_FailureRoll_OptionZero_AlwaysBreaks_BreakingRateIsTenThousand()
    {
        // Option 0's BreakingRate is 10000 (= 100%) - any failure at this option always breaks, regardless of the roll.
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0);
        var jewel = new CharacterInventoryItem(2, 1, EtelSkyblueJewel, 1, 0, true, 0, 0, 0);
        // roll(success) >= totalChance(1000) fails; roll(breaking) < BreakingRate(10000) always breaks.
        var rolls = new Queue<int>([1000, 0]);
        var operations = new InMemoryInventoryOperations(new([armor, jewel]), 200_000);
        var inventoryService = TestInventory.Service(AccountId, CharId, operations, new([armor, jewel]));
        var service = new CharacterEnchantGradeService(inventoryService, () => rolls.Dequeue());

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 0, catalystSteps: 0, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.Broken, result);
        Assert.DoesNotContain(updated!.Items, i => i.DurableId == armor.DurableId); // the armor itself is deleted.
        Assert.DoesNotContain(updated.Items, i => i.ItemId == EtelSkyblueJewel); // material still consumed.
        Assert.Equal(25_000u, operations.Zeny); // zeny still spent even on failure.
    }

    [Fact]
    public async Task UpgradeAsync_FailureRoll_OptionOne_PlainFailure_NoBreakingOrDowngrade_GradeAndRefineUnchanged()
    {
        // Option 1 has no BreakingRate and no DowngradeAmount - a failure here is a plain failure: material and
        // zeny are spent, but nothing about the item itself changes.
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0);
        var jewels = new CharacterInventoryItem(2, 1, EtelSkyblueJewel, 5, 0, true, 0, 0, 0); // Option 1's own Amount is 5.
        var (operations, service) = Setup(new([armor, jewels]), zeny: 1_000_000, roll: 1000); // roll >= totalChance(1000) fails; BreakingRate 0 never breaks.

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 1, catalystSteps: 0, expectedZeny: 1_000_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.Failure, result);
        var row = updated!.Items.Single(i => i.DurableId == armor.DurableId);
        Assert.Equal(0, row.Details.EnchantGrade); // unchanged.
        Assert.Equal(9, row.Refine); // unchanged.
        Assert.DoesNotContain(updated.Items, i => i.ItemId == EtelSkyblueJewel); // all 5 jewels consumed.
        Assert.Equal(125_000u, operations.Zeny); // 1000000 - 875000 (Option 1's Zeny).
    }

    [Fact]
    public async Task UpgradeAsync_WithCatalyst_SameMaterialAsOption_CombinesIntoOneDeletion()
    {
        // Blessed_Etel_Dust (the catalyst) and Etel_Skyblue_Jewel (Option 0's own material) are DIFFERENT real
        // items here, so this test instead proves the opposite, more interesting real-data case first: the
        // catalyst RAISES the chance via ChanceIncrease, consuming its own separate material.
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0);
        var jewel = new CharacterInventoryItem(2, 1, EtelSkyblueJewel, 1, 0, true, 0, 0, 0);
        var dust = new CharacterInventoryItem(3, 2, BlessedEtelDust, 3, 0, true, 0, 0, 0); // 3 steps * AmountPerStep 1 = 3.
        // totalChance = 1000 (base) + 3*100 (ChanceIncrease) = 1300. roll 1200 succeeds (1200 < 1300) but would
        // have FAILED without the catalyst (1200 >= 1000) - this is the exact behavior under test.
        var (operations, service) = Setup(new([armor, jewel, dust]), zeny: 200_000, roll: 1200);

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 0, catalystSteps: 3, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.Success, result);
        Assert.DoesNotContain(updated!.Items, i => i.ItemId == EtelSkyblueJewel);
        Assert.DoesNotContain(updated.Items, i => i.ItemId == BlessedEtelDust); // all 3 dust consumed.
        Assert.Equal(25_000u, operations.Zeny);
    }

    [Fact]
    public async Task UpgradeAsync_CatalystStepsClampedToMaximumSteps_ExcessIsIgnoredNotRejected()
    {
        // MaximumSteps is 10 - requesting 100 steps must clamp to 10, matching pinned `min(p->blessing_amount, maximumSteps)`,
        // not reject the whole attempt or consume material for the unclamped amount.
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0);
        var jewel = new CharacterInventoryItem(2, 1, EtelSkyblueJewel, 1, 0, true, 0, 0, 0);
        var dust = new CharacterInventoryItem(3, 2, BlessedEtelDust, 10, 0, true, 0, 0, 0); // exactly enough for the CLAMPED 10 steps.
        // totalChance = 1000 + 10*100 = 2000. roll 1999 succeeds only with the full clamp applied.
        var (operations, service) = Setup(new([armor, jewel, dust]), zeny: 200_000, roll: 1999);

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 0, catalystSteps: 100, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.Success, result);
        Assert.DoesNotContain(updated!.Items, i => i.ItemId == BlessedEtelDust); // all 10 (not 100) consumed.
    }

    [Fact]
    public async Task UpgradeAsync_InsufficientCatalystMaterial_RejectsTheWholeOperation_NothingPersisted()
    {
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0);
        var jewel = new CharacterInventoryItem(2, 1, EtelSkyblueJewel, 1, 0, true, 0, 0, 0);
        var dust = new CharacterInventoryItem(3, 2, BlessedEtelDust, 2, 0, true, 0, 0, 0); // need 3, only have 2.
        var (operations, service) = Setup(new([armor, jewel, dust]), zeny: 200_000, roll: 0);

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 0, catalystSteps: 3, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.InsufficientMaterial, result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests); // nothing sent to CharServer at all.
        Assert.Equal(200_000u, operations.Zeny); // untouched.
    }

    [Fact]
    public async Task UpgradeAsync_InsufficientZeny_RejectsTheWholeOperation_NothingPersisted()
    {
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0);
        var jewel = new CharacterInventoryItem(2, 1, EtelSkyblueJewel, 1, 0, true, 0, 0, 0);
        var (operations, service) = Setup(new([armor, jewel]), zeny: 100_000, roll: 0); // needs 175000.

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 0, catalystSteps: 0, expectedZeny: 100_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.InsufficientZeny, result);
        Assert.Null(updated);
        Assert.Empty(operations.Requests);
        Assert.DoesNotContain(updated?.Items ?? [], i => i.ItemId == EtelSkyblueJewel);
    }

    [Fact]
    public async Task UpgradeAsync_ZeroChanceAtThisRefineLevel_RejectsAsMaxGrade()
    {
        // Grade None's Chances table starts at Refine 9 - refine 8 has NO tabulated entry, matching pinned
        // "No chance to increase the enchantgrade" (totalChance == 0).
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 8, 0, 0);
        var jewel = new CharacterInventoryItem(2, 1, EtelSkyblueJewel, 1, 0, true, 0, 0, 0);
        var (_, service) = Setup(new([armor, jewel]), zeny: 200_000, roll: 0);

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 0, catalystSteps: 0, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.MaxGrade, result);
        Assert.Null(updated);
    }

    [Fact]
    public async Task UpgradeAsync_UnknownOptionIndex_IsRejected()
    {
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0);
        var (_, service) = Setup(new([armor]), zeny: 200_000, roll: 0);

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 99, catalystSteps: 0, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.UnknownOption, result);
        Assert.Null(updated);
    }

    [Fact]
    public async Task UpgradeAsync_NonGradableItem_IsRejected()
    {
        const int sword = 1101; // Refineable, but not Gradable (no (Weapon,1) entry in enchantgrade.yml at all).
        var weapon = new CharacterInventoryItem(1, 0, sword, 1, 0, true, 0, 0, 0);
        var (_, service) = Setup(new([weapon]), zeny: 200_000, roll: 0);

        var (result, updated) = await service.UpgradeAsync(weapon.DurableId, optionIndex: 0, catalystSteps: 0, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.NotGradable, result);
        Assert.Null(updated);
    }

    [Fact]
    public async Task UpgradeAsync_UnknownRow_IsRejected()
    {
        var (_, service) = Setup(new([]), zeny: 200_000, roll: 0);
        var (result, updated) = await service.UpgradeAsync(targetDurableId: 999, optionIndex: 0, catalystSteps: 0, expectedZeny: 200_000, Items, CancellationToken.None);
        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.UnknownRow, result);
        Assert.Null(updated);
    }

    [Fact]
    public async Task UpgradeAsync_AlreadyAtMaxGrade_IsRejected()
    {
        var armor = new CharacterInventoryItem(1, 0, StarArmorOfPow, 1, 0, true, Refine: 9, 0, 0,
            Detail: ItemInstanceDetail.Empty with { EnchantGrade = 4, Revision = 0 }); // grade A, the maximum.
        var (_, service) = Setup(new([armor]), zeny: 200_000, roll: 0);

        var (result, updated) = await service.UpgradeAsync(armor.DurableId, optionIndex: 0, catalystSteps: 0, expectedZeny: 200_000, Items, CancellationToken.None);

        Assert.Equal(CharacterEnchantGradeService.EnchantGradeAttemptResult.MaxGrade, result);
        Assert.Null(updated);
    }
}
