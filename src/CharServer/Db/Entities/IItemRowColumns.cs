namespace Athena.Net.CharServer.Db.Entities;

// The persisted item columns shared by every container table (inventory, cart, storage, guild storage), so one mapper and one
// operation executor serve them all. Revision is the optimistic-concurrency token bumped by every mutation of the row.
public interface IItemRowColumns
{
    uint Id { get; set; }
    uint NameId { get; set; }
    uint Amount { get; set; }
    uint Equip { get; set; }
    short Identify { get; set; }
    byte Refine { get; set; }
    byte Attribute { get; set; }
    uint Card0 { get; set; }
    uint Card1 { get; set; }
    uint Card2 { get; set; }
    uint Card3 { get; set; }
    short OptionId0 { get; set; }
    short OptionVal0 { get; set; }
    byte OptionParm0 { get; set; }
    short OptionId1 { get; set; }
    short OptionVal1 { get; set; }
    byte OptionParm1 { get; set; }
    short OptionId2 { get; set; }
    short OptionVal2 { get; set; }
    byte OptionParm2 { get; set; }
    short OptionId3 { get; set; }
    short OptionVal3 { get; set; }
    byte OptionParm3 { get; set; }
    short OptionId4 { get; set; }
    short OptionVal4 { get; set; }
    byte OptionParm4 { get; set; }
    uint ExpireTime { get; set; }
    byte Favorite { get; set; }
    byte Bound { get; set; }
    ulong UniqueId { get; set; }
    uint EquipSwitch { get; set; }
    byte EnchantGrade { get; set; }
    uint Revision { get; set; }
}
