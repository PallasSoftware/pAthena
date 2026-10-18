using System.Collections.Frozen;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.World;

// VERTICAL-SLICE EVALUATION ONLY - not wired into production loading/DI, not referenced by any startup path.
// Answers the user's exact question: what would a binary mob pack + MobCatalog actually cost/buy, using REAL
// MobDefinition shapes (including nested Drops/MvpDrops/RaceGroups) rather than a synthetic toy. Deliberately
// small: a handful of representative mobs, not a full migration. See MobPackPrototypeTests for the proof that
// round-tripping and nested-property access actually works, and the Feature 16 execution plan / this session's
// own conversation for the tradeoff writeup this slice backs.
//
// Reuses GeneratedPackHeader (the proven magic/version/commit/hash framing) exactly as ItemPackFormat does -
// this IS the "what could be shared with ItemCatalog" answer for the header. The BODY layout below is
// deliberately NOT shared with ItemPackFormat's body (different fields, no script/group sections a mob needs,
// no NoEquip-shaped flags a mob has) - exactly the "standardize mechanisms, not domain behavior" split already
// established for the real GeneratedPackHeader PoC.
public static class MobPackPrototypeFormat
{
    public const ushort Version = 1;
    private static readonly byte[] Magic = "AMP0"u8.ToArray(); // "0" marks this explicitly as the prototype, never a real version 1.

    public static byte[] Write(string rathenaCommit, IReadOnlyList<MobDefinition> mobs)
    {
        var strings = new List<string>();
        var stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
        int Intern(string value)
        {
            if (!stringIds.TryGetValue(value, out var id)) { id = strings.Count; strings.Add(value); stringIds.Add(value, id); }
            return id;
        }

        using var sections = new MemoryStream();
        using (var w = new BinaryWriter(sections, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(mobs.Count);
            foreach (var mob in mobs)
            {
                w.Write(mob.Id); w.Write(Intern(mob.AegisName)); w.Write(Intern(mob.Name));
                w.Write(mob.Level); w.Write(mob.MaxHp);
                w.Write(mob.Attack); w.Write(mob.Attack2); w.Write(mob.Defense); w.Write(mob.MagicDefense);
                w.Write(mob.Str); w.Write(mob.Agi); w.Write(mob.Vit); w.Write(mob.Int); w.Write(mob.Dex); w.Write(mob.Luk);
                w.Write(mob.AttackRange); w.Write(mob.WalkSpeed); w.Write(mob.AttackDelay); w.Write(mob.AttackMotion); w.Write(mob.DamageMotion);
                w.Write(mob.BaseExp); w.Write(mob.JobExp); w.Write((uint)mob.Mode);
                w.Write((byte)mob.Size); w.Write((byte)mob.Race); w.Write((byte)mob.Element); w.Write((byte)mob.Class);

                var raceGroups = mob.RaceGroups ?? [];
                w.Write(raceGroups.Count);
                foreach (var entry in raceGroups) { w.Write(Intern(entry.Name)); w.Write(entry.Value); }

                void WriteDrops(IReadOnlyList<MobDropEntry>? drops)
                {
                    drops ??= [];
                    w.Write(drops.Count);
                    foreach (var drop in drops)
                    {
                        w.Write(Intern(drop.Item)); w.Write(drop.Rate); w.Write(drop.StealProtected);
                        w.Write(drop.RandomOptionGroup is null ? -1 : Intern(drop.RandomOptionGroup));
                    }
                }
                WriteDrops(mob.Drops);
                WriteDrops(mob.MvpDrops);
            }
        }

        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(strings.Count);
            foreach (var value in strings) w.Write(value);
            sections.Position = 0;
            sections.CopyTo(body);
        }

        var bodyBytes = body.ToArray();
        using var output = new MemoryStream();
        GeneratedPackHeader.Write(output, Magic, Version, rathenaCommit, bodyBytes);
        return output.ToArray();
    }

    public static (string Commit, IReadOnlyList<MobDefinition> Mobs) Read(ReadOnlySpan<byte> data)
    {
        var (commit, bodySpan) = GeneratedPackHeader.ReadAndVerify(data, Magic, Version, "mob data pack prototype");
        var body = bodySpan.ToArray();

        using var stream = new MemoryStream(body);
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8);
        var strings = new string[r.ReadInt32()];
        for (var i = 0; i < strings.Length; i++) strings[i] = r.ReadString();

        var mobs = new List<MobDefinition>(r.ReadInt32());
        var count = mobs.Capacity;
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadInt32(); var aegis = strings[r.ReadInt32()]; var name = strings[r.ReadInt32()];
            var level = r.ReadInt32(); var maxHp = r.ReadUInt32();
            var atk = r.ReadInt32(); var atk2 = r.ReadInt32(); var def = r.ReadInt32(); var mdef = r.ReadInt32();
            var str = r.ReadInt32(); var agi = r.ReadInt32(); var vit = r.ReadInt32(); var intel = r.ReadInt32(); var dex = r.ReadInt32(); var luk = r.ReadInt32();
            var range = r.ReadInt32(); var walkSpeed = r.ReadInt32(); var attackDelay = r.ReadInt32(); var attackMotion = r.ReadInt32(); var damageMotion = r.ReadInt32();
            var baseExp = r.ReadInt64(); var jobExp = r.ReadInt64(); var mode = (MobMode)r.ReadUInt32();
            var size = (MobSize)r.ReadByte(); var race = (MobRace)r.ReadByte(); var element = (MobElement)r.ReadByte(); var mobClass = (MobClass)r.ReadByte();

            var raceGroupCount = r.ReadInt32();
            var raceGroups = new List<MobRaceGroupEntry>(raceGroupCount);
            for (var g = 0; g < raceGroupCount; g++) raceGroups.Add(new MobRaceGroupEntry(strings[r.ReadInt32()], r.ReadBoolean()));

            List<MobDropEntry> ReadDrops()
            {
                var dropCount = r.ReadInt32();
                var drops = new List<MobDropEntry>(dropCount);
                for (var d = 0; d < dropCount; d++)
                {
                    var item = strings[r.ReadInt32()]; var rate = r.ReadInt32(); var stealProtected = r.ReadBoolean();
                    var randomOptionGroupIndex = r.ReadInt32();
                    drops.Add(new MobDropEntry(item, rate, stealProtected, randomOptionGroupIndex < 0 ? null : strings[randomOptionGroupIndex]));
                }
                return drops;
            }
            var drops = ReadDrops();
            var mvpDrops = ReadDrops();

            mobs.Add(new MobDefinition(
                id, aegis, name, level, maxHp, atk, atk2, def, mdef, str, agi, vit, intel, dex, luk,
                range, walkSpeed, attackDelay, attackMotion, damageMotion, baseExp, jobExp, mode,
                Source: new WorldSourceInfo("rAthena", commit, "mob_db.yml (prototype)", 0),
                Size: size, Race: race, Element: element, Class: mobClass,
                RaceGroups: raceGroups.Count == 0 ? null : raceGroups,
                Drops: drops.Count == 0 ? null : drops,
                MvpDrops: mvpDrops.Count == 0 ? null : mvpDrops));
        }
        return (commit, mobs);
    }
}

// Prototype runtime registry, mirroring ItemCatalog's exact shape (Lazy<T> singleton, FrozenDictionary<int, T>
// ById, Open(path)/Create(pack) split) so the comparison against today's GeneratedMobs direct-static-field
// access is apples-to-apples. NOT wired into production DI - evaluation only.
public sealed class MobCatalogPrototype
{
    public string RathenaCommit { get; }
    public FrozenDictionary<int, MobDefinition> ById { get; }

    private MobCatalogPrototype(string commit, FrozenDictionary<int, MobDefinition> byId)
    {
        RathenaCommit = commit;
        ById = byId;
    }

    public static MobCatalogPrototype Create(byte[] packData)
    {
        var (commit, mobs) = MobPackPrototypeFormat.Read(packData);
        return new(commit, mobs.ToDictionary(m => m.Id).ToFrozenDictionary());
    }
}
