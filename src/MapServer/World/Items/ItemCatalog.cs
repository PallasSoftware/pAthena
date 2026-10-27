using System.Collections.Frozen;

namespace Athena.Net.MapServer.World.Items;

// The runtime item registry: every pinned item_db entry as an immutable ItemDefinition, built once from the generated item data
// pack (MapData/AthenaItems.bin). The pack is the only source - no per-item C# is generated - so the catalog scales with the
// dataset. Definitions keep their pinned concrete type (Weapon/Armor/Etc/Usable/Healing/DelayConsume/Card/Ammo/...) so code
// that branches on type keeps working, and carry the loader's remaining fields in ItemDefinition.Meta.
public sealed class ItemCatalog
{
    private static readonly Lazy<ItemCatalog> Production = new(() => Open(Path.Combine(AppContext.BaseDirectory, "MapData", "AthenaItems.bin")));

    public static ItemCatalog Current => Production.Value;

    public string RathenaCommit { get; }
    public FrozenDictionary<int, ItemDefinition> ById { get; }
    // Pinned item_db lookups by AegisName (e.g. mob_db.yml `Drops:` entries, which name items by
    // AegisName, never by id) are case-insensitive, matching itemdb_search_aegisname's own strcasecmp.
    public FrozenDictionary<string, ItemDefinition> ByAegisName { get; }
    public IReadOnlyList<PackScript> Scripts { get; }
    public FrozenDictionary<uint, PackItemGroup> Groups { get; }
    // Keyed by the client-matching random-option id (item_randomopt_db.yml Id, enumvar.lub), NOT a pack.Scripts
    // index - resolving a row's own InventoryOption.Id through this lookup is how EquipmentBonusResolver.Resolve
    // gets the PackScript a random-option slot should execute (parallel to ById for equip/card scripts).
    public FrozenDictionary<uint, PackScript> RandomOptions { get; }
    // Every db/re/item_combos.yml entry, resolved to its real PackScript (ComboResolver walks this list looking
    // for an alternative item-id set that is a subset of the equipped item ids).
    public IReadOnlyList<ComboDefinition> Combos { get; }

    private ItemCatalog(string commit, FrozenDictionary<int, ItemDefinition> byId, IReadOnlyList<PackScript> scripts,
        FrozenDictionary<uint, PackItemGroup> groups, FrozenDictionary<uint, PackScript> randomOptions, IReadOnlyList<ComboDefinition> combos)
    {
        RathenaCommit = commit;
        ById = byId;
        ByAegisName = byId.Values.ToFrozenDictionary(item => item.AegisName, StringComparer.OrdinalIgnoreCase);
        Scripts = scripts;
        Groups = groups;
        RandomOptions = randomOptions;
        Combos = combos;
    }

    public static ItemCatalog Open(string path) => Create(ItemPackFormat.Read(File.ReadAllBytes(path)));

    public static ItemCatalog Create(ItemPack pack)
    {
        var definitions = new Dictionary<int, ItemDefinition>(pack.Items.Count);
        foreach (var item in pack.Items) definitions.Add((int)item.Id, Build(item, pack));
        var randomOptions = pack.RandomOptions.ToFrozenDictionary(option => option.Id, option => pack.Scripts[option.ScriptIndex]);
        var combos = pack.Combos.Select(combo => new ComboDefinition(combo.Alternatives, pack.Scripts[combo.ScriptIndex])).ToArray();
        return new(pack.RathenaCommit, definitions.ToFrozenDictionary(), pack.Scripts, pack.Groups.ToFrozenDictionary(group => group.GroupId), randomOptions, combos);
    }

    private static ItemDefinition Build(PackItem item, ItemPack pack)
    {
        PackScript? Script(int index) => index < 0 ? null : pack.Scripts[index];
        var meta = new ItemMetadata(item, Script(item.ScriptIndex), Script(item.EquipScriptIndex), Script(item.UnEquipScriptIndex));
        var id = (int)item.Id;
        var stackable = item.Type is not (PackItemTypes.Weapon or PackItemTypes.Armor or PackItemTypes.PetEgg or PackItemTypes.PetArmor or PackItemTypes.ShadowGear);
        // client_nameid() (clif.cpp:144-151): the alias view id when the row declares one, else the item's own id.
        var clientViewId = item.ViewId != 0 ? (int)item.ViewId : id;
        var source = new WorldSourceInfo("rAthena", pack.RathenaCommit, "legacy/rathena/db/re/item_db.yml", 0);

        ItemDefinition definition = item.Type switch
        {
            PackItemTypes.Weapon => new WeaponItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, (int)item.Attack, (int)item.WeaponLevel,
                (WeaponType)item.Subtype, (int)item.Range, item.Equip, source, (int)item.MagicAttack),
            PackItemTypes.Armor => new ArmorItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, (int)item.Defense, item.Equip, source),
            PackItemTypes.ShadowGear => new ShadowGearItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source, (int)item.Defense, item.Equip),
            PackItemTypes.Ammo => new AmmoItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source, (int)item.Attack, item.Subtype, item.Equip),
            PackItemTypes.Card => new CardItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source, item.Equip, item.Subtype),
            PackItemTypes.Healing => new HealingItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source),
            PackItemTypes.Usable when item.Flags.HasFlag(PackItemFlags.DelayConsumeTemp) => new DelayConsumeItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source),
            PackItemTypes.Usable => new UsableItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source, Grants(Script(item.ScriptIndex))),
            PackItemTypes.PetEgg => new PetEggItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source),
            PackItemTypes.PetArmor => new PetArmorItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source),
            PackItemTypes.Cash => new CashItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source),
            _ => new EtcItemDefinition(id, item.AegisName, item.Name, stackable, clientViewId, source),
        };
        return definition with { Meta = meta };
    }

    // A Usable whose whole script is a sequence of constant `getitem <id>,<amount>;` statements is a container (First Aid Box, ...).
    // Anything else keeps its script in Meta and has no Grants.
    private static IReadOnlyList<ItemGrantDefinition>? Grants(PackScript? script)
    {
        if (script?.Program is not { Kind: IrKind.Block } block || block.Items.Count == 0) return null;
        var grants = new List<ItemGrantDefinition>();
        foreach (var statement in block.Items)
        {
            if (statement is not { Kind: IrKind.Command, Text: "getitem", Items: [{ Kind: IrKind.Int } itemId, { Kind: IrKind.Int } amount] }) return null;
            grants.Add(new ItemGrantDefinition((int)itemId.Number, (uint)amount.Number));
        }
        return grants;
    }
}
