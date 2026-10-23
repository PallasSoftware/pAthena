namespace Athena.Net.MapServer.World.Items;

// One db/re/item_combos.yml entry: Alternatives holds every independent item-id SET that activates this SAME
// shared Script (any ONE equipped set is sufficient - they are not combined). Mirrors PackCombo exactly but
// with the script already resolved, same relationship ItemDefinition has to PackItem.
public sealed record ComboDefinition(IReadOnlyList<IReadOnlyList<uint>> Alternatives, PackScript Script);

// Pinned status_calc_pc_additional's own combo loop (status.cpp, over sd->combos): a combo activates when
// EVERY item in at least one of its alternative sets is CURRENTLY equipped (pc_checkcombo/pc_calc_skilltree's
// own combo-matching, pc.cpp) - not merely present in the inventory. getrefine() inside a combo's own script
// has no single "current item" (a combo spans several equipped rows), so combo scripts never reference it -
// this resolver always passes refine 0, matching how a card's own script call already does.
public static class ComboResolver
{
    // Returns every combo whose activation condition is currently met. presentItemIds is the COMBINED pool of
    // every id that counts toward a combo match (pinned pc_checkcombo, pc.cpp:11803-11900): each equipped row's
    // own ItemId ONCE per row, plus each of that row's nonzero Card0-3 - cards are real, common combo members
    // (e.g. S_Helmut_Card/Bathory_Card), matched identically to a plain equip item, just read from a card slot
    // instead of the row's own ItemId. Duplicates allowed and meaningful: a combo alternative with a repeated
    // id needs that id present that many times (distinct equipped rows or card slots), matching the pinned
    // loop's own per-instance (never per-distinct-id) accounting.
    public static IReadOnlyList<ComboDefinition> ActiveCombos(IReadOnlyList<ComboDefinition> combos, IReadOnlyList<uint> presentItemIds)
    {
        var active = new List<ComboDefinition>();
        foreach (var combo in combos)
            if (combo.Alternatives.Any(alternative => IsSatisfied(alternative, presentItemIds)))
                active.Add(combo);
        return active;
    }

    private static bool IsSatisfied(IReadOnlyList<uint> alternative, IReadOnlyList<uint> presentItemIds)
    {
        var remaining = new List<uint>(presentItemIds);
        foreach (var required in alternative)
        {
            var index = remaining.IndexOf(required);
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }
        return true;
    }
}
