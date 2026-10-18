using System.Globalization;
using Athena.Net.MapServer.World.Items;

namespace Athena.WorldCompiler.Generation;

// Script identifier -> value table produced by the REAL pinned rAthena (script_get_constant / script_get_parameter) via
// tools/rathena-reference/run-data.ps1 and committed as rathena-constants.tsv. Lookup is case-insensitive like rAthena's
// search_str. The importer never guesses a constant: a missing name is a hard error so a stale table cannot silently
// change generated data.
internal sealed class RathenaConstantTable
{
    private readonly Dictionary<string, (long Value, bool IsParameter)> _entries = new(StringComparer.OrdinalIgnoreCase);

    internal static RathenaConstantTable Load(string tsv)
    {
        var table = new RathenaConstantTable();
        foreach (var line in tsv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length != 3) throw new InvalidDataException($"Malformed constants row '{line}'.");
            table._entries[parts[0]] = (long.Parse(parts[1], CultureInfo.InvariantCulture), parts[2] == "p");
        }
        return table;
    }

    internal int Count => _entries.Count;
    internal bool Contains(string name) => _entries.ContainsKey(name);
    internal bool TryGetConstant(string name, out long value)
    {
        if (_entries.TryGetValue(name, out var entry) && !entry.IsParameter) { value = entry.Value; return true; }
        value = 0;
        return false;
    }
    internal bool TryGetParameter(string name, out long id)
    {
        if (_entries.TryGetValue(name, out var entry) && entry.IsParameter) { id = entry.Value; return true; }
        id = 0;
        return false;
    }
    internal bool IsParameter(string name) => _entries.TryGetValue(name, out var entry) && entry.IsParameter;
}

// Port of the pinned ItemDatabase::parseBodyNode + loadingFinished (itemdb.cpp:49-1219, Renewal build) for the item_db
// import chain of db/re/item_db.yml. Same field order and defaults, same cross-field fixups. Where the C++ only warns and
// substitutes a default the same default is applied and the warning is collected (ItemDatabaseLoadResult.Warnings) so the
// data's quirks stay visible. The result is compared field by field with the real loader's dump
// (tests/WorldDataImporter.Tests/Reference/item-db-loaded.txt.gz) - see ItemDatabaseLoaderTests.
internal sealed record LoadedItem(PackItem Item, string? Script, string? EquipScript, string? UnEquipScript);
internal sealed record ItemDatabaseLoadResult(IReadOnlyList<LoadedItem> Items, IReadOnlyList<string> Warnings);

internal static class ItemDatabaseLoader
{
    internal static readonly string[] ImportOrder = ["db/re/item_db_usable.yml", "db/re/item_db_equip.yml", "db/re/item_db_etc.yml"];

    private const ulong MapIdAll = ulong.MaxValue;       // pinned MAPID_ALL = UINT64_MAX (map.hpp)
    private const uint MaxLevel = 275;                    // map.hpp MAX_LEVEL
    private const uint DefTypeMax = (uint)short.MaxValue;      // config/const.hpp DEFTYPE_MAX (Renewal)
    private const uint AreaSize = 14;                     // battle_config.area_size default (conf/battle/battle.conf)
    private const uint MaxSlots = 4;                      // mmo.hpp MAX_SLOTS
    private const uint MaxWeaponLevel = 5;                // mmo.hpp MAX_WEAPON_LEVEL (Renewal)
    private const uint MaxArmorLevel = 2;                 // mmo.hpp MAX_ARMOR_LEVEL (Renewal)
    private const uint MaxZeny = int.MaxValue;            // mmo.hpp MAX_ZENY
    private const uint ItemJAll = 0x7F;                  // itemdb.hpp ITEMJ_ALL (Renewal: all seven classes)
    private const uint ItemIdDummy = 499;                // ITEMID_DUMMY
    private const uint UnknownItemId = 512;              // UNKNOWN_ITEM_ID
    private const int ScNone = -1;                       // SC_NONE
    private const int ScMax = 2000;                      // upper bound only guards junk input; pinned SC_MAX is far below
    private const int ItMax = 19;                        // IT_MAX
    private const int MaxWeaponType = 24;                // MAX_WEAPON_TYPE
    private const int MaxAmmoType = 10;                  // MAX_AMMO_TYPE
    private const int MaxCardType = 2;                   // MAX_CARD_TYPE

    private sealed class Draft
    {
        public uint Id; public string AegisName = ""; public string Name = "";
        public byte Type = PackItemTypes.Etc; public byte Subtype;
        public uint Buy, Sell, Weight, Attack, MagicAttack, Defense, Range, Slots;
        public bool HasBuy, HasSell;
        public ulong ClassBase0 = MapIdAll, ClassBase1 = MapIdAll, ClassBase2 = MapIdAll;
        public uint ClassUpper = ItemJAll;
        public byte Sex = 2;
        public uint Equip, WeaponLevel, ArmorLevel, EquipLevelMin, EquipLevelMax = MaxLevel, Look, ViewId;
        public byte DropEffect;
        public uint DelayDuration; public int DelayStatus = ScNone; public uint StackAmount;
        public int NoUseOverride = 100, TradeOverride = 100;
        public PackItemFlags Flags;
        public string? Script, EquipScript, UnEquipScript;
    }

    internal static ItemDatabaseLoadResult Load(Func<string, string> readFile, RathenaConstantTable constants, IReadOnlyList<string>? importOrder = null)
    {
        var warnings = new List<string>();
        var drafts = new List<Draft>();
        var byId = new Dictionary<uint, Draft>();
        var byAegis = new Dictionary<string, Draft>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in importOrder ?? ImportOrder)
        {
            var root = (YamlMap)RathenaYaml.Parse(readFile(path), path);
            var body = root.Get("Body") as YamlSequence ?? throw new InvalidDataException($"{path} has no Body sequence.");
            foreach (var node in body.Items.OfType<YamlMap>())
            {
                var draft = ParseNode(node, path, constants, byAegis, warnings);
                if (!byId.TryAdd(draft.Id, draft)) throw new InvalidDataException($"{path}:{node.Line}: item id {draft.Id} is defined twice; the pinned database has no overrides, so a merge path is not implemented.");
                byAegis[draft.AegisName] = draft;
                drafts.Add(draft);
            }
        }

        foreach (var draft in drafts) Finish(draft, constants, warnings);

        var items = drafts.Select(draft => new LoadedItem(ToItem(draft), draft.Script, draft.EquipScript, draft.UnEquipScript)).ToList();
        if (!byId.ContainsKey(ItemIdDummy))
        {
            // Pinned loadingFinished adds a default-constructed item for ITEMID_DUMMY when the database has none.
            items.Add(new LoadedItem(ToItem(new Draft
            {
                Id = ItemIdDummy, AegisName = "UNKNOWN_ITEM", Name = "Unknown Item", Weight = 1, Sell = 1, ViewId = UnknownItemId,
                ClassBase0 = 0, ClassBase1 = 0, ClassBase2 = 0, ClassUpper = 0, Sex = 0, EquipLevelMax = 0, TradeOverride = 0, NoUseOverride = 0, DelayStatus = 0, Flags = PackItemFlags.Refineable,
            }), null, null, null));
        }
        items.Sort((a, b) => a.Item.Id.CompareTo(b.Item.Id));
        return new(items, warnings);
    }

    private static Draft ParseNode(YamlMap node, string path, RathenaConstantTable constants, Dictionary<string, Draft> byAegis, List<string> warnings)
    {
        var draft = new Draft();
        void Warn(string message) => warnings.Add($"{path}:{node.Line}: item {draft.Id}: {message}");
        var location = $"{path}:{node.Line}";

        draft.Id = UInt(node, "Id", location) ?? throw new InvalidDataException($"{location}: item has no Id.");
        draft.AegisName = node.Scalar("AegisName") ?? throw new InvalidDataException($"{location}: item {draft.Id} has no AegisName.");
        draft.Name = node.Scalar("Name") ?? throw new InvalidDataException($"{location}: item {draft.Id} has no Name.");
        if (byAegis.TryGetValue(draft.AegisName, out var duplicate) && duplicate.Id != draft.Id)
            throw new InvalidDataException($"{location}: AegisName '{draft.AegisName}' of item {draft.Id} is already used by item {duplicate.Id}.");

        draft.Type = PackItemTypes.Etc;
        if (node.Scalar("Type") is { } type)
        {
            if (constants.TryGetConstant("IT_" + type, out var typeValue) && typeValue is >= 0 and < ItMax) draft.Type = (byte)typeValue;
            else Warn($"invalid item type {type}, defaulting to IT_ETC");
        }

        if (node.Scalar("SubType") is { } subType)
        {
            if (draft.Type == PackItemTypes.Weapon) draft.Subtype = Bounded(constants, "W_" + subType, 0, MaxWeaponType - 1, 0, "weapon type", Warn);
            else if (draft.Type == PackItemTypes.Ammo) draft.Subtype = Bounded(constants, "AMMO_" + subType, 1, MaxAmmoType - 1, 0, "ammo type", Warn);
            else if (draft.Type == PackItemTypes.Card) draft.Subtype = Bounded(constants, "CARD_" + subType, 0, MaxCardType - 1, 0, "card type", Warn);
            else throw new InvalidDataException($"{location}: item {draft.Id} has a SubType but type {draft.Type} does not support one (the pinned loader skips the whole item).");
        }

        if (UInt(node, "Buy", location) is { } buy) { draft.Buy = Math.Min(buy, MaxZeny); draft.HasBuy = true; }
        if (UInt(node, "Sell", location) is { } sell) { draft.Sell = Math.Min(sell, MaxZeny); draft.HasSell = true; }
        draft.Weight = UInt(node, "Weight", location) ?? 0;
        draft.Attack = UInt(node, "Attack", location) ?? 0;
        draft.MagicAttack = UInt(node, "MagicAttack", location) ?? 0;
        draft.Defense = Math.Min(UInt(node, "Defense", location) ?? 0, DefTypeMax);
        draft.Range = Math.Min(UInt(node, "Range", location) ?? 0, AreaSize);
        draft.Slots = Math.Min(UInt(node, "Slots", location) ?? 0, MaxSlots);

        if (node.Get("Jobs") is YamlMap jobs)
        {
            draft.ClassBase0 = draft.ClassBase1 = draft.ClassBase2 = 0;
            if (jobs.Scalar("All") is { } all) ApplyJob(draft, MapIdAll, ParseBool(all, location));
            foreach (var entry in jobs.Entries)
            {
                if (entry.Key == "All") continue;
                if (!constants.TryGetConstant("EAJ_" + entry.Key, out var jobConstant))
                {
                    Warn($"invalid item job {entry.Key}, defaulting to All");
                    ApplyJob(draft, MapIdAll, true);
                    break;
                }
                ApplyJob(draft, (ulong)jobConstant, ParseBool(((YamlScalar)entry.Value).Value, location));
            }
        }

        if (node.Get("Classes") is YamlMap classes)
        {
            draft.ClassUpper = 0;
            if (classes.Scalar("All") is { } all) draft.ClassUpper = ParseBool(all, location) ? ItemJAll : 0;
            foreach (var entry in classes.Entries)
            {
                if (entry.Key == "All") continue;
                if (!constants.TryGetConstant("ITEMJ_" + entry.Key, out var classConstant))
                {
                    Warn($"invalid class upper {entry.Key}, defaulting to All");
                    draft.ClassUpper |= ItemJAll;
                    break;
                }
                if (ParseBool(((YamlScalar)entry.Value).Value, location)) draft.ClassUpper |= (uint)classConstant;
                else draft.ClassUpper &= ~(uint)classConstant;
            }
        }

        draft.Sex = 2;
        if (node.Scalar("Gender") is { } gender)
        {
            if (constants.TryGetConstant("SEX_" + gender, out var sexValue) && sexValue is >= 0 and <= 2) draft.Sex = (byte)sexValue;
            else Warn($"invalid item gender {gender}, defaulting to SEX_BOTH");
        }
        draft.Sex = DefaultGender(draft, constants);

        if (node.Get("Locations") is YamlMap locations)
        {
            var shadowGear = ShadowGearMask(constants);
            foreach (var entry in locations.Entries)
            {
                if (!constants.TryGetConstant("EQP_" + entry.Key, out var equipConstant))
                {
                    Warn($"invalid equip location {entry.Key}, defaulting to IT_ETC");
                    draft.Type = PackItemTypes.Etc;
                    break;
                }
                if (ParseBool(((YamlScalar)entry.Value).Value, location))
                {
                    if ((equipConstant & shadowGear) != 0 && draft.Type != PackItemTypes.ShadowGear)
                    {
                        Warn($"equip location {entry.Key} is not valid for a non shadow gear item, defaulting to IT_ETC");
                        draft.Type = PackItemTypes.Etc;
                    }
                    draft.Equip |= (uint)equipConstant;
                }
                else draft.Equip &= ~(uint)equipConstant;
            }
        }
        else if (IsEquip(draft.Type))
        {
            Warn("equippable item has no equip location, defaulting to IT_ETC");
            draft.Type = PackItemTypes.Etc;
        }

        if (UInt(node, "WeaponLevel", location) is { } weaponLevel)
        {
            if (weaponLevel > MaxWeaponLevel) { Warn($"invalid weapon level {weaponLevel}, defaulting to 0"); weaponLevel = 0; }
            if (draft.Type != PackItemTypes.Weapon) { Warn("item is not a weapon, weapon level defaults to 0"); weaponLevel = 0; }
            draft.WeaponLevel = weaponLevel;
        }
        if (UInt(node, "ArmorLevel", location) is { } armorLevel)
        {
            if (armorLevel > MaxArmorLevel) { Warn($"invalid armor level {armorLevel}, defaulting to 0"); armorLevel = 0; }
            if (draft.Type != PackItemTypes.Armor) { Warn("item is not an armor, armor level defaults to 0"); armorLevel = 0; }
            draft.ArmorLevel = armorLevel;
        }

        if (UInt(node, "EquipLevelMin", location) is { } elv) draft.EquipLevelMin = Math.Min(elv, MaxLevel);
        if (UInt(node, "EquipLevelMax", location) is { } elvMax)
        {
            if (elvMax < draft.EquipLevelMin) elvMax = draft.EquipLevelMin;
            draft.EquipLevelMax = Math.Min(elvMax, MaxLevel);
        }

        var refineable = node.Scalar("Refineable") is { } refine && ParseBool(refine, location);
        draft.Flags = refineable ? PackItemFlags.Refineable : PackItemFlags.None;
        if (node.Scalar("Gradable") is { } gradable && ParseBool(gradable, location)) draft.Flags |= PackItemFlags.Gradable;

        draft.Look = UInt(node, "View", location) ?? 0;
        if (node.Scalar("AliasName") is { } alias)
        {
            if (!byAegis.TryGetValue(alias, out var view)) throw new InvalidDataException($"{location}: item {draft.Id} aliases unknown item '{alias}' (the pinned loader skips the whole item).");
            draft.ViewId = view.Id;
        }

        if (node.Get("Flags") is YamlMap flags)
        {
            var stackable = IsStackable(draft.Type);
            void Flag(string key, PackItemFlags flag, bool requiresStackable = false)
            {
                if (flags.Scalar(key) is not { } value) return;
                var active = ParseBool(value, location);
                if (requiresStackable && !stackable && active) { Warn($"non-stackable item cannot enable {key}"); active = false; }
                if (active) draft.Flags |= flag;
            }
            Flag("BuyingStore", PackItemFlags.BuyingStore, requiresStackable: true);
            Flag("DeadBranch", PackItemFlags.DeadBranch);
            Flag("Container", PackItemFlags.Container);
            Flag("UniqueId", PackItemFlags.UniqueId, requiresStackable: true);
            Flag("BindOnEquip", PackItemFlags.BindOnEquip);
            Flag("DropAnnounce", PackItemFlags.DropAnnounce);
            Flag("NoConsume", PackItemFlags.NoConsume);
            if (flags.Scalar("DropEffect") is { } effect)
            {
                if (constants.TryGetConstant("DROPEFFECT_" + effect, out var effectValue) && effectValue is >= 0 and <= 8) draft.DropEffect = (byte)effectValue;
                else Warn($"invalid item drop effect {effect}, defaulting to DROPEFFECT_NONE");
            }
        }

        if (node.Get("Delay") is YamlMap delay)
        {
            draft.DelayDuration = UInt(delay, "Duration", location) ?? 0;
            if (delay.Scalar("Status") is { } status)
            {
                if (constants.TryGetConstant("SC_" + status, out var statusValue) && statusValue is >= ScNone and < ScMax) draft.DelayStatus = (int)statusValue;
                else Warn($"invalid item delay status {status}, defaulting to SC_NONE");
            }
        }

        if (node.Get("Stack") is YamlMap stack)
        {
            if (UInt(stack, "Amount", location) is { } amount)
            {
                if (!IsStackable(draft.Type)) { Warn("non-stackable item cannot be enabled for stacking"); amount = 0; }
                draft.StackAmount = amount;
            }
            if (stack.Scalar("Inventory") is { } inv && ParseBool(inv, location)) draft.Flags |= PackItemFlags.StackInventory;
            if (stack.Scalar("Cart") is { } cart && ParseBool(cart, location)) draft.Flags |= PackItemFlags.StackCart;
            if (stack.Scalar("Storage") is { } storage && ParseBool(storage, location)) draft.Flags |= PackItemFlags.StackStorage;
            if (stack.Scalar("GuildStorage") is { } guild && ParseBool(guild, location)) draft.Flags |= PackItemFlags.StackGuildStorage;
        }

        if (node.Get("NoUse") is YamlMap noUse)
        {
            if (UInt(noUse, "Override", location) is { } noUseOverride) draft.NoUseOverride = (int)Math.Min(noUseOverride, 100);
            if (noUse.Scalar("Sitting") is { } sitting && ParseBool(sitting, location)) draft.Flags |= PackItemFlags.NoUseSitting;
        }

        if (node.Get("Trade") is YamlMap trade)
        {
            if (UInt(trade, "Override", location) is { } tradeOverride) draft.TradeOverride = (int)Math.Min(tradeOverride, 100);
            void Trade(string key, PackItemFlags flag)
            {
                if (trade.Scalar(key) is { } value && ParseBool(value, location)) draft.Flags |= flag;
            }
            Trade("NoDrop", PackItemFlags.NoDrop); Trade("NoTrade", PackItemFlags.NoTrade); Trade("TradePartner", PackItemFlags.TradePartner);
            Trade("NoSell", PackItemFlags.NoSell); Trade("NoCart", PackItemFlags.NoCart); Trade("NoStorage", PackItemFlags.NoStorage);
            Trade("NoGuildStorage", PackItemFlags.NoGuildStorage); Trade("NoMail", PackItemFlags.NoMail); Trade("NoAuction", PackItemFlags.NoAuction);
        }

        // parse_script returns no code for a block that holds only comments (e.g. "/* TODO */"), so such a slot has no script.
        draft.Script = WithCode(node.Scalar("Script"), path);
        draft.EquipScript = WithCode(node.Scalar("EquipScript"), path);
        draft.UnEquipScript = WithCode(node.Scalar("UnEquipScript"), path);

        // The pinned loader remembers DELAYCONSUME_TEMP separately from the final type: Type: DelayConsume is rewritten to
        // IT_USABLE in loadingFinished, so the marker must be set from the parsed type here and survive that rewrite.
        if (draft.Type == PackItemTypes.DelayConsume) draft.Flags |= PackItemFlags.DelayConsumeTemp;
        return draft;
    }

    private static string? WithCode(string? script, string path) =>
        script is not null && new Athena.WorldCompiler.Rathena.RathenaLexer(script, path).Lex().Any(token => token.Kind != Athena.WorldCompiler.Rathena.Syntax.TokenKind.EndOfFile) ? script : null;

    private static void Finish(Draft draft, RathenaConstantTable constants, List<string> warnings)
    {
        void Warn(string message) => warnings.Add($"item {draft.Id}: {message}");

        if (draft.Type == PackItemTypes.DelayConsume) draft.Type = PackItemTypes.Usable;
        else draft.Flags &= ~PackItemFlags.DelayConsumeTemp;

        if (draft.Type == PackItemTypes.Weapon)
        {
            if (draft.WeaponLevel == 0) { Warn("weapon without weapon level, defaulting to 1"); draft.WeaponLevel = 1; }
            if (draft.ArmorLevel != 0) { Warn("weapon has an armor level, defaulting to 0"); draft.ArmorLevel = 0; }
        }
        else if (draft.Type == PackItemTypes.Armor)
        {
            if (draft.ArmorLevel == 0) { Warn("armor without armor level, defaulting to 1"); draft.ArmorLevel = 1; }
            if (draft.WeaponLevel != 0) { Warn("armor has a weapon level, defaulting to 0"); draft.WeaponLevel = 0; }
        }
        else
        {
            if (draft.WeaponLevel != 0) { Warn("not a weapon but has a weapon level, defaulting to 0"); draft.WeaponLevel = 0; }
            if (draft.ArmorLevel != 0) { Warn("not an armor but has an armor level, defaulting to 0"); draft.ArmorLevel = 0; }
        }

        if (draft.Type != PackItemTypes.Armor && draft.Type != PackItemTypes.ShadowGear && draft.Defense > 0) { Warn("defense defaults to 0"); draft.Defense = 0; }
        if (draft.Type != PackItemTypes.Weapon && draft.Type != PackItemTypes.Ammo && draft.Attack > 0) { Warn("attack defaults to 0"); draft.Attack = 0; }
        if (draft.Type != PackItemTypes.Weapon)
        {
            if (draft.MagicAttack > 0) { Warn("magic attack defaults to 0"); draft.MagicAttack = 0; }
            if (draft.Range > 0) { Warn("range defaults to 0"); draft.Range = 0; }
        }

        if (!draft.HasBuy && draft.HasSell) draft.Buy = draft.Sell * 2;
        else if (draft.HasBuy && !draft.HasSell) draft.Sell = draft.Buy / 2;

        if (draft.Buy / 124.0 < draft.Sell / 75.0)
        {
            Warn($"buy/sell [{draft.Buy}/{draft.Sell}] allows a zeny exploit, defaulting Sell to 1");
            draft.Sell = 1;
        }

        constants.TryGetConstant("EQP_Left_Hand", out var shield);
        if (draft.Type == PackItemTypes.Armor && (draft.Equip & (uint)shield) != 0 && draft.Look == 0)
        {
            Warn("shield without a view id, defaulting to Guard");
            draft.Look = 1;
        }
    }

    private static PackItem ToItem(Draft d) => new(
        d.Id, d.AegisName, d.Name, d.Type, d.Subtype, (int)d.Buy, (int)d.Sell, d.Weight, d.Attack, d.MagicAttack, d.Defense, d.Range, d.Slots,
        d.ClassBase0, d.ClassBase1, d.ClassBase2, d.ClassUpper, d.Sex, d.Equip, d.WeaponLevel, d.ArmorLevel, d.EquipLevelMin, d.EquipLevelMax,
        d.Look, d.ViewId, d.DropEffect, d.DelayDuration, d.DelayStatus, d.StackAmount, d.NoUseOverride, d.TradeOverride, d.Flags, -1, -1, -1, 0);

    private static bool IsEquip(byte type) => type is PackItemTypes.Weapon or PackItemTypes.Armor or PackItemTypes.Ammo or PackItemTypes.ShadowGear;
    private static bool IsStackable(byte type) => type is not (PackItemTypes.Weapon or PackItemTypes.Armor or PackItemTypes.PetEgg or PackItemTypes.PetArmor or PackItemTypes.ShadowGear);

    // Pinned ItemDatabase::defaultGender (itemdb.cpp:1227): Grom/Bride rings, instruments (male) and whips (female) are forced.
    private static byte DefaultGender(Draft draft, RathenaConstantTable constants)
    {
        const uint WeddingRingM = 2634, WeddingRingF = 2635;
        if (draft.Id == WeddingRingM) return 1;
        if (draft.Id == WeddingRingF) return 0;
        if (draft.Type == PackItemTypes.Weapon)
        {
            if (draft.Subtype == 13) return 1;   // W_MUSICAL
            if (draft.Subtype == 14) return 0;   // W_WHIP
        }
        return draft.Sex;
    }

    private static uint ShadowGearMask(RathenaConstantTable constants)
    {
        uint mask = 0;
        foreach (var name in new[] { "EQP_Shadow_Armor", "EQP_Shadow_Weapon", "EQP_Shadow_Shield", "EQP_Shadow_Shoes", "EQP_Shadow_Right_Accessory", "EQP_Shadow_Left_Accessory" })
        {
            if (!constants.TryGetConstant(name, out var value)) throw new InvalidDataException($"The constants table has no {name}.");
            mask |= (uint)value;
        }
        return mask;
    }

    // Pinned itemdb_jobid2mapid (itemdb.cpp:3201).
    private static void ApplyJob(Draft draft, ulong jobMask, bool active)
    {
        ulong t0 = 0, t1 = 0, t2 = 0;
        if (jobMask != MapIdAll)
        {
            var job = 1UL << (int)(jobMask & 0xff);
            if ((jobMask & 0x100) != 0) t1 = job;
            else if ((jobMask & 0x200) != 0) t2 = job;
            else t0 = job;
        }
        else t0 = t1 = t2 = MapIdAll;

        if (t0 != 0) draft.ClassBase0 = active ? draft.ClassBase0 | t0 : draft.ClassBase0 & ~t0;
        if (t1 != 0) draft.ClassBase1 = active ? draft.ClassBase1 | t1 : draft.ClassBase1 & ~t1;
        if (t2 != 0) draft.ClassBase2 = active ? draft.ClassBase2 | t2 : draft.ClassBase2 & ~t2;
    }

    private static byte Bounded(RathenaConstantTable constants, string name, int minimum, int maximum, byte fallback, string what, Action<string> warn)
    {
        if (constants.TryGetConstant(name, out var value) && value >= minimum && value <= maximum) return (byte)value;
        warn($"invalid {what} {name}, defaulting");
        return fallback;
    }

    private static uint? UInt(YamlMap node, string key, string location)
    {
        if (node.Scalar(key) is not { } text) return null;
        if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"{location}: '{key}: {text}' is not an unsigned integer.");
        return value;
    }

    private static bool ParseBool(string text, string location) => text.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => throw new InvalidDataException($"{location}: '{text}' is not a boolean."),
    };
}
