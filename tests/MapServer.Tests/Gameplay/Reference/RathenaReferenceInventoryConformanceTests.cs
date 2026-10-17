using System.Text.Json;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.Tests.Gameplay.Reference;

// Feature 16 inventory mechanics against the executed pinned rAthena (VF-20 weight limits, VF-21 pc_additem decisions, VF-22 weight status).
public abstract partial class RathenaReferenceConformanceTests
{
    private static readonly IReadOnlyDictionary<int, ItemDefinition> Catalog = ItemCatalog.Current.ById;

    // ---- VF-20 ----

    public static TheoryData<int> MaxWeightCases => new(Enumerable.Range(0, 42));

    [Theory]
    [MemberData(nameof(MaxWeightCases))]
    public void Vf20_MaxWeight_MatchesStatusCalcWeight(int index)
    {
        var c = RathenaReferenceFixture.LoadCommitted().Output("VF-20").GetProperty("cases")[index];
        var job = (ushort)c.GetProperty("job").GetInt32();

        var actual = Rules.Inventory.MaxWeight(new MaxWeightInput(job, (uint)c.GetProperty("str").GetInt32()));

        Assert.Equal((uint)c.GetProperty("max_weight").GetInt32(), actual);
    }

    [Fact]
    public void Vf20_PinnedWeightRates_AreTheOnesTheRulesUse()
    {
        var config = RathenaReferenceFixture.LoadCommitted().Output("VF-20").GetProperty("config");
        Assert.Equal(70, config.GetProperty("natural_heal_weight_rate").GetInt32());
        Assert.Equal(90, config.GetProperty("major_overweight_rate").GetInt32());
        Assert.Equal(WeightLevel.Over, Rules.Inventory.WeightLevelFor(config.GetProperty("natural_heal_weight_rate").GetInt32()));
        Assert.Equal(WeightLevel.Major, Rules.Inventory.WeightLevelFor(config.GetProperty("major_overweight_rate").GetInt32()));
        Assert.Equal(30_000, config.GetProperty("max_amount").GetInt32());
        Assert.Equal(100, config.GetProperty("inventory_base").GetInt32());
    }

    // ---- VF-22 ----

    [Fact]
    public void Vf22_WeightStatusThresholds_MatchPcUpdateWeightStatus()
    {
        var steps = RathenaReferenceFixture.LoadCommitted().Output("VF-22").GetProperty("steps");
        foreach (var step in steps.EnumerateArray())
        {
            var weight = (uint)step.GetProperty("weight").GetInt32();
            var percent = Rules.Inventory.WeightPercent(weight, 1000);
            var level = Rules.Inventory.WeightLevelFor(percent);

            Assert.Equal(step.GetProperty("percent").GetInt32(), percent);
            Assert.Equal(step.GetProperty("weight50").GetBoolean(), level == WeightLevel.Over);
            Assert.Equal(step.GetProperty("weight90").GetBoolean(), level == WeightLevel.Major);
            Assert.Equal(step.GetProperty("overweight").GetBoolean(), level != WeightLevel.Normal);
        }
    }

    // ---- VF-21 ----

    public static TheoryData<int> AddItemCases => new(Enumerable.Range(0, 22));

    [Theory]
    [MemberData(nameof(AddItemCases))]
    public void Vf21_AddItem_MatchesPcAddItem(int index)
    {
        var c = RathenaReferenceFixture.LoadCommitted().Output("VF-21").GetProperty("cases")[index];
        var input = c.GetProperty("in");
        var add = input.GetProperty("add");
        var slots = (uint)input.GetProperty("slots").GetInt32();

        // The rows before the add, placed at slots 0..n-1 exactly like the runner does.
        var rows = input.GetProperty("rows").EnumerateArray().Select((row, slot) => RowOf(row, (uint)slot)).ToArray();
        var inventory = new CharacterInventorySnapshot(rows);
        var maxWeight = input.GetProperty("max_weight").GetInt64() >= 0
            ? (uint)input.GetProperty("max_weight").GetInt64()
            : Rules.Inventory.MaxWeight(new MaxWeightInput(0, (uint)input.GetProperty("str").GetInt32()));
        var weight = Rules.Inventory.TotalWeight(inventory, Catalog);
        Assert.Equal((uint)c.GetProperty("weight_before").GetInt32(), weight);

        var candidate = new AddItemCandidate(Catalog[add.GetProperty("nameid").GetInt32()], (uint)input.GetProperty("amount").GetInt32(),
            (byte)add.GetProperty("bound").GetInt32(), add.GetProperty("expire").GetUInt32(), add.GetProperty("unique").GetUInt64(), add.GetProperty("card0").GetUInt32());
        var decision = Rules.Inventory.EvaluateAdd(inventory, new InventoryLimits(slots, maxWeight), weight, candidate);

        // pc_additem reports 0 on success and its failure code otherwise; the numeric values are the pinned e_additem_result.
        Assert.Equal(c.GetProperty("result").GetInt32(), (int)decision.Result);
        if (!decision.Succeeded)
        {
            Assert.Equal(c.GetProperty("weight_after").GetInt32(), (int)weight);   // a refusal changes nothing
            return;
        }

        // Apply the decision the way pc_additem does and compare the resulting rows and weight.
        var after = decision.MergeInto is { } target
            ? inventory.WithUpdatedItem(target.DurableId, target.ItemId, target.Amount + candidate.Amount, target.Equip, target.Identified, target.Refine, target.Favorite, target.Bound)
            : inventory.WithNewItem(1000, candidate.Item.Id, candidate.Amount, 0, true, 0, 0, candidate.Bound);
        Assert.Equal(c.GetProperty("weight_after").GetInt32(), (int)(weight + decision.AddedWeight));
        var expectedRows = c.GetProperty("rows").EnumerateArray().Select(r => (r.GetProperty("nameid").GetInt32(), (uint)r.GetProperty("amount").GetInt32())).OrderBy(r => r.Item1).ThenBy(r => r.Item2).ToArray();
        var actualRows = after.Items.Select(r => (r.ItemId, r.Amount)).OrderBy(r => r.ItemId).ThenBy(r => r.Amount).ToArray();
        // A non-stackable multi-amount add is one row with that amount in pc_additem (callers loop); the planner never asks for it.
        Assert.Equal(expectedRows, actualRows);
        if (decision.NewSlot is { } slot) Assert.Equal(c.GetProperty("rows").EnumerateArray().Count(r => r.GetProperty("nameid").GetInt32() == candidate.Item.Id && r.GetProperty("slot").GetInt32() == (int)slot), 1);
        Assert.Equal((uint)c.GetProperty("free_slots").GetInt32(), Rules.Inventory.FreeSlots(after, slots));
    }

    private static CharacterInventoryItem RowOf(JsonElement row, uint slot) =>
        new(slot + 1, slot, row.GetProperty("nameid").GetInt32(), (uint)row.GetProperty("amount").GetInt32(), 0, true, 0, 0, (byte)row.GetProperty("bound").GetInt32(),
            new ItemInstanceDetail(0, 0, row.GetProperty("card0").GetUInt32(), 0, 0, 0, default, default, default, default, default,
                row.GetProperty("expire").GetUInt32(), row.GetProperty("unique").GetUInt64(), 0, 0));
}
