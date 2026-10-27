using Athena.Net.CharServer.Db.Entities;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.CharServer.Inventory;

// Copies between the persisted item columns (any container table) and the wire row. Pure data movement: no validation, no rules.
internal static class ItemRowMapper
{
    internal static InventoryItemRow ToRow(IItemRowColumns e) => new(
        e.Id, e.NameId, e.Amount, e.Equip, (byte)e.Identify, e.Refine, e.Attribute, e.Favorite, e.Bound, e.EnchantGrade,
        e.Card0, e.Card1, e.Card2, e.Card3,
        new(e.OptionId0, e.OptionVal0, e.OptionParm0), new(e.OptionId1, e.OptionVal1, e.OptionParm1), new(e.OptionId2, e.OptionVal2, e.OptionParm2),
        new(e.OptionId3, e.OptionVal3, e.OptionParm3), new(e.OptionId4, e.OptionVal4, e.OptionParm4),
        e.ExpireTime, e.UniqueId, e.EquipSwitch, e.Revision);

    /// <summary>Fills every column of a NEW row from the template (the caller sets owner columns and Revision).</summary>
    internal static void ApplyNew(IItemRowColumns e, InventoryItemRow row)
    {
        e.NameId = row.ItemId;
        e.Amount = row.Amount;
        ApplyMutable(e, row);
    }

    /// <summary>Replaces every mutable column except the amount (changed only by add/remove mutations).</summary>
    internal static void ApplyMutable(IItemRowColumns e, InventoryItemRow row)
    {
        e.NameId = row.ItemId;
        e.Equip = row.Equip;
        e.Identify = row.Identify;
        e.Refine = row.Refine;
        e.Attribute = row.Attribute;
        e.Favorite = row.Favorite;
        e.Bound = row.Bound;
        e.EnchantGrade = row.EnchantGrade;
        e.Card0 = row.Card0; e.Card1 = row.Card1; e.Card2 = row.Card2; e.Card3 = row.Card3;
        e.OptionId0 = row.Option0.Id; e.OptionVal0 = row.Option0.Value; e.OptionParm0 = row.Option0.Param;
        e.OptionId1 = row.Option1.Id; e.OptionVal1 = row.Option1.Value; e.OptionParm1 = row.Option1.Param;
        e.OptionId2 = row.Option2.Id; e.OptionVal2 = row.Option2.Value; e.OptionParm2 = row.Option2.Param;
        e.OptionId3 = row.Option3.Id; e.OptionVal3 = row.Option3.Value; e.OptionParm3 = row.Option3.Param;
        e.OptionId4 = row.Option4.Id; e.OptionVal4 = row.Option4.Value; e.OptionParm4 = row.Option4.Param;
        e.ExpireTime = row.ExpireTime;
        e.UniqueId = row.UniqueId;
        e.EquipSwitch = row.EquipSwitch;
    }
}
