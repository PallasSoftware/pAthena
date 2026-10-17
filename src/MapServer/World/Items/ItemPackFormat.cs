using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Athena.Net.MapServer.World.Items;

// Item data pack: the compact, versioned, deterministic artifact the MapServer loads once at startup instead of 29k
// generated C# records (Feature 16 decision D3). This single source file is compiled into BOTH the WorldDataImporter
// (writer) and the MapServer (reader), exactly like AthenaMapPackFormat, so the two ends cannot drift. It holds plain
// data only - no gameplay algorithm. Region-specific behavior never lives here.
//
// Layout (little-endian): magic "AIP1" | version u16 | commit[40] | contentHash[32] | body
// contentHash = SHA-256 of the body; the reader verifies it. The body is a string table followed by sections.

public static class PackItemTypes
{
    // Pinned enum item_types (mmo.hpp) AFTER the loader's post-processing: IT_DELAYCONSUME is stored as IT_USABLE
    // with ItemFlags.DelayConsumeTemp set (ItemDatabase::loadingFinished), and that is kept verbatim here.
    public const byte Healing = 0, Usable = 2, Etc = 3, Armor = 4, Weapon = 5, Card = 6, PetEgg = 7, PetArmor = 8, Ammo = 10, DelayConsume = 11, ShadowGear = 12, Cash = 18;
}

[Flags]
public enum PackItemFlags : uint
{
    None = 0,
    Refineable = 1 << 0,
    Gradable = 1 << 1,
    BuyingStore = 1 << 2,
    DeadBranch = 1 << 3,
    Container = 1 << 4,
    UniqueId = 1 << 5,
    BindOnEquip = 1 << 6,
    DropAnnounce = 1 << 7,
    DelayConsumeTemp = 1 << 8,   // pinned DELAYCONSUME_TEMP (the item was Type: DelayConsume)
    NoConsume = 1 << 9,          // pinned DELAYCONSUME_NOCONSUME
    StackInventory = 1 << 10,
    StackCart = 1 << 11,
    StackStorage = 1 << 12,
    StackGuildStorage = 1 << 13,
    NoUseSitting = 1 << 14,
    NoDrop = 1 << 15,
    NoTrade = 1 << 16,
    TradePartner = 1 << 17,
    NoSell = 1 << 18,
    NoCart = 1 << 19,
    NoStorage = 1 << 20,
    NoGuildStorage = 1 << 21,
    NoMail = 1 << 22,
    NoAuction = 1 << 23,
}

// One loaded item_db entry (pinned struct item_data after ItemDatabase::parseBodyNode + loadingFinished).
public sealed record PackItem(
    uint Id,
    string AegisName,
    string Name,
    byte Type,
    byte Subtype,
    int Buy,
    int Sell,
    uint Weight,
    uint Attack,
    uint MagicAttack,
    uint Defense,
    uint Range,
    uint Slots,
    ulong ClassBase0,
    ulong ClassBase1,
    ulong ClassBase2,
    uint ClassUpper,
    byte Sex,
    uint Equip,
    uint WeaponLevel,
    uint ArmorLevel,
    uint EquipLevelMin,
    uint EquipLevelMax,
    uint Look,
    uint ViewId,
    byte DropEffect,
    uint DelayDuration,
    int DelayStatus,
    uint StackAmount,
    int NoUseOverride,
    int TradeOverride,
    PackItemFlags Flags,
    int ScriptIndex,
    int EquipScriptIndex,
    int UnEquipScriptIndex)
{
    // One line per item in the same field order and number format the reference runner (reference_runner.cpp
    // dump_items) prints from the real pinned loader, so the committed loader evidence can be compared byte for byte.
    public string ToCanonicalLine()
    {
        var f = Flags;
        static string B(bool value) => value ? "1" : "0";
        var delayConsume = (f.HasFlag(PackItemFlags.DelayConsumeTemp) ? 1 : 0) | (f.HasFlag(PackItemFlags.NoConsume) ? 2 : 0);
        var fields = new string[]
        {
            Id.ToString(CultureInfo.InvariantCulture), AegisName, Name,
            Type.ToString(CultureInfo.InvariantCulture), Subtype.ToString(CultureInfo.InvariantCulture),
            Buy.ToString(CultureInfo.InvariantCulture), Sell.ToString(CultureInfo.InvariantCulture),
            Weight.ToString(CultureInfo.InvariantCulture), Attack.ToString(CultureInfo.InvariantCulture), MagicAttack.ToString(CultureInfo.InvariantCulture),
            Defense.ToString(CultureInfo.InvariantCulture), Range.ToString(CultureInfo.InvariantCulture), Slots.ToString(CultureInfo.InvariantCulture),
            ((long)ClassBase0).ToString(CultureInfo.InvariantCulture), ((long)ClassBase1).ToString(CultureInfo.InvariantCulture), ((long)ClassBase2).ToString(CultureInfo.InvariantCulture),
            ClassUpper.ToString(CultureInfo.InvariantCulture), Sex.ToString(CultureInfo.InvariantCulture), Equip.ToString(CultureInfo.InvariantCulture),
            WeaponLevel.ToString(CultureInfo.InvariantCulture), ArmorLevel.ToString(CultureInfo.InvariantCulture),
            EquipLevelMin.ToString(CultureInfo.InvariantCulture), EquipLevelMax.ToString(CultureInfo.InvariantCulture),
            B(!f.HasFlag(PackItemFlags.Refineable)), B(f.HasFlag(PackItemFlags.Gradable)),
            Look.ToString(CultureInfo.InvariantCulture), ViewId.ToString(CultureInfo.InvariantCulture),
            B(f.HasFlag(PackItemFlags.BuyingStore)), B(f.HasFlag(PackItemFlags.DeadBranch)), B(f.HasFlag(PackItemFlags.Container)),
            B(f.HasFlag(PackItemFlags.UniqueId)), B(f.HasFlag(PackItemFlags.BindOnEquip)), B(f.HasFlag(PackItemFlags.DropAnnounce)),
            delayConsume.ToString(CultureInfo.InvariantCulture), DropEffect.ToString(CultureInfo.InvariantCulture),
            DelayDuration.ToString(CultureInfo.InvariantCulture), DelayStatus.ToString(CultureInfo.InvariantCulture),
            StackAmount.ToString(CultureInfo.InvariantCulture), B(f.HasFlag(PackItemFlags.StackInventory)), B(f.HasFlag(PackItemFlags.StackCart)),
            B(f.HasFlag(PackItemFlags.StackStorage)), B(f.HasFlag(PackItemFlags.StackGuildStorage)),
            NoUseOverride.ToString(CultureInfo.InvariantCulture), B(f.HasFlag(PackItemFlags.NoUseSitting)), TradeOverride.ToString(CultureInfo.InvariantCulture),
            B(f.HasFlag(PackItemFlags.NoDrop)), B(f.HasFlag(PackItemFlags.NoTrade)), B(f.HasFlag(PackItemFlags.TradePartner)),
            B(f.HasFlag(PackItemFlags.NoSell)), B(f.HasFlag(PackItemFlags.NoCart)), B(f.HasFlag(PackItemFlags.NoStorage)),
            B(f.HasFlag(PackItemFlags.NoGuildStorage)), B(f.HasFlag(PackItemFlags.NoMail)), B(f.HasFlag(PackItemFlags.NoAuction)),
            B(ScriptIndex >= 0), B(EquipScriptIndex >= 0), B(UnEquipScriptIndex >= 0),
        };
        return string.Join('|', fields);
    }
}

// Original rAthena script text kept for every script slot, with its lowering classification. Nothing is discarded:
// `Deferred` scripts keep their source so Feature 20 (general script execution) can run them without a data change.
public enum PackScriptSupport : byte { Lowered = 0, DeferredSkillExecution = 1, DeferredGeneralScript = 2 }

public sealed record PackScript(string Source, PackScriptSupport Support, string? DeferredConstruct);

public sealed record ItemPack(string RathenaCommit, IReadOnlyList<PackItem> Items, IReadOnlyList<PackScript> Scripts);

public static class ItemPackFormat
{
    public const ushort Version = 1;
    private static readonly byte[] Magic = "AIP1"u8.ToArray();

    public static byte[] Write(ItemPack pack)
    {
        if (pack.RathenaCommit.Length != 40) throw new ArgumentException("The pinned rAthena commit must be 40 hex characters.", nameof(pack));

        var strings = new List<string>();
        var stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
        int Intern(string value)
        {
            if (!stringIds.TryGetValue(value, out var id)) { id = strings.Count; strings.Add(value); stringIds.Add(value, id); }
            return id;
        }

        using var sections = new MemoryStream();
        using (var w = new BinaryWriter(sections, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(pack.Items.Count);
            var previous = -1L;
            foreach (var item in pack.Items)
            {
                if (item.Id <= previous) throw new ArgumentException("Items must be sorted by strictly increasing id.", nameof(pack));
                previous = item.Id;
                w.Write(item.Id); w.Write(Intern(item.AegisName)); w.Write(Intern(item.Name));
                w.Write(item.Type); w.Write(item.Subtype); w.Write(item.Buy); w.Write(item.Sell); w.Write(item.Weight);
                w.Write(item.Attack); w.Write(item.MagicAttack); w.Write(item.Defense); w.Write(item.Range); w.Write(item.Slots);
                w.Write(item.ClassBase0); w.Write(item.ClassBase1); w.Write(item.ClassBase2); w.Write(item.ClassUpper); w.Write(item.Sex);
                w.Write(item.Equip); w.Write(item.WeaponLevel); w.Write(item.ArmorLevel); w.Write(item.EquipLevelMin); w.Write(item.EquipLevelMax);
                w.Write(item.Look); w.Write(item.ViewId); w.Write(item.DropEffect); w.Write(item.DelayDuration); w.Write(item.DelayStatus);
                w.Write(item.StackAmount); w.Write(item.NoUseOverride); w.Write(item.TradeOverride); w.Write((uint)item.Flags);
                w.Write(item.ScriptIndex); w.Write(item.EquipScriptIndex); w.Write(item.UnEquipScriptIndex);
            }

            w.Write(pack.Scripts.Count);
            foreach (var script in pack.Scripts)
            {
                w.Write(Intern(script.Source)); w.Write((byte)script.Support); w.Write(script.DeferredConstruct is null ? -1 : Intern(script.DeferredConstruct));
            }
        }

        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(strings.Count);
            foreach (var value in strings) w.Write(value);
            sections.Position = 0;
            sections.CopyTo(body);
        }

        var bodyBytes = body.ToArray();
        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(BitConverter.GetBytes(Version));
        output.Write(Encoding.ASCII.GetBytes(pack.RathenaCommit));
        output.Write(SHA256.HashData(bodyBytes));
        output.Write(bodyBytes);
        return output.ToArray();
    }

    public static ItemPack Read(ReadOnlySpan<byte> data)
    {
        const int headerLength = 4 + 2 + 40 + 32;
        if (data.Length < headerLength || !data[..4].SequenceEqual(Magic)) throw new InvalidDataException("Not an item data pack.");
        if (BitConverter.ToUInt16(data[4..6]) != Version) throw new InvalidDataException("Unsupported item data pack version.");
        var commit = Encoding.ASCII.GetString(data[6..46]);
        var body = data[headerLength..];
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(data[46..78])) throw new InvalidDataException("Item data pack content hash mismatch.");

        using var stream = new MemoryStream(body.ToArray());
        using var r = new BinaryReader(stream, Encoding.UTF8);
        var strings = new string[r.ReadInt32()];
        for (var i = 0; i < strings.Length; i++) strings[i] = r.ReadString();

        var items = new PackItem[r.ReadInt32()];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new PackItem(
                r.ReadUInt32(), strings[r.ReadInt32()], strings[r.ReadInt32()],
                r.ReadByte(), r.ReadByte(), r.ReadInt32(), r.ReadInt32(), r.ReadUInt32(),
                r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(),
                r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt32(), r.ReadByte(),
                r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(),
                r.ReadUInt32(), r.ReadUInt32(), r.ReadByte(), r.ReadUInt32(), r.ReadInt32(),
                r.ReadUInt32(), r.ReadInt32(), r.ReadInt32(), (PackItemFlags)r.ReadUInt32(),
                r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        }

        var scripts = new PackScript[r.ReadInt32()];
        for (var i = 0; i < scripts.Length; i++)
        {
            var source = strings[r.ReadInt32()];
            var support = (PackScriptSupport)r.ReadByte();
            var construct = r.ReadInt32();
            scripts[i] = new PackScript(source, support, construct < 0 ? null : strings[construct]);
        }

        return new ItemPack(commit, items, scripts);
    }
}
