namespace Athena.Net.MapServer.World;

// Mob data pack: the versioned, deterministic artifact (MapData/AthenaMobs.bin) holding every effective mob_db definition
// (pinned db/re/mob_db.yml plus data/custom/mobs overrides), loaded once by MobCatalog. Like ItemPackFormat, this single
// source file is compiled into BOTH the WorldDataImporter (writer) and the MapServer (reader), together with MobData.cs,
// so the two ends cannot drift. Plain data only.
//
// Layout: GeneratedPackHeader (magic "AMB1" | version | commit | SHA-256 of body) | body.
// Body (little-endian): string table, then mob count and the mobs sorted by Id, every MobDefinition field in declaration
// order. Nullable strings are a string index or -1; an empty/absent RaceGroups/Drops/MvpDrops list is count 0 and reads
// back as null (the generated C# literals this pack replaced never held an empty list).
public sealed record MobPack(string RathenaCommit, IReadOnlyList<MobDefinition> Mobs);

public static class MobPackFormat
{
    public const ushort Version = 1;
    private static readonly byte[] Magic = "AMB1"u8.ToArray();

    public static byte[] Write(MobPack pack)
    {
        var strings = new List<string>();
        var stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
        int Intern(string? value)
        {
            if (value is null) return -1;
            if (!stringIds.TryGetValue(value, out var id)) { id = strings.Count; strings.Add(value); stringIds.Add(value, id); }
            return id;
        }

        var mobs = pack.Mobs.OrderBy(mob => mob.Id).ToArray();
        using var sections = new MemoryStream();
        using (var w = new BinaryWriter(sections, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(mobs.Length);
            foreach (var mob in mobs)
            {
                w.Write(mob.Id); w.Write(Intern(mob.AegisName)); w.Write(Intern(mob.Name)); w.Write(mob.Level); w.Write(mob.MaxHp);
                w.Write(mob.Attack); w.Write(mob.Attack2); w.Write(mob.Defense); w.Write(mob.MagicDefense);
                w.Write(mob.Str); w.Write(mob.Agi); w.Write(mob.Vit); w.Write(mob.Int); w.Write(mob.Dex); w.Write(mob.Luk);
                w.Write(mob.AttackRange); w.Write(mob.WalkSpeed); w.Write(mob.AttackDelay); w.Write(mob.AttackMotion); w.Write(mob.DamageMotion);
                w.Write(mob.BaseExp); w.Write(mob.JobExp); w.Write((uint)mob.Mode);
                w.Write(Intern(mob.Source.Repository)); w.Write(Intern(mob.Source.Commit)); w.Write(Intern(mob.Source.File)); w.Write(mob.Source.Line);
                w.Write(Intern(mob.JapaneseName)); w.Write(mob.MaxSp); w.Write(mob.MvpExp);
                w.Write(mob.Resistance); w.Write(mob.MagicResistance); w.Write(mob.SkillRange); w.Write(mob.ChaseRange);
                w.Write((int)mob.Size); w.Write((int)mob.Race); w.Write((int)mob.Element); w.Write(mob.ElementLevel);
                w.Write(mob.ClientAttackMotion); w.Write(mob.DamageTaken); w.Write(mob.GroupId); w.Write(Intern(mob.Title)); w.Write((int)mob.Class);

                var raceGroups = mob.RaceGroups ?? [];
                w.Write(raceGroups.Count);
                foreach (var entry in raceGroups) { w.Write(Intern(entry.Name)); w.Write(entry.Value); }
                foreach (var drops in new[] { mob.Drops ?? [], mob.MvpDrops ?? [] })
                {
                    w.Write(drops.Count);
                    foreach (var drop in drops) { w.Write(Intern(drop.Item)); w.Write(drop.Rate); w.Write(drop.StealProtected); w.Write(Intern(drop.RandomOptionGroup)); }
                }
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

        using var output = new MemoryStream();
        GeneratedPackHeader.Write(output, Magic, Version, pack.RathenaCommit, body.ToArray());
        return output.ToArray();
    }

    public static MobPack Read(ReadOnlySpan<byte> data)
    {
        var (commit, bodySpan) = GeneratedPackHeader.ReadAndVerify(data, Magic, Version, "mob data pack");
        using var stream = new MemoryStream(bodySpan.ToArray());
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8);
        var strings = new string[r.ReadInt32()];
        for (var i = 0; i < strings.Length; i++) strings[i] = r.ReadString();
        string? Str() => r.ReadInt32() is var index and >= 0 ? strings[index] : null;
        string Required() => Str() ?? throw new InvalidDataException("mob data pack has a missing required string.");

        var mobs = new MobDefinition[r.ReadInt32()];
        for (var i = 0; i < mobs.Length; i++)
        {
            var id = r.ReadInt32(); var aegisName = Required(); var name = Required(); var level = r.ReadInt32(); var maxHp = r.ReadUInt32();
            var attack = r.ReadInt32(); var attack2 = r.ReadInt32(); var defense = r.ReadInt32(); var magicDefense = r.ReadInt32();
            var str = r.ReadInt32(); var agi = r.ReadInt32(); var vit = r.ReadInt32(); var intel = r.ReadInt32(); var dex = r.ReadInt32(); var luk = r.ReadInt32();
            var attackRange = r.ReadInt32(); var walkSpeed = r.ReadInt32(); var attackDelay = r.ReadInt32(); var attackMotion = r.ReadInt32(); var damageMotion = r.ReadInt32();
            var baseExp = r.ReadInt64(); var jobExp = r.ReadInt64(); var mode = (MobMode)r.ReadUInt32();
            var source = new WorldSourceInfo(Required(), Required(), Required(), r.ReadInt32());
            var japaneseName = Str(); var maxSp = r.ReadUInt32(); var mvpExp = r.ReadInt64();
            var resistance = r.ReadInt32(); var magicResistance = r.ReadInt32(); var skillRange = r.ReadInt32(); var chaseRange = r.ReadInt32();
            var size = (MobSize)r.ReadInt32(); var race = (MobRace)r.ReadInt32(); var element = (MobElement)r.ReadInt32(); var elementLevel = r.ReadInt32();
            var clientAttackMotion = r.ReadInt32(); var damageTaken = r.ReadInt32(); var groupId = r.ReadInt32(); var title = Str(); var mobClass = (MobClass)r.ReadInt32();

            var raceGroups = new MobRaceGroupEntry[r.ReadInt32()];
            for (var g = 0; g < raceGroups.Length; g++) raceGroups[g] = new MobRaceGroupEntry(Required(), r.ReadBoolean());
            MobDropEntry[] Drops()
            {
                var drops = new MobDropEntry[r.ReadInt32()];
                for (var d = 0; d < drops.Length; d++) drops[d] = new MobDropEntry(Required(), r.ReadInt32(), r.ReadBoolean(), Str());
                return drops;
            }
            var drops = Drops();
            var mvpDrops = Drops();

            mobs[i] = new MobDefinition(
                id, aegisName, name, level, maxHp, attack, attack2, defense, magicDefense, str, agi, vit, intel, dex, luk,
                attackRange, walkSpeed, attackDelay, attackMotion, damageMotion, baseExp, jobExp, mode, source,
                japaneseName, maxSp, mvpExp, resistance, magicResistance, skillRange, chaseRange, size, race, element, elementLevel,
                clientAttackMotion, damageTaken, groupId, title, mobClass,
                raceGroups.Length == 0 ? null : raceGroups, drops.Length == 0 ? null : drops, mvpDrops.Length == 0 ? null : mvpDrops);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("mob data pack has trailing bytes.");
        return new MobPack(commit, mobs);
    }
}
