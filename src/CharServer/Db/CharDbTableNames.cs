namespace Athena.Net.CharServer.Db;

public sealed class CharDbTableNames
{
    public string CharTable { get; init; } = "char";
    public string InventoryTable { get; init; } = "inventory";
    public string SkillTable { get; init; } = "skill";
    public string HotkeyTable { get; init; } = "hotkey";
    public string QuestTable { get; init; } = "quest";
    public string ContainerItemTable { get; init; } = "container_item";
    public string InventoryOperationTable { get; init; } = "inventory_operation";
    public string PendingGroundDropTable { get; init; } = "pending_ground_drop";
}
