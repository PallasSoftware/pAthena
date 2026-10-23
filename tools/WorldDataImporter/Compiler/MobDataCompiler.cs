using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.WorldCompiler.Generation;

// Compiles pinned rAthena mob_db.yml Id blocks plus the fixed-column
// "map,x,y monster Name MobId,Count,Delay" declaration format used by
// npc/re/mobs/*.txt. mob_db.yml is read with RathenaYaml (the same block-YAML
// reader ItemDatabaseLoader uses for item_db*.yml) into YamlMap blocks, so a
// field's presence is explicit (YamlMap.Has/Scalar) rather than inferred from
// regex matches; the spawn-line format stays a fixed-column regex scanner.
// The YamlMap port is pinned to the previous scanner's complete output by
// MobDatabaseParserRegressionTests. ReadMobDefinition's FIELD COVERAGE is intentionally
// lossless/near-complete for mob_db.yml's documented schema (every meaningful
// top-level scalar plus the Modes:/RaceGroups:/Drops:/MvpDrops: list-shaped
// blocks - see ai/world-data.md's "Mob static-data schema coverage" section
// and the PinnedMobDbSchema_* tests in MobDataCompilerTests.cs) - it is not
// merely reading "the few scalars an early vertical slice needed".
internal static class MobDataCompiler
{
    internal sealed record MobDefinitionData(
        int Id, string AegisName, string Name, int Level, uint Hp,
        int Attack, int Attack2, int Defense, int MagicDefense,
        int Str, int Agi, int Vit, int Int, int Dex, int Luk,
        int AttackRange, int WalkSpeed, int AttackDelay, int AttackMotion, int DamageMotion,
        long BaseExp, long JobExp, MobModeData Mode,
        string? JapaneseName, uint Sp, long MvpExp, int Resistance, int MagicResistance,
        int SkillRange, int ChaseRange, MobSizeData Size, MobRaceData Race, MobElementData Element,
        int ElementLevel, int ClientAttackMotion, int DamageTaken, int GroupId, string? Title,
        MobClassData Class,
        IReadOnlyList<MobRaceGroupEntryData> RaceGroups, IReadOnlyList<MobDropEntryData> Drops,
        IReadOnlyList<MobDropEntryData> MvpDrops,
        // The pinned mob_db.yml 1-based line number of this mob's OWN "  - Id: <n>" declaration
        // line, computed from the block's string offset - real provenance rather than a shared/
        // caller-supplied placeholder. 0 only for synthetic in-memory fixtures that never came from
        // a real file offset (e.g. unit-test YAML snippets built directly as MobDefinitionData).
        int SourceLine = 0,
        // Null for a pinned db/re/mob_db.yml definition (the generator's shared source file applies). A definition
        // added or overridden by a custom layer carries that custom file, and SourceLine is the custom block's line.
        string? SourceFile = null);

    // Mirrors Athena.Net.MapServer.World.MobRaceGroupEntry exactly - see that record's own doc
    // comment for why RaceGroups is a pinned-name list rather than a fixed C# enum.
    internal sealed record MobRaceGroupEntryData(string Name, bool Value);

    // Mirrors Athena.Net.MapServer.World.MobDropEntry exactly - see that record's own doc comment.
    internal sealed record MobDropEntryData(string Item, int Rate, bool StealProtected, string? RandomOptionGroup);

    // Mirrors Athena.Net.MapServer.World.MobSize exactly (same numeric values/names) - see that
    // enum's own doc comment for the pinned e_size trace and why SZ_ALL/SZ_MAX are excluded.
    internal enum MobSizeData { Small = 0, Medium = 1, Big = 2 }

    // Mirrors Athena.Net.MapServer.World.MobRace exactly (same numeric values/names) - see that
    // enum's own doc comment for the pinned e_race trace and why RC_NONE_/RC_ALL/RC_MAX are excluded.
    internal enum MobRaceData
    {
        Formless = 0, Undead = 1, Brute = 2, Plant = 3, Insect = 4, Fish = 5, Demon = 6,
        DemiHuman = 7, Angel = 8, Dragon = 9, PlayerHuman = 10, PlayerDoram = 11,
    }

    // Mirrors Athena.Net.MapServer.World.MobElement exactly (same numeric values/names) - see that
    // enum's own doc comment for the pinned e_element trace and why the sentinel/wildcard members
    // are excluded.
    internal enum MobElementData
    {
        Neutral = 0, Water = 1, Earth = 2, Fire = 3, Wind = 4, Poison = 5, Holy = 6, Dark = 7,
        Ghost = 8, Undead = 9,
    }

    // Mirrors Athena.Net.MapServer.World.MobClass exactly (same numeric values/names, including the
    // pinned enum's own gap at 3) - see that enum's own doc comment for the pinned e_mob_class trace.
    internal enum MobClassData { Normal = 0, Boss = 1, Guardian = 2, Battlefield = 4, Event = 5 }

    // Mirrors Athena.Net.MapServer.World.MobModeResolver.ClassDerivedBits exactly - pinned
    // MobDatabase::loadingFinished()'s class-derived mode-bit resolution (mob.cpp:5536-5551),
    // applied to every mob's SOURCE mode (Ai preset + Modes: overrides) to compute the pinned-
    // accurate EFFECTIVE mode a real rAthena server actually holds/runs combat against. See
    // MobModeResolver's own doc comment in WorldEntityDefinition.cs for the SourceMode-vs-
    // EffectiveMode rationale this mirrors.
    internal static MobModeData ResolveEffectiveMode(MobModeData sourceMode, MobClassData mobClass) => sourceMode | mobClass switch
    {
        MobClassData.Boss => MobModeData.Detector | MobModeData.StatusImmune | MobModeData.KnockBackImmune,
        MobClassData.Guardian => MobModeData.StatusImmune,
        MobClassData.Battlefield => MobModeData.StatusImmune | MobModeData.SkillImmune,
        MobClassData.Event => MobModeData.FixedItemDrop,
        _ => MobModeData.None,
    };

    // Mirrors Athena.Net.MapServer.World.MobMode exactly (same bit values/names, the complete
    // pinned MD_* bitmask) - kept as a separate type per this project's existing
    // WorldDataImporter/MapServer decoupling rule (see e.g. CompiledMapCellFlags's own doc comment
    // for the same pattern): WorldDataImporter has no project reference to MapServer.
    [Flags]
    internal enum MobModeData : uint
    {
        None = 0,
        CanMove = 0x0000001,
        Looter = 0x0000002,
        Aggressive = 0x0000004,
        Assist = 0x0000008,
        CastSensorIdle = 0x0000010,
        NoRandomWalk = 0x0000020,
        NoCast = 0x0000040,
        CanAttack = 0x0000080,
        CastSensorChase = 0x0000200,
        ChangeChase = 0x0000400,
        Angry = 0x0000800,
        ChangeTargetMelee = 0x0001000,
        ChangeTargetChase = 0x0002000,
        TargetWeak = 0x0004000,
        RandomTarget = 0x0008000,
        IgnoreMelee = 0x0010000,
        IgnoreMagic = 0x0020000,
        IgnoreRanged = 0x0040000,
        Mvp = 0x0080000,
        IgnoreMisc = 0x0100000,
        KnockBackImmune = 0x0200000,
        TeleportBlock = 0x0400000,
        FixedItemDrop = 0x1000000,
        Detector = 0x2000000,
        StatusImmune = 0x4000000,
        SkillImmune = 0x8000000,
    }

    // Pinned e_aegis_monstertype (legacy/rathena/src/map/mob.hpp:151-182) - the COMPLETE pinned
    // Ai-preset-name -> raw e_mode bitmask table, reproduced in full even though this project's
    // MobModeData only exposes two of the bits any given preset may set, so a future mob using a
    // different Ai preset is decoded correctly rather than needing this table extended piecemeal.
    private static readonly Dictionary<string, int> AiPresets = new(StringComparer.Ordinal)
    {
        ["01"] = 0x81, ["02"] = 0x83, ["03"] = 0x1089, ["04"] = 0x3885, ["05"] = 0x2085,
        ["06"] = 0, ["07"] = 0x108B, ["08"] = 0x7085, ["09"] = 0x3095, ["10"] = 0x84,
        ["11"] = 0x84, ["12"] = 0x2085, ["13"] = 0x308D, ["17"] = 0x91, ["19"] = 0x3095,
        ["20"] = 0x3295, ["21"] = 0x3695, ["24"] = 0xA1, ["25"] = 0x1, ["26"] = 0xB695,
        ["27"] = 0x8084, ["ABR_PASSIVE"] = 0x21, ["ABR_OFFENSIVE"] = 0xA5,
    };

    // DeathEvent/Size/Ai mirror pinned npc_parse_mob's `w4` format's remaining optional positions
    // (`<mobid>,<count>,<delay1>,<delay2>,<event>,<size>,<ai>`) - preserved losslessly as source
    // data even though no death-event/size-override/AI-override runtime exists in this project.
    // `null` means the field was omitted from the source line; a present DeathEvent (including the
    // pinned tree's own inert literal "0" placeholder values, see ReadMobSpawns) is stored verbatim.
    // Size reuses this file's own MobSizeData (mirrors Athena.Net.MapServer.World.MobSize exactly -
    // see MobSizeData's own doc comment) rather than a raw int, for the same reason mob.Size does
    // above: a present-but-out-of-[0,2]-range source value should fail generation loudly instead of
    // being silently cast into a meaningless enum member.
    // SpawnName/DeclaredLevel mirror pinned npc_parse_mob's w3 field
    // (`<mob name>{,<mob level>}`, npc.cpp:5218-5317) - preserved independently of
    // MobDefinitionData.Name/Level, which they are not guaranteed to match. SpawnName is the exact
    // source token verbatim (always present - w3's own mob-name capture never fails to match).
    // DeclaredLevel is null when the source line genuinely omits the level field (pinned's own
    // `mob_lv = -1` sentinel before sscanf runs) and the RAW signed parsed value when present -
    // INCLUDING a value pinned parses as valid but that never actually overrides mob.level (any
    // negative other than the omitted sentinel; pinned's own effective-override gate is
    // `mob_lv > 0 && mob_lv <= MAX_LEVEL`, strictly narrower than its validity gate). See
    // MaxPinnedMobLevel's own doc comment for the exact pinned validity rule this compiler enforces
    // at parse time, and EffectiveLevelOverride (WorldEntityDefinition.cs) for the separate, derived
    // "does this actually override" concept - never collapsed into this stored field.
    internal sealed record MobSpawnData(string Map, int MobId, int Count, int RespawnDelay, int RespawnRandomDelay, string SourceFile, int SourceLine, short X, short Y, short Xs, short Ys, string SpawnName, int? DeclaredLevel = null, string? DeathEvent = null, MobSizeData? Size = null, int? Ai = null);

    // Pinned MAX_LEVEL (src/map/map.hpp:78). npc_parse_mob rejects a present, non-omitted level
    // field outside (0, MAX_LEVEL] as a hard parse error - this compiler reproduces that exact
    // pinned validity gate (see MobSpawnData's own doc comment) rather than accepting every signed
    // integer.
    internal const int MaxPinnedMobLevel = 275;

    // Parses one `- Id: <n>` block out of mob_db.yml up to (not including) the
    // next top-level `- Id:` line. Defaults for fields absent from the pinned
    // block match mob.cpp's spawn_data default-constructor (mob_summon /
    // MobDatabase::create, ~line 4946-4963) which runs BEFORE the YAML loader
    // conditionally overwrites individual fields - NOT a blanket "0":
    // Level=1, Str/Agi/Vit/Int/Dex/Luk=1, WalkSpeed=DEFAULT_WALK_SPEED(150).
    // Hp/BaseExp/JobExp/Attack/Attack2/Defense/MagicDefense/AttackRange/
    // AttackDelay all genuinely default to 0/unset in that same constructor.
    //
    // The single-mob lookup only SLICES the requested block out of the text (first exact
    // "  - Id: <n>" line up to the next one) and parses that slice with RathenaYaml; it does not
    // re-parse the whole ~3 MB file per call, because RepositoryDomainAnalyzers.AnalyzeMobs calls it
    // once per pinned mob.
    internal static MobDefinitionData ReadMobDefinition(string mobDbYaml, int mobId)
    {
        var marker = $"  - Id: {mobId.ToString(CultureInfo.InvariantCulture)}\n";
        var start = mobDbYaml.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new ArgumentException($"Mob Id {mobId} was not found in the pinned mob_db.yml.");
        var next = mobDbYaml.IndexOf("\n  - Id: ", start + marker.Length, StringComparison.Ordinal);
        var block = next >= 0 ? mobDbYaml[start..(next + 1)] : mobDbYaml[start..];
        var node = (RathenaYaml.Parse(block, "mob_db.yml") as YamlSequence)?.Items.FirstOrDefault() as YamlMap
            ?? throw new ArgumentException($"Mob Id {mobId} block in the pinned mob_db.yml is not a map.");
        return ParseMobNode(node, CountLines(mobDbYaml, start));
    }

    // Enumerates EVERY Body entry of the pinned mob_db.yml, in source (file) order - the source of
    // truth for "generate all pinned mobs", as opposed to ReadMobDefinition's single-mob lookup used
    // by the pre-existing manually-curated compile-mob-definitions command. One RathenaYaml parse of
    // the whole file (the same reader ItemDatabaseLoader uses for item_db*.yml); each Body map's own
    // line is its "  - Id:" declaration line, i.e. the mob's real provenance line.
    //
    // `customLayers` (name, YAML text) are Athena.NET-authored mob_db.yml-shaped files (data/custom/mobs)
    // applied after the pinned file, with the same structure as ItemDatabaseLoader's custom layers: an
    // existing Id is re-parsed by the SAME ParseMobNode with the pinned block as its base (only keys present
    // in the custom block change anything), a new Id is parsed exactly like a pinned block and appended.
    // An Id may appear once across all custom files; unknown keys fail. Each custom definition carries its
    // custom file/line as provenance.
    internal static IReadOnlyList<MobDefinitionData> ReadAllMobDefinitions(string mobDbYaml, IReadOnlyList<(string Name, string Yaml)>? customLayers = null)
    {
        var root = RathenaYaml.Parse(mobDbYaml, "mob_db.yml") as YamlMap;
        var mobs = (root?.Get("Body") as YamlSequence)?.Items.OfType<YamlMap>().Select(node => (Node: node, Mob: ParseMobNode(node, node.Line))).ToList() ?? [];

        var indexById = new Dictionary<int, int>();
        for (var i = 0; i < mobs.Count; i++) indexById.TryAdd(mobs[i].Mob.Id, i);
        var customIds = new Dictionary<int, string>();
        foreach (var (path, yaml) in customLayers ?? [])
        {
            var body = (RathenaYaml.Parse(yaml, path) as YamlMap)?.Get("Body") as YamlSequence ?? throw new InvalidDataException($"{path} has no Body sequence.");
            foreach (var node in body.Items.OfType<YamlMap>())
            {
                var location = $"{path}:{node.Line}";
                var id = (int)(Long(node, "Id") ?? throw new InvalidDataException($"{location}: mob has no Id."));
                RathenaYaml.RejectUnknownKeys(node, CustomSchema, path, $"mob {id}");
                if (!customIds.TryAdd(id, location)) throw new InvalidDataException($"{location}: mob id {id} is defined twice in the custom layers (first at {customIds[id]}).");
                if (indexById.TryGetValue(id, out var index)) mobs[index] = (mobs[index].Node, ParseMobNode(node, node.Line, path, mobs[index].Node));
                else mobs.Add((node, ParseMobNode(node, node.Line, path)));
            }
        }
        return mobs.Select(mob => mob.Mob).ToArray();
    }

    // `baseNode` is the pinned block a custom override applies to (null for a pinned or new mob): every field
    // reads its key from `node` when present there and from `baseNode` otherwise, so an omitted key inherits the
    // pinned value through the same conversion. Map/list fields (Modes, RaceGroups, Drops, MvpDrops) therefore
    // replace wholesale when present - a present Drops: list is parsed on its own (its Index: entries address
    // the custom list), never merged into the pinned drops the way rAthena's own db/import overlay merges them.
    // Mode is derived from two keys: Ai and Modes are each taken from wherever they are present.
    private static MobDefinitionData ParseMobNode(YamlMap node, int sourceLine, string? sourceFile = null, YamlMap? baseNode = null)
    {
        YamlMap Src(string key) => baseNode is not null && !node.Has(key) ? baseNode : node;
        long? Field(string key) => Long(Src(key), key);
        var location = $"{sourceFile ?? "mob_db.yml"}:{sourceLine}";
        var mobId = (int)(Long(node, "Id") ?? throw new ArgumentException($"{location}: mob block has no 'Id' field."));
        string Required(string key) => Src(key).Scalar(key) ?? throw new ArgumentException($"{location}: mob {mobId} has no '{key}' field.");
        var name = Required("Name");
        var attackMotion = Field("AttackMotion") ?? 0;

        return new MobDefinitionData(
            mobId,
            Required("AegisName"),
            name,
            (int)(Field("Level") ?? 1),
            (uint)(Field("Hp") ?? 0),
            (int)(Field("Attack") ?? 0),
            (int)(Field("Attack2") ?? 0),
            (int)(Field("Defense") ?? 0),
            (int)(Field("MagicDefense") ?? 0),
            (int)(Field("Str") ?? 1),
            (int)(Field("Agi") ?? 1),
            (int)(Field("Vit") ?? 1),
            (int)(Field("Int") ?? 1),
            (int)(Field("Dex") ?? 1),
            (int)(Field("Luk") ?? 1),
            (int)(Field("AttackRange") ?? 0),
            (int)(Field("WalkSpeed") ?? 150),
            (int)(Field("AttackDelay") ?? 0),
            // AttackMotion (amotion) and DamageMotion (dmotion) are pinned mob_db.yml scalars
            // distinct from AttackDelay (adelay) - amotion is the attacker's attack-animation
            // timing (clif_damage's own srcSpeed for a mob attacker), dmotion is this mob's OWN
            // hit-reaction/walk-delay timing when IT is the target (clif_damage's own dstSpeed when
            // a player attacks this mob) - see MobBasicAttackCalculator/IroMonsterCombatPackets
            // call sites for where each is actually used. Never conflated with AttackDelay, which
            // controls attack CADENCE (NextAttackAt), not animation/hit-reaction timing. Same
            // mob.cpp:4946-4963 default-constructor rationale as AttackDelay's own comment: both
            // genuinely default to 0/unset when the pinned block omits them.
            (int)attackMotion,
            (int)(Field("DamageMotion") ?? 0),
            Field("BaseExp") ?? 0,
            Field("JobExp") ?? 0,
            ReadMode(Src("Ai"), Src("Modes")),
            // Pinned MobDatabase::parseBodyNode (mob.cpp:5028-5040): JapaneseName present -> use it;
            // absent on a mob_id seen for the first time (`!exists`) -> falls back to this SAME
            // block's own resolved Name (mob->jname = mob->name), never left null/blank. This
            // fallback applies to pinned and new custom mobs. A custom override that omits it keeps the
            // pinned mob's resolved jname (the `exists`-true branch): the base JapaneseName, else the base Name.
            Src("JapaneseName").Scalar("JapaneseName") ?? (baseNode ?? node).Scalar("Name") ?? name,
            (uint)(Field("Sp") ?? 1),
            Field("MvpExp") ?? 0,
            (int)(Field("Resistance") ?? 0),
            (int)(Field("MagicResistance") ?? 0),
            (int)(Field("SkillRange") ?? 0),
            (int)(Field("ChaseRange") ?? 0),
            ReadSize(Src("Size")),
            ReadRace(Src("Race")),
            ReadElement(Src("Element")),
            (int)(Field("ElementLevel") ?? 1),
            // ClientAttackMotion has no fixed default: pinned MobDatabase::parseBodyNode resolves an
            // absent field to THIS SAME mob's own resolved AttackMotion value the first time a mob_id
            // is seen (mob.cpp:5391-5397) - see MobDefinition.ClientAttackMotion's own doc comment. An
            // override that omits it keeps the pinned mob's resolved value (the base block's AttackMotion).
            (int)(Field("ClientAttackMotion") ?? (baseNode is null ? attackMotion : Long(baseNode, "AttackMotion") ?? 0)),
            (int)(Field("DamageTaken") ?? 100),
            (int)(Field("GroupId") ?? 0),
            Src("Title").Scalar("Title"),
            ReadClass(Src("Class")),
            ReadRaceGroups(Src("RaceGroups")),
            ReadDrops(Src("Drops"), "Drops"),
            ReadDrops(Src("MvpDrops"), "MvpDrops"),
            sourceLine,
            sourceFile);
    }

    // Presence primitive for every scalar field: null means the key is absent from the block (the
    // caller applies the documented mob.cpp default), a present value must be an integer. Like the
    // previous scanner, a malformed integer is a hard FormatException, never a silent default.
    private static long? Long(YamlMap node, string key) =>
        node.Scalar(key) is { } text ? long.Parse(text, CultureInfo.InvariantCulture) : null;

    // Modes:/RaceGroups: entries are "<Name>: <bool>" pairs, in source order. Only the exact
    // lowercase "true"/"false" literals are recognized - the previous scanner's entry pattern
    // matched nothing else, so any other value (none exist in the pinned data) is skipped rather
    // than reinterpreted.
    private static IEnumerable<(string Name, bool Value)> BooleanEntries(YamlMap node, string key) =>
        node.Get(key) is YamlMap map
            ? map.Entries.Where(entry => (entry.Value as YamlScalar)?.Value is "true" or "false")
                .Select(entry => (entry.Key, ((YamlScalar)entry.Value).Value == "true"))
            : [];

    internal sealed record GeneratedMobSymbol(MobDefinitionData Mob, string Symbol);

    internal static IReadOnlyList<GeneratedMobSymbol> CreateGeneratedSymbols(IReadOnlyList<MobDefinitionData> mobs)
    {
        var duplicateIds = mobs.GroupBy(mob => mob.Id).Where(group => group.Count() > 1).OrderBy(group => group.Key).ToArray();
        if (duplicateIds.Length > 0)
            throw new ArgumentException($"Pinned mob_db.yml contains duplicate effective mob Id(s): {string.Join(", ", duplicateIds.Select(group => group.Key))}.");

        var used = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<GeneratedMobSymbol>(mobs.Count);
        foreach (var mob in mobs.OrderBy(mob => mob.Id))
        {
            var baseSymbol = SanitizeMobSymbol(mob.AegisName);
            var symbol = baseSymbol;
            if (!used.Add(symbol))
            {
                symbol = $"{baseSymbol}_{mob.Id.ToString(CultureInfo.InvariantCulture)}";
                if (!used.Add(symbol))
                    throw new ArgumentException($"Mob symbol collision could not be disambiguated for Id {mob.Id} ('{mob.AegisName}').");
            }
            results.Add(new GeneratedMobSymbol(mob, symbol));
        }
        return results;
    }

    internal static string SanitizeMobSymbol(string aegisName)
    {
        var parts = Regex.Matches(aegisName, @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(part => part.Length > 0)
            .ToArray();
        var symbol = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
        if (symbol.Length == 0) symbol = "Mob";
        if (!SyntaxFactsLikeIdentifierStart(symbol[0])) symbol = "Mob" + symbol;
        return CSharpKeywords.Contains(symbol) ? symbol + "Mob" : symbol;
    }

    private static bool SyntaxFactsLikeIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "Abstract", "As", "Base", "Bool", "Break", "Byte", "Case", "Catch", "Char", "Checked",
        "Class", "Const", "Continue", "Decimal", "Default", "Delegate", "Do", "Double", "Else",
        "Enum", "Event", "Explicit", "Extern", "False", "Finally", "Fixed", "Float", "For", "Foreach",
        "Goto", "If", "Implicit", "In", "Int", "Interface", "Internal", "Is", "Lock", "Long", "Namespace",
        "New", "Null", "Object", "Operator", "Out", "Override", "Params", "Private", "Protected", "Public",
        "Readonly", "Ref", "Return", "Sbyte", "Sealed", "Short", "Sizeof", "Stackalloc", "Static", "String",
        "Struct", "Switch", "This", "Throw", "True", "Try", "Typeof", "Uint", "Ulong", "Unchecked", "Unsafe",
        "Ushort", "Using", "Virtual", "Void", "Volatile", "While", "Record", "Required", "File"
    };

    // Reproduces pinned MobDatabase::parseBodyNode's RaceGroups: resolution (mob.cpp:5291-5317):
    // each entry is a bare pinned key name (never re-prefixed/validated against a fixed bound here -
    // see MobRaceGroupEntry's own doc comment for why this project does not hardcode the pinned
    // RC2_* table) paired with its own explicit true/false toggle value, in pinned source order.
    private static IReadOnlyList<MobRaceGroupEntryData> ReadRaceGroups(YamlMap node) =>
        BooleanEntries(node, "RaceGroups").Select(entry => new MobRaceGroupEntryData(entry.Name, entry.Value)).ToArray();

    // Pinned MAX_MOB_DROP/MAX_MVP_DROP (mob.hpp:27/31) - the exact bound MobDatabase::parseDropNode
    // enforces per section (an Index >= max, or an append once the effective list already holds
    // max entries, is skipped/warned rather than accepted).
    private const int MaxMobDrop = 10;
    private const int MaxMvpDrop = 3;

    // Reproduces pinned MobDatabase::parseDropNode (mob.cpp:4844-4923) EXACTLY, including its
    // stateful Index: overwrite/append/skip semantics - shared verbatim by both `Drops:` and
    // `MvpDrops:` (parseDropNode is one function called for both sections with a different `max`).
    // Index is NOT merely a db/import overlay mechanism this project can ignore: real pinned
    // db/re/mob_db.yml itself uses `Index:` on essentially every drop entry (1,301 real occurrences)
    // - e.g. the REAL Poring/1002 declares `Index: 0` through `Index: 7` on its own 8 base-file
    // drop entries. Declarations are processed SEQUENTIALLY, each one mutating the SAME growing
    // effective list the next declaration is evaluated against (not independently parsed and then
    // reordered) - exactly mirroring pinned source's own `for (dropit : node) { ... }` loop over
    // the SAME `drops` vector:
    //   - no `Index:`           -> append (if the effective list has not yet reached `max`)
    //   - `Index == count`      -> append at that (implicitly correct) next slot
    //   - `Index < count`       -> OVERWRITE the entry already at that slot in place (does not move it)
    //   - `Index > count`       -> skip (a "gap" - pinned source's own explicit `// TODO: warning` case)
    //   - `Index >= max`        -> skip (invalid, out of the section's own bound)
    private static IReadOnlyList<MobDropEntryData> ReadDrops(YamlMap node, string sectionName)
    {
        var max = sectionName == "MvpDrops" ? MaxMvpDrop : MaxMobDrop;
        if (node.Get(sectionName) is not YamlSequence section) return [];
        var entries = new List<MobDropEntryData>();
        foreach (var entry in section.Items.OfType<YamlMap>().Where(entry => entry.Has("Item")))
        {
            var item = entry.Scalar("Item") ?? throw new ArgumentException($"mob_db.yml:{entry.Line}: drop entry 'Item' is not a scalar.");
            var rate = int.Parse(entry.Scalar("Rate") ?? throw new ArgumentException("Pinned mob_db.yml drop entry has no 'Rate' field."), CultureInfo.InvariantCulture);
            var steal = string.Equals(entry.Scalar("StealProtected"), "true", StringComparison.OrdinalIgnoreCase);
            var group = entry.Scalar("RandomOptionGroup");
            var drop = new MobDropEntryData(item, rate, steal, group);

            var indexValue = entry.Scalar("Index");
            if (indexValue is null)
            {
                if (entries.Count < max) entries.Add(drop); // else: skipped, matching pinned "Maximum of %d monster %s met, skipping.".
                continue;
            }

            var index = int.Parse(indexValue, CultureInfo.InvariantCulture);
            if (index >= max) continue; // Skipped, matching pinned "Invalid monster %s index %hu ... skipping.".
            if (index == entries.Count) entries.Add(drop); // Append at the next slot.
            else if (index < entries.Count) entries[index] = drop; // Overwrite in place - does not move the entry.
            // else (index > entries.Count): a genuine gap - skipped, matching pinned's own "TODO: warning" case.
        }
        return entries;
    }

    // Pinned MobDatabase::parseBodyNode's Size:/Race:/Element:/Class: resolution (mob.cpp:5244-5487):
    // build "<Prefix>_" + the pinned string value, case-insensitively look it up against the fixed
    // pinned constant table (script_get_constant -> search_str uses strcasecmp, matching real data
    // such as "Player_Doram"/"Demihuman"), and fall back to the documented default when absent,
    // unrecognized, or out of the type's valid range - exactly like every other unrecognized-Ai/
    // unrecognized-mode fallback already in this file, never a thrown error for a single bad
    // enum-shaped field.
    private static MobSizeData ReadSize(YamlMap node)
    {
        var value = node.Scalar("Size");
        if (value is null) return MobSizeData.Small;
        return value.Trim() switch
        {
            var v when string.Equals(v, "Small", StringComparison.OrdinalIgnoreCase) => MobSizeData.Small,
            var v when string.Equals(v, "Medium", StringComparison.OrdinalIgnoreCase) => MobSizeData.Medium,
            var v when string.Equals(v, "Large", StringComparison.OrdinalIgnoreCase) => MobSizeData.Big, // Pinned "Size_Large" constant name maps to SZ_BIG.
            _ => MobSizeData.Small,
        };
    }

    private static readonly Dictionary<string, MobRaceData> RaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Formless"] = MobRaceData.Formless, ["Undead"] = MobRaceData.Undead, ["Brute"] = MobRaceData.Brute,
        ["Plant"] = MobRaceData.Plant, ["Insect"] = MobRaceData.Insect, ["Fish"] = MobRaceData.Fish,
        ["Demon"] = MobRaceData.Demon, ["Demihuman"] = MobRaceData.DemiHuman, ["Angel"] = MobRaceData.Angel,
        ["Dragon"] = MobRaceData.Dragon, ["Player_Human"] = MobRaceData.PlayerHuman, ["Player_Doram"] = MobRaceData.PlayerDoram,
    };

    private static MobRaceData ReadRace(YamlMap node)
    {
        var value = node.Scalar("Race");
        if (value is null) return MobRaceData.Formless;
        return RaceNames.TryGetValue(value.Trim(), out var race) ? race : MobRaceData.Formless;
    }

    private static readonly Dictionary<string, MobElementData> ElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Neutral"] = MobElementData.Neutral, ["Water"] = MobElementData.Water, ["Earth"] = MobElementData.Earth,
        ["Fire"] = MobElementData.Fire, ["Wind"] = MobElementData.Wind, ["Poison"] = MobElementData.Poison,
        ["Holy"] = MobElementData.Holy, ["Dark"] = MobElementData.Dark, ["Ghost"] = MobElementData.Ghost,
        ["Undead"] = MobElementData.Undead,
    };

    private static MobElementData ReadElement(YamlMap node)
    {
        var value = node.Scalar("Element");
        if (value is null) return MobElementData.Neutral;
        return ElementNames.TryGetValue(value.Trim(), out var element) ? element : MobElementData.Neutral;
    }

    private static readonly Dictionary<string, MobClassData> ClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = MobClassData.Normal, ["Boss"] = MobClassData.Boss, ["Guardian"] = MobClassData.Guardian,
        ["Battlefield"] = MobClassData.Battlefield, ["Event"] = MobClassData.Event,
    };

    private static MobClassData ReadClass(YamlMap node)
    {
        var value = node.Scalar("Class");
        if (value is null) return MobClassData.Normal;
        return ClassNames.TryGetValue(value.Trim(), out var mobClass) ? mobClass : MobClassData.Normal;
    }

    // Reproduces pinned MobDatabase::parseBodyNode's mode resolution exactly (mob.cpp:5446-5519):
    // the pinned `Ai:` field resolves to one of the MONSTER_TYPE_NN preset bitmasks (the mob's
    // BASE status.mode), then each pinned `Modes:` entry individually ORs (true) or AND-NOTs
    // (false) its own bit on top of that preset - never treating the Modes: block as the complete
    // mode by itself. A block with no `Ai:` field defaults to the same MONSTER_TYPE_06=0 pinned
    // uses when the YAML field is entirely absent (mob.cpp default-constructs status.mode to 0
    // before any conditional field ever runs). Every named MD_* bit is preserved (ModeBitsByName
    // below covers the complete pinned bitmask, matching MobModeData exactly) - an unrecognized
    // mode NAME (never a valid pinned MD_* name, only a genuinely unknown/future one) is skipped
    // exactly like pinned source's own "Unknown monster mode %s, skipping" invalidWarning path, not
    // silently normalized into an unrelated bit.
    private static MobModeData ReadMode(YamlMap aiNode, YamlMap modesNode)
    {
        var mode = MobModeData.None;

        if (aiNode.Scalar("Ai") is { } ai)
        {
            // Unknown Ai defaults to MONSTER_TYPE_06=0, matching pinned invalidWarning fallback.
            mode = AiPresets.TryGetValue(ai.Trim(), out var preset) ? (MobModeData)preset : MobModeData.None;
        }

        foreach (var (name, active) in BooleanEntries(modesNode, "Modes"))
        {
            if (!ModeBitsByName.TryGetValue(name, out var bit)) continue; // Genuinely unrecognized mode name - pinned source's own "skip" fallback.
            mode = active ? mode | bit : mode & ~bit;
        }

        return mode;
    }

    // The complete pinned MD_* name -> bit table (doc/mob_db_mode_list.txt), matching MobModeData's
    // full bitmask exactly - every valid pinned Modes: entry name is recognized and its bit
    // retained, independent of whether MapServer's runtime executes that bit yet (see MobModeData's
    // own doc comment and RepositoryDomainAnalyzers' ModeData/ModeRuntime split).
    private static readonly Dictionary<string, MobModeData> ModeBitsByName = new(StringComparer.Ordinal)
    {
        ["CanMove"] = MobModeData.CanMove,
        ["Looter"] = MobModeData.Looter,
        ["Aggressive"] = MobModeData.Aggressive,
        ["Assist"] = MobModeData.Assist,
        ["CastSensorIdle"] = MobModeData.CastSensorIdle,
        ["NoRandomWalk"] = MobModeData.NoRandomWalk,
        ["NoCast"] = MobModeData.NoCast,
        ["CanAttack"] = MobModeData.CanAttack,
        ["CastSensorChase"] = MobModeData.CastSensorChase,
        ["ChangeChase"] = MobModeData.ChangeChase,
        ["Angry"] = MobModeData.Angry,
        ["ChangeTargetMelee"] = MobModeData.ChangeTargetMelee,
        ["ChangeTargetChase"] = MobModeData.ChangeTargetChase,
        ["TargetWeak"] = MobModeData.TargetWeak,
        ["RandomTarget"] = MobModeData.RandomTarget,
        ["IgnoreMelee"] = MobModeData.IgnoreMelee,
        ["IgnoreMagic"] = MobModeData.IgnoreMagic,
        ["IgnoreRanged"] = MobModeData.IgnoreRanged,
        ["Mvp"] = MobModeData.Mvp,
        ["IgnoreMisc"] = MobModeData.IgnoreMisc,
        ["KnockBackImmune"] = MobModeData.KnockBackImmune,
        ["TeleportBlock"] = MobModeData.TeleportBlock,
        ["FixedItemDrop"] = MobModeData.FixedItemDrop,
        ["Detector"] = MobModeData.Detector,
        ["StatusImmune"] = MobModeData.StatusImmune,
        ["SkillImmune"] = MobModeData.SkillImmune,
    };

    // Every key ParseMobNode reads, for rejecting typos in custom layers (see RathenaYaml.RejectUnknownKeys). Declared
    // after ModeBitsByName because static fields initialize in textual order. Modes: names must be real MD_* names
    // (the pinned path skips unknown ones); RaceGroups: names are open, like the pinned path.
    private static readonly IReadOnlySet<string> DropKeys = new HashSet<string>(StringComparer.Ordinal) { "Item", "Rate", "StealProtected", "RandomOptionGroup", "Index" };
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>?> CustomSchema =
        new[]
        {
            "Id", "AegisName", "Name", "JapaneseName", "Level", "Hp", "Sp", "BaseExp", "JobExp", "MvpExp", "Attack", "Attack2", "Defense",
            "MagicDefense", "Resistance", "MagicResistance", "Str", "Agi", "Vit", "Int", "Dex", "Luk", "AttackRange", "SkillRange", "ChaseRange",
            "Size", "Race", "RaceGroups", "Element", "ElementLevel", "WalkSpeed", "AttackDelay", "AttackMotion", "ClientAttackMotion",
            "DamageMotion", "DamageTaken", "GroupId", "Title", "Ai", "Class",
        }.ToDictionary(key => key, _ => (IReadOnlySet<string>?)null, StringComparer.Ordinal)
        .Concat(new Dictionary<string, IReadOnlySet<string>?>
        {
            ["Modes"] = ModeBitsByName.Keys.ToHashSet(StringComparer.Ordinal), ["Drops"] = DropKeys, ["MvpDrops"] = DropKeys,
        })
        .ToDictionary(StringComparer.Ordinal);

    // Parses the fixed rAthena spawn-declaration format:
    //   <map>,<x>,<y>[,<xs>,<ys>]\tmonster\t<Name>\t<MobId>,<Count>[,<Delay1>[,<Delay2>]]
    // Only lines whose Name matches `mobName` (rAthena's second field, purely
    // cosmetic/for grep-ability - the numeric MobId is authoritative) are
    // returned. `x,y` of `0,0` with no `xs,ys` means rAthena's mob_spawn
    // (mob.cpp:1117) treats the declared center as unusable (xs+ys<1) and
    // instead does a map-wide randomized candidate search bounded by
    // battle_config.map_edge_size, checked against real GAT walkability
    // (map.cpp:1798 map_search_freecell) - it is NOT literal coordinate (0,0)
    // and NOT "random within a small radius around (0,0)". See
    // IMobSpawnCellSelector for how Athena's runtime handles this given it has
    // no GAT/collision data source at all (a genuine data gap, not a
    // deliberately-skipped feature).
    // `excludedMaps` lets a caller drop rows for a map instance this MapServer
    // build genuinely does not serve at all. It must NEVER be used merely
    // because a row is a `duplicate(...)` family's generic/base template map
    // (e.g. `int_land` for the int_land/int_land01..04 G_PORING family) - an
    // earlier invocation of this command excluded `int_land` on the
    // (by-then-stale) assumption that the compiled Academy world never
    // registered anything there, mirroring a similar, since-corrected
    // over-exclusion in compile-npc-world's --exclude-placement usage (see
    // ai/world-data.md's "Runtime architecture" section and
    // WorldMapRegistryFamilyTests/PoringRandomSpawnIntegrationTests for the
    // regression coverage this caused).
    internal static IReadOnlyList<MobSpawnData> ReadMobSpawns(string spawnScriptText, string sourceFile, string mobName, IReadOnlySet<string>? excludedMaps = null, IReadOnlyDictionary<string, int>? aegisNameToId = null)
    {
        var results = new List<MobSpawnData>();
        var lines = spawnScriptText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryParseSpawnLine(lines[i], sourceFile, i + 1, aegisNameToId, out var spawn, out var name)) continue;
            if (!string.Equals(name, mobName, StringComparison.Ordinal)) continue;
            if (excludedMaps is not null && excludedMaps.Contains(spawn.Map)) continue;
            results.Add(spawn);
        }
        if (results.Count == 0) throw new ArgumentException($"No '{mobName}' monster spawn declarations were found in the pinned source.");
        return results;
    }

    // Every ordinary `monster` declaration in the file, regardless of mob name/id - the
    // generate-mob-spawns CLI command's own scan needs every declaration, not one name at a time
    // like ReadMobSpawns (which single-mob callers such as compile-mob-spawn still use). Shares the
    // SAME TryParseSpawnLine helper as ReadMobSpawns so there remains exactly one spawn-line parser
    // (task's "strong preference: one shared parser" - RepositoryDomainAnalyzers.AnalyzeMobSpawns
    // already calls ReadMobSpawns per-name; a future cleanup could switch it to this all-at-once
    // form too, but that is not required for parser unification since both paths already bottom out
    // in TryParseSpawnLine).
    internal static IReadOnlyList<MobSpawnData> ReadAllMobSpawns(string spawnScriptText, string sourceFile, IReadOnlyDictionary<string, int>? aegisNameToId = null)
    {
        var results = new List<MobSpawnData>();
        var lines = spawnScriptText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (TryParseSpawnLine(lines[i], sourceFile, i + 1, aegisNameToId, out var spawn, out _))
                results.Add(spawn);
        return results;
    }

    // One spawn-line parse failure isolated to its own line - never a whole-file poison. Distinct
    // from generate-mob-spawns' own fail-CLOSED behavior (a hard generation error is correct there:
    // production output must never silently omit or misrepresent a real declaration). The analyzer's
    // job is different - repository-wide REPORTING must keep going across the rest of a file/tree
    // even when one line is genuinely malformed, but it must never silently drop that line's
    // existence either. Line/Message are enough for a stable, deterministic diagnostic blocker id
    // (RepositoryDomainAnalyzers.AnalyzeMobSpawns turns this into its own DomainEntity, matching the
    // existing AnalyzeMobs try/catch-per-entity convention).
    internal sealed record MobSpawnLineFailure(int Line, string Message);

    // Deliberately MUCH broader than the full SpawnLine grammar - this only asks "does this line look
    // like it is TRYING to be an ordinary `monster` spawn declaration at all", never "is it a
    // well-formed one" (that remains SpawnLine/TryParseSpawnLine's job). Anchored on pinned
    // npc_parse's OWN dispatch condition, not merely "contains the word monster anywhere": pinned
    // splits a source line into tab-delimited w1/w2/w3/w4 fields and dispatches to npc_parse_mob only
    // when w2 (the SECOND field) is exactly "monster" (case-insensitive) -
    // `strcmpi(w2,"monster") == 0`, src/map/npc.cpp:5810. `^\S+\t+monster\t` requires a non-empty w1
    // (tolerating ANY future w1 shape - coordinates, missing coordinates, whatever) immediately
    // followed by a tab-delimited "monster" field - deliberately NOT a bare substring match, which
    // would (and, in an earlier draft of this fix, DID) false-positive on the unrelated `monster`
    // SCRIPT COMMAND (e.g. `monster "bat_a01",273,203,"Neutrality Flag",1911,1,...;` inside an
    // OnEnable: block - a runtime scripted spawn call, not a source declaration) and on plain English
    // prose in `mes "..."` dialogue text (e.g. "...the power of monsters..."). Anchoring on the exact
    // pinned w2-field dispatch position excludes both false-positive classes while still tolerating
    // ANY future syntax drift in w1's own coordinate shape or w3/w4's own field shape - exactly the
    // class of syntax drift that silently dropped 224 real bare-map-name declarations before the
    // SpawnLine grammar itself was fixed (see SpawnLine's own doc comment). `boss_monster` is a
    // DIFFERENT w2 value entirely (not merely a "monster" substring) and a deliberately separate,
    // unimplemented domain (task: MVP/boss_monster runtime out of scope) - `^\S+\t+monster\t`
    // (anchored start-to-end of the w2 field) never matches a `boss_monster` w2 value.
    private static readonly Regex OrdinaryMonsterCandidateLine = new(@"^\S+\t+monster\t", RegexOptions.IgnoreCase);

    // A commented-out ("//"-prefixed after trimming leading whitespace) or blank line is never a
    // candidate, regardless of what text follows the comment marker - matches
    // RepositoryDomainAnalyzers.IsCommentedOrBlank's own convention exactly (real pinned data has
    // 327 genuinely commented-out "monster"-containing lines, e.g.
    // "//gef_dun03,0,0\tmonster\tBaphomet\t1039,1,7200000,7200000" in
    // npc/re/mobs/dungeons/gef_dun.txt - these must stay silently ignored, not become false parse
    // failures).
    private static bool IsCommentedOrBlankLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    // Fail-OPEN, line-isolated variant of ReadAllMobSpawns for analyzer use. Three outcomes per line:
    //   1. Not an ordinary-monster candidate line at all (OrdinaryMonsterCandidateLine doesn't
    //      match) - ignored, exactly like ReadAllMobSpawns/TryParseSpawnLine's existing "continue"
    //      semantics for an unrelated line (NPC/script/warp/mapflag/comment/blank).
    //   2. A candidate line whose full SpawnLine grammar ALSO matches and resolves cleanly - a
    //      parsed MobSpawnData in Spawns.
    //   3. A candidate line that TryParseSpawnLine either doesn't match (regex-level syntax the
    //      current grammar doesn't understand) OR matches but whose subsequent
    //      resolution/validation throws (unknown AegisName, invalid declared level, or any future
    //      pinned syntax/value drift) - captured in Failures instead of being silently skipped OR
    //      aborting the scan for the rest of the file. Case 3's two sub-paths (regex non-match vs.
    //      thrown exception) are the fix for a real remaining gap: TryParseSpawnLine returning false
    //      previously meant ONLY "not a spawn declaration", even for a line that plainly WAS one by
    //      the broader OrdinaryMonsterCandidateLine test - collapsing "irrelevant line" and "spawn
    //      declaration SpawnLine no longer understands" into the same silent no-op.
    internal static (IReadOnlyList<MobSpawnData> Spawns, IReadOnlyList<MobSpawnLineFailure> Failures) TryReadAllMobSpawns(string spawnScriptText, string sourceFile, IReadOnlyDictionary<string, int>? aegisNameToId = null)
    {
        var results = new List<MobSpawnData>();
        var failures = new List<MobSpawnLineFailure>();
        var lines = spawnScriptText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsCommentedOrBlankLine(line) || !OrdinaryMonsterCandidateLine.IsMatch(line)) continue;
            try
            {
                if (TryParseSpawnLine(line, sourceFile, i + 1, aegisNameToId, out var spawn, out _))
                    results.Add(spawn);
                else
                    failures.Add(new MobSpawnLineFailure(i + 1, $"Line looks like an ordinary 'monster' spawn declaration but does not match the current SpawnLine grammar at {sourceFile}:{i + 1}."));
            }
            catch (Exception exception)
            {
                failures.Add(new MobSpawnLineFailure(i + 1, exception.Message));
            }
        }
        return (results, failures);
    }

    // Case-insensitive AegisName -> MobId lookup, matching pinned mobdb_search_aegisname's own
    // strcmpi comparison (src/map/mob.cpp:308-316) - built once by the caller from the same
    // ReadAllMobDefinitions result already used for MobId resolution elsewhere in this pipeline.
    internal static IReadOnlyDictionary<string, int> BuildAegisNameLookup(IReadOnlyList<MobDefinitionData> mobs) =>
        mobs.ToDictionary(mob => mob.AegisName, mob => mob.Id, StringComparer.OrdinalIgnoreCase);

    // Pinned npc_parse_mob (npc.cpp:5218): delay1 defaults to 5000 when the 3rd `w4` field is
    // absent (the local `int32 delay = 5000` initializer, never overwritten by sscanf when that
    // field is missing) - NOT 0. delay2 defaults to 0 (spawn_data is memset to 0 before parsing).
    // DeathEvent/Size/Ai follow the SAME omitted-means-null rule as Xs/Ys above; a present
    // DeathEvent has its optional surrounding quotes (the pinned tree's own convention for real
    // event labels - see SpawnLine's own doc comment) stripped so callers always see one logical
    // string regardless of source quoting style.
    // aegisNameToId resolves a non-numeric w4 mob token (pinned npc_parse_mob tries strtol first,
    // then falls back to mobdb_search_aegisname - src/map/npc.cpp:5258-5275). A null resolver means
    // the caller has no mob database loaded; a non-numeric token in that case fails closed exactly
    // like an unresolvable AegisName would (there is no valid interpretation of a non-numeric token
    // without a database to resolve it against).
    private static bool TryParseSpawnLine(string line, string sourceFile, int sourceLine, IReadOnlyDictionary<string, int>? aegisNameToId, out MobSpawnData spawn, out string name)
    {
        var match = SpawnLineRegex().Match(line);
        if (!match.Success) { spawn = null!; name = string.Empty; return false; }
        var spawnName = match.Groups["spawnname"].Value;
        name = spawnName;
        var map = match.Groups["map"].Value;
        var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
        var delay1Group = match.Groups["delay1"];
        var delay1 = delay1Group.Success ? int.Parse(delay1Group.Value, CultureInfo.InvariantCulture) : 5000;
        var delay2Group = match.Groups["delay2"];
        var delay2 = delay2Group.Success ? int.Parse(delay2Group.Value, CultureInfo.InvariantCulture) : 0;
        var xGroup = match.Groups["x"];
        var yGroup = match.Groups["y"];
        var x = xGroup.Success ? short.Parse(xGroup.Value, CultureInfo.InvariantCulture) : (short)0;
        var y = yGroup.Success ? short.Parse(yGroup.Value, CultureInfo.InvariantCulture) : (short)0;
        var xsGroup = match.Groups["xs"];
        var ysGroup = match.Groups["ys"];
        var xs = xsGroup.Success ? short.Parse(xsGroup.Value, CultureInfo.InvariantCulture) : (short)0;
        var ys = ysGroup.Success ? short.Parse(ysGroup.Value, CultureInfo.InvariantCulture) : (short)0;
        var eventGroup = match.Groups["event"];
        var deathEvent = eventGroup.Success ? StripQuotes(eventGroup.Value) : null;
        var sizeGroup = match.Groups["size"];
        var size = sizeGroup.Success ? ParseSize(sizeGroup.Value, sourceFile, sourceLine) : (MobSizeData?)null;
        var aiGroup = match.Groups["ai"];
        var ai = aiGroup.Success ? int.Parse(aiGroup.Value, CultureInfo.InvariantCulture) : (int?)null;
        var levelGroup = match.Groups["level"];
        var declaredLevel = levelGroup.Success ? int.Parse(levelGroup.Value, CultureInfo.InvariantCulture) : (int?)null;
        if (declaredLevel is 0 || declaredLevel > MaxPinnedMobLevel)
            throw new ArgumentException($"Invalid mob spawn level {declaredLevel} for '{spawnName}' at {sourceFile}:{sourceLine}; pinned rAthena requires 1-{MaxPinnedMobLevel} when the level field is present.");
        var mobToken = match.Groups["mobtoken"].Value;
        int mobId;
        if (int.TryParse(mobToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
        {
            mobId = numericId;
        }
        else if (aegisNameToId is not null && aegisNameToId.TryGetValue(mobToken, out var resolvedId))
        {
            mobId = resolvedId;
        }
        else
        {
            throw new ArgumentException($"Unknown mob AegisName token '{mobToken}' at {sourceFile}:{sourceLine}; expected a numeric mob ID or a resolvable AegisName.");
        }
        spawn = new MobSpawnData(
            map,
            mobId,
            count,
            delay1,
            delay2,
            sourceFile,
            sourceLine,
            x, y, xs, ys,
            spawnName, declaredLevel,
            deathEvent, size, ai);
        return true;
    }

    private static string StripQuotes(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    // Fail closed (task section 21): a present spawn-line size field outside pinned SZ_SMALL(0)..
    // SZ_BIG(2) is a genuine unsupported-syntax case, not silently coerced into a meaningless enum
    // member. No real pinned ordinary-monster declaration exercises this today (verified
    // exhaustively - zero rows have a 6th `w4` field at all), but a future pinned revision that adds
    // one must be caught here rather than corrupting generated output.
    private static MobSizeData ParseSize(string raw, string sourceFile, int sourceLine)
    {
        var value = int.Parse(raw, CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(typeof(MobSizeData), value))
            throw new ArgumentException($"Unsupported mob spawn size {value} at {sourceFile}:{sourceLine}; expected 0 (Small), 1 (Medium), or 2 (Big).");
        return (MobSizeData)value;
    }

    internal static string GenerateMobDefinition(MobDefinitionData mob, string commit, string className, string constantName, string sourceFile, int sourceLine) =>
        GenerateMobDefinitions([(mob, constantName)], commit, className, sourceFile, sourceLine);

    // One class, N mob constants - the same "one class, many constants" shape GeneratedItems and
    // the existing single-mob GeneratedMobs class already use (see GeneratedItems.cs). A single
    // mob is just the N=1 case, kept byte-identical to the original single-mob emission (verified
    // by CompilerTests) rather than a separate code path.
    // `note` adds one header comment line (generate-mobs uses it to mark the readable view shards).
    internal static string GenerateMobDefinitions(IReadOnlyList<(MobDefinitionData Mob, string ConstantName)> mobs, string commit, string className, string sourceFile, int sourceLine, string? note = null)
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(sourceFile).Append(':').Append(sourceLine.ToString(CultureInfo.InvariantCulture)).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.");
        if (note is not null) output.Append("// ").AppendLine(note);
        output
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            // Global game data ("what is mob <id>") - not world/placement data, so this does NOT
            // live under Generated.World.Izlude.Academy even though only the Academy slice
            // currently generates a mob. MobSpawnDefinition (map/count/respawn) is the
            // world-scoped counterpart and stays under Generated.World.Izlude.Academy.
            //
            // `partial`: large mob coverage is organized as multiple deterministic generated files
            // (one per pinned-source-derived category, e.g. GeneratedMobs.Monsters.cs today, a
            // future GeneratedMobs.Mvps.cs once real MVP-flagged mobs are generated) that all
            // contribute to this ONE class - consumers always reference GeneratedMobs.<Name>
            // regardless of which file actually declares it; file layout is purely organizational.
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine()
            .Append("internal static partial class ").AppendLine(className)
            .AppendLine("{");
        // One physical source line per MobDefinition (ascending MobId within this call's mob set):
        // these files are generated data that will eventually hold thousands of entries, where the
        // prior ~25-line-per-mob layout made large datasets hard to scan/grep/diff. Named arguments
        // are kept in full (never collapsed to positional/helper arguments) - only the line-break
        // formatting changes, not the emitted field set or values.
        foreach (var (mob, constantName) in mobs.OrderBy(item => item.Mob.Id))
        {
            output
                .Append("    internal static readonly MobDefinition ").Append(constantName).Append(" = new(Id: ").Append(mob.Id)
                .Append(", AegisName: \"").Append(mob.AegisName).Append("\", Name: \"").Append(mob.Name)
                .Append("\", Level: ").Append(mob.Level).Append(", MaxHp: ").Append(mob.Hp)
                .Append(", Attack: ").Append(mob.Attack).Append(", Attack2: ").Append(mob.Attack2)
                .Append(", Defense: ").Append(mob.Defense).Append(", MagicDefense: ").Append(mob.MagicDefense)
                .Append(", Str: ").Append(mob.Str).Append(", Agi: ").Append(mob.Agi)
                .Append(", Vit: ").Append(mob.Vit).Append(", Int: ").Append(mob.Int)
                .Append(", Dex: ").Append(mob.Dex).Append(", Luk: ").Append(mob.Luk)
                .Append(", AttackRange: ").Append(mob.AttackRange).Append(", WalkSpeed: ").Append(mob.WalkSpeed)
                .Append(", AttackDelay: ").Append(mob.AttackDelay).Append(", AttackMotion: ").Append(mob.AttackMotion)
                .Append(", DamageMotion: ").Append(mob.DamageMotion).Append(", BaseExp: ").Append(mob.BaseExp)
                .Append(", JobExp: ").Append(mob.JobExp).Append(", Mode: ").Append(FormatMode(mob.Mode))
                // A custom-layer definition names its own data/custom file; it has no rAthena commit (empty Commit).
                .Append(", Source: new WorldSourceInfo(\"").Append(mob.SourceFile is null ? "rAthena" : "Athena.NET").Append("\", \"").Append(mob.SourceFile is null ? commit : "")
                .Append("\", \"").Append(mob.SourceFile ?? sourceFile).Append("\", ").Append(mob.SourceLine == 0 ? sourceLine : mob.SourceLine).Append(')')
                .Append(", JapaneseName: ").Append(FormatNullableString(mob.JapaneseName))
                .Append(", MaxSp: ").Append(mob.Sp).Append(", MvpExp: ").Append(mob.MvpExp)
                .Append(", Resistance: ").Append(mob.Resistance).Append(", MagicResistance: ").Append(mob.MagicResistance)
                .Append(", SkillRange: ").Append(mob.SkillRange).Append(", ChaseRange: ").Append(mob.ChaseRange)
                .Append(", Size: MobSize.").Append(mob.Size).Append(", Race: MobRace.").Append(mob.Race)
                .Append(", Element: MobElement.").Append(mob.Element).Append(", ElementLevel: ").Append(mob.ElementLevel)
                .Append(", ClientAttackMotion: ").Append(mob.ClientAttackMotion).Append(", DamageTaken: ").Append(mob.DamageTaken)
                .Append(", GroupId: ").Append(mob.GroupId).Append(", Title: ").Append(FormatNullableString(mob.Title))
                .Append(", Class: MobClass.").Append(mob.Class)
                .Append(", RaceGroups: ").Append(FormatRaceGroups(mob.RaceGroups))
                .Append(", Drops: ").Append(FormatDrops(mob.Drops))
                .Append(", MvpDrops: ").Append(FormatDrops(mob.MvpDrops)).AppendLine(");");
        }
        output.AppendLine("}");
        return output.ToString();
    }

    // The runtime MobDefinition for one effective definition - the same field mapping GenerateMobDefinitions spells out as
    // a C# literal (MobModeData/MobSizeData/... share MapServer's numeric values; an empty list is null). Pinned
    // definitions are attributed to `sourceFile` at `commit`, custom-layer ones to their own data/custom file.
    internal static Athena.Net.MapServer.World.MobDefinition ToMobDefinition(MobDefinitionData mob, string commit, string sourceFile) => new(
        mob.Id, mob.AegisName, mob.Name, mob.Level, mob.Hp, mob.Attack, mob.Attack2, mob.Defense, mob.MagicDefense,
        mob.Str, mob.Agi, mob.Vit, mob.Int, mob.Dex, mob.Luk, mob.AttackRange, mob.WalkSpeed, mob.AttackDelay, mob.AttackMotion, mob.DamageMotion,
        mob.BaseExp, mob.JobExp, (Athena.Net.MapServer.World.MobMode)mob.Mode,
        mob.SourceFile is null
            ? new Athena.Net.MapServer.World.WorldSourceInfo("rAthena", commit, sourceFile, mob.SourceLine)
            : new Athena.Net.MapServer.World.WorldSourceInfo("Athena.NET", "", mob.SourceFile, mob.SourceLine),
        mob.JapaneseName, mob.Sp, mob.MvpExp, mob.Resistance, mob.MagicResistance, mob.SkillRange, mob.ChaseRange,
        (Athena.Net.MapServer.World.MobSize)mob.Size, (Athena.Net.MapServer.World.MobRace)mob.Race, (Athena.Net.MapServer.World.MobElement)mob.Element,
        mob.ElementLevel, mob.ClientAttackMotion, mob.DamageTaken, mob.GroupId, mob.Title, (Athena.Net.MapServer.World.MobClass)mob.Class,
        mob.RaceGroups.Count == 0 ? null : mob.RaceGroups.Select(entry => new Athena.Net.MapServer.World.MobRaceGroupEntry(entry.Name, entry.Value)).ToArray(),
        Drops(mob.Drops), Drops(mob.MvpDrops));

    private static Athena.Net.MapServer.World.MobDropEntry[]? Drops(IReadOnlyList<MobDropEntryData> drops) =>
        drops.Count == 0 ? null : drops.Select(drop => new Athena.Net.MapServer.World.MobDropEntry(drop.Item, drop.Rate, drop.StealProtected, drop.RandomOptionGroup)).ToArray();

    internal const string ReadableViewNote = "Readable view of the effective definitions: NOT compiled (MapServer.csproj Compile Remove); runtime data is AthenaMobs.bin via MobCatalog.";

    // Compiled mob shard over the mob data pack: one single-line `GeneratedMobs.<Symbol>` property per mob (ascending id),
    // resolving through MobCatalog. Every mob must be in `pack` before its property is emitted, so a mob removed from the
    // effective dataset loses its property and every generated reference to it fails to compile, as with the old literals.
    internal static string GenerateMobFacades(IReadOnlyList<GeneratedMobSymbol> mobs, Athena.Net.MapServer.World.MobPack pack, string commit, string className, string sourceFile)
    {
        var packed = pack.Mobs.Select(mob => mob.Id).ToHashSet();
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(sourceFile).AppendLine(" (data: AthenaMobs.bin via MobCatalog)")
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine()
            .Append("internal static partial class ").AppendLine(className)
            .AppendLine("{");
        foreach (var item in mobs.OrderBy(item => item.Mob.Id))
        {
            if (!packed.Contains(item.Mob.Id)) throw new ArgumentException($"Mob {item.Mob.Id} ({item.Mob.AegisName}) is not in the mob data pack; refusing to emit GeneratedMobs.{item.Symbol}.");
            output.Append("    internal static MobDefinition ").Append(item.Symbol).Append(" => MobCatalog.Current.ById[").Append(item.Mob.Id).AppendLine("];");
        }
        return output.AppendLine("}").ToString();
    }

    internal static string GenerateMobRegistry(IReadOnlyList<GeneratedMobSymbol> mobs, string commit, string sourceFile)
    {
        var ordered = mobs.OrderBy(item => item.Mob.Id).ToArray();
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(sourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine()
            .AppendLine("internal static class GeneratedMobRegistry")
            .AppendLine("{")
            .AppendLine("    private static readonly IReadOnlyDictionary<int, MobDefinition> ById = new Dictionary<int, MobDefinition>")
            .AppendLine("    {");
        foreach (var item in ordered)
            output.Append("        [").Append(item.Mob.Id).Append("] = GeneratedMobs.").Append(item.Symbol).AppendLine(",");
        output
            .AppendLine("    };")
            .AppendLine()
            .AppendLine("    internal static int Count => ById.Count;")
            .AppendLine("    internal static IEnumerable<int> Ids => ById.Keys;")
            .AppendLine("    internal static IEnumerable<MobDefinition> All => ById.Values;")
            .AppendLine("    internal static bool TryGet(int mobId, out MobDefinition mob) => ById.TryGetValue(mobId, out mob!);")
            .AppendLine("    internal static MobDefinition Get(int mobId) => ById.TryGetValue(mobId, out var mob)")
            .AppendLine("        ? mob")
            .AppendLine("        : throw new KeyNotFoundException($\"Unknown generated mob Id {mobId}.\");")
            .AppendLine("}");
        return output.ToString();
    }

    internal static bool IsOwnedGeneratedMobFile(string path, string className, string category)
    {
        var name = Path.GetFileName(path);
        if (!(name == $"{className}.Registry.cs" ||
              (name.StartsWith($"{className}.{category}.", StringComparison.Ordinal) && name.EndsWith(".cs", StringComparison.Ordinal))))
            return false;
        using var reader = new StreamReader(path);
        return string.Equals(reader.ReadLine(), "// <auto-generated>", StringComparison.Ordinal) &&
               string.Equals(reader.ReadLine(), "// Generated by Athena.WorldCompiler.", StringComparison.Ordinal);
    }

    // Same safe-stale-cleanup contract as IsOwnedGeneratedMobFile (task section 36: delete only
    // files this generator owns, by filename-prefix PLUS header validation) - adapted for
    // generate-mob-spawns' own one-file-per-source-file shape (`<className>.<Suffix>.cs` for every
    // pinned NPC source file, `<className>.Registry.cs` for the map-keyed aggregation), where
    // "category" has no meaning (there is only ever one category: mob spawns).
    // Map-oriented spawn generation (ai/world-data.md's "Generated mob spawns" section) emits one
    // physical file per map/world-family module (e.g. "PrtFild08MobSpawns.cs",
    // "PayFild01MobSpawns.cs", "EvtZombieMobSpawns.cs") rather than the earlier source-file-sharded
    // "<className>.<Suffix>.cs" shape - every generated spawn file's own basename ends with the
    // fixed "MobSpawns.cs" suffix (explicit plural: each file/class owns a COLLECTION of
    // MobSpawnDefinition records, and "Spawn" alone was ambiguous against future NPC/boss/dynamic
    // spawn concepts), distinguishing it unambiguously from hand-maintained sibling files in the
    // same folder (*Npcs.cs/*Warps.cs/*World.cs, Scripts/) which never end that way. The bare
    // "Spawn.cs" suffix is ALSO recognized here purely so a one-time regeneration after this rename
    // can safely delete the previous generation's now-stale files - the generator itself never
    // emits that suffix anymore. The registry itself is named literally `registryFileName` (e.g.
    // "GeneratedMobSpawnRegistry.cs", deliberately unchanged - "MobSpawn" was already the correct
    // compound noun there). Both still require the standard auto-generated header before deletion -
    // filename pattern alone is not proof of ownership.
    internal static bool IsOwnedGeneratedMobSpawnFile(string path, string registryFileName, string? profilesFileName = null)
    {
        var name = Path.GetFileName(path);
        if (!(name == registryFileName ||
              name == profilesFileName ||
              name.EndsWith("MobSpawns.cs", StringComparison.Ordinal) ||
              name.EndsWith("Spawn.cs", StringComparison.Ordinal)))
            return false;
        using var reader = new StreamReader(path);
        return string.Equals(reader.ReadLine(), "// <auto-generated>", StringComparison.Ordinal) &&
               string.Equals(reader.ReadLine(), "// Generated by Athena.WorldCompiler.", StringComparison.Ordinal);
    }

    internal static string GenerateMobSpawns(IReadOnlyList<MobSpawnData> spawns, string mobDefinitionExpression, string commit, string className, string arrayName, string worldNamespace = "Athena.Net.MapServer.Generated.World.Izlude.Academy") =>
        GenerateMobSpawnGroups([(spawns, mobDefinitionExpression, arrayName)], commit, className, worldNamespace);

    // Map-centric shape: ONE `MobSpawnDefinition[] All` array per file, where each entry may
    // reference a DIFFERENT mob's global GeneratedMobs.* constant (a single real map commonly hosts
    // several different mobs) - distinct from GenerateMobSpawnGroups' "one array per mob, several
    // maps mixed into it" shape used by the Academy slice. `entries` is (Spawn, MobDefinitionExpression)
    // pairs, already filtered/ordered by the caller to belong to exactly one map.
    internal static string GenerateMobSpawnsForMap(IReadOnlyList<(MobSpawnData Spawn, string MobDefinitionExpression)> entries, string commit, string className, string worldNamespace)
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(entries[0].Spawn.SourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .Append("namespace ").Append(worldNamespace).AppendLine(";")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{")
            .AppendLine("    internal static readonly MobSpawnDefinition[] All =")
            .AppendLine("    [");
        foreach (var (spawn, mobDefinitionExpression) in entries) AppendSpawnEntry(output, spawn, mobDefinitionExpression, commit);
        output.AppendLine("    ];").AppendLine("}");
        return output.ToString();
    }

    // Consolidated DUPLICATE-FAMILY shape (e.g. pinned prt_fild08/a/b/c/d): one class, one
    // `MobSpawnDefinition[]` array per CONCRETE map (each entry keeps its own exact map string and
    // source provenance - the five maps are never collapsed into one runtime/template identity),
    // plus one composed `All` array that concatenates every per-map array (never a duplicated
    // re-listing of the same entries) for callers that want the family's complete population.
    // `mapEntries` is (ArrayName, Entries) pairs in caller-supplied order - e.g.
    // [("PrtFild08", prt_fild08 rows), ("PrtFild08A", prt_fild08a rows), ...]. ArrayName is a
    // fully caller-supplied identifier, deliberately kept separate from PascalCaseMapName's own
    // map-NAME casing convention: a family's per-map array name (e.g. "PrtFild08A") uses a
    // different capitalization from that same map's own file/type name elsewhere (e.g.
    // "PrtFild08a"), and is not itself derived by this function.
    internal static string GenerateMobSpawnFamily(IReadOnlyList<(string ArrayName, IReadOnlyList<(MobSpawnData Spawn, string MobDefinitionExpression)> Entries)> mapEntries, string commit, string className, string worldNamespace)
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(mapEntries[0].Entries[0].Spawn.SourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .Append("namespace ").Append(worldNamespace).AppendLine(";")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{");
        var arrayNames = new List<string>();
        foreach (var (arrayName, entries) in mapEntries)
        {
            arrayNames.Add(arrayName);
            output
                .Append("    internal static readonly MobSpawnDefinition[] ").Append(arrayName).AppendLine(" =")
                .AppendLine("    [");
            foreach (var (spawn, mobDefinitionExpression) in entries) AppendSpawnEntry(output, spawn, mobDefinitionExpression, commit);
            output.AppendLine("    ];");
        }
        output
            .AppendLine("    internal static readonly MobSpawnDefinition[] All =")
            .Append("        [.. ").Append(string.Join(", .. ", arrayNames)).AppendLine("];")
            .AppendLine("}");
        return output.ToString();
    }

    // One class, N named MobSpawnDefinition[] arrays (one per mob) - mirrors GenerateMobDefinitions'
    // "one class, many constants" shape. A single mob/array is the N=1 case, kept byte-identical to
    // the original single-array emission (verified by CompilerTests).
    internal static string GenerateMobSpawnGroups(IReadOnlyList<(IReadOnlyList<MobSpawnData> Spawns, string MobDefinitionExpression, string ArrayName)> groups, string commit, string className, string worldNamespace = "Athena.Net.MapServer.Generated.World.Izlude.Academy")
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(groups[0].Spawns[0].SourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            // World/placement data - lives under whichever area namespace the caller supplies
            // (defaulting to Generated.World.Izlude.Academy for existing invocations), unlike the
            // MobDefinition it references (global game data, see GenerateMobDefinition above).
            .Append("namespace ").Append(worldNamespace).AppendLine(";")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{");
        foreach (var (spawns, mobDefinitionExpression, arrayName) in groups)
        {
            output
                .Append("    internal static readonly MobSpawnDefinition[] ").Append(arrayName).AppendLine(" =")
                .AppendLine("    [");
            foreach (var spawn in spawns) AppendSpawnEntry(output, spawn, mobDefinitionExpression, commit);
            output.AppendLine("    ];");
        }
        output.AppendLine("}");
        return output.ToString();
    }

    // Shared `new(...)` emission for one MobSpawnDefinition, reused by every GenerateMobSpawn*
    // shape above so the emitted argument list/formatting can never drift between them. SpawnName is
    // always emitted (a required, always-present source fact - see MobSpawnData's own doc comment).
    // DeclaredLevel/DeathEvent/Size/Ai are only emitted as named arguments when the source actually
    // supplied a value - keeps the overwhelming majority (declarations with none of these four
    // optional fields) exactly as compact as before this branch added them.
    private static void AppendSpawnEntry(StringBuilder output, MobSpawnData spawn, string mobDefinitionExpression, string commit)
    {
        output.Append("        new(").Append(mobDefinitionExpression).Append(", \"").Append(spawn.Map).Append("\", ")
            .Append(spawn.Count).Append(", ").Append(spawn.RespawnDelay).Append(", ").Append(spawn.RespawnRandomDelay)
            .Append(", new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(spawn.SourceFile).Append("\", ").Append(spawn.SourceLine).Append(')')
            .Append(", X: ").Append(spawn.X).Append(", Y: ").Append(spawn.Y).Append(", Xs: ").Append(spawn.Xs).Append(", Ys: ").Append(spawn.Ys)
            .Append(", SpawnName: \"").Append(EscapeForCSharpString(spawn.SpawnName)).Append('"');
        if (spawn.DeclaredLevel is not null) output.Append(", DeclaredLevel: ").Append(spawn.DeclaredLevel);
        if (spawn.DeathEvent is not null) output.Append(", DeathEvent: \"").Append(spawn.DeathEvent.Replace("\"", "\\\"")).Append('"');
        if (spawn.Size is not null) output.Append(", Size: MobSize.").Append(spawn.Size);
        if (spawn.Ai is not null) output.Append(", Ai: ").Append(spawn.Ai);
        output.AppendLine("),");
    }

    // Emits a C# MobMode expression matching the generated definition's flags exactly - "None"
    // when no bit is set, otherwise a `|`-joined list of every SET MobMode member name (the
    // complete pinned bitmask, not only the runtime-executed subset - see MobModeData's own doc
    // comment).
    private static readonly (MobModeData Bit, string Name)[] ModeBitOrder =
    [
        (MobModeData.CanMove, "CanMove"), (MobModeData.Looter, "Looter"), (MobModeData.Aggressive, "Aggressive"),
        (MobModeData.Assist, "Assist"), (MobModeData.CastSensorIdle, "CastSensorIdle"), (MobModeData.NoRandomWalk, "NoRandomWalk"),
        (MobModeData.NoCast, "NoCast"), (MobModeData.CanAttack, "CanAttack"), (MobModeData.CastSensorChase, "CastSensorChase"),
        (MobModeData.ChangeChase, "ChangeChase"), (MobModeData.Angry, "Angry"), (MobModeData.ChangeTargetMelee, "ChangeTargetMelee"),
        (MobModeData.ChangeTargetChase, "ChangeTargetChase"), (MobModeData.TargetWeak, "TargetWeak"), (MobModeData.RandomTarget, "RandomTarget"),
        (MobModeData.IgnoreMelee, "IgnoreMelee"), (MobModeData.IgnoreMagic, "IgnoreMagic"), (MobModeData.IgnoreRanged, "IgnoreRanged"),
        (MobModeData.Mvp, "Mvp"), (MobModeData.IgnoreMisc, "IgnoreMisc"), (MobModeData.KnockBackImmune, "KnockBackImmune"),
        (MobModeData.TeleportBlock, "TeleportBlock"), (MobModeData.FixedItemDrop, "FixedItemDrop"), (MobModeData.Detector, "Detector"),
        (MobModeData.StatusImmune, "StatusImmune"), (MobModeData.SkillImmune, "SkillImmune"),
    ];

    private static string FormatMode(MobModeData mode)
    {
        if (mode == MobModeData.None) return "MobMode.None";
        var parts = ModeBitOrder.Where(entry => mode.HasFlag(entry.Bit)).Select(entry => "MobMode." + entry.Name).ToArray();
        return string.Join(" | ", parts);
    }

    // Emits a C# null literal or an escaped string literal - JapaneseName/Title are genuinely
    // Optional pinned fields (mob_db.yml doc comment) unlike AegisName/Name, which pinned source
    // treats as always-present per-block identifiers.
    private static string FormatNullableString(string? value) =>
        value is null ? "null" : "\"" + EscapeForCSharpString(value) + "\"";

    // Emits a C# collection-expression literal for RaceGroups - "null" when the pinned block has no
    // RaceGroups: section at all (distinct from an explicit empty list, which pinned source cannot
    // actually produce, since an empty `RaceGroups:` header with no entries never appears in real
    // data - but the record's own nullable default matches "field absent" for every other optional
    // block on this record, so this keeps that convention rather than inventing a special case).
    private static string FormatRaceGroups(IReadOnlyList<MobRaceGroupEntryData> entries)
    {
        if (entries.Count == 0) return "null";
        var parts = entries.Select(entry => $"new MobRaceGroupEntry(\"{EscapeForCSharpString(entry.Name)}\", {(entry.Value ? "true" : "false")})");
        return "[" + string.Join(", ", parts) + "]";
    }

    // Emits a C# collection-expression literal for Drops/MvpDrops - "null" when the pinned block
    // has no such section (see FormatRaceGroups' own doc comment for the same "absent vs empty"
    // rationale).
    private static string FormatDrops(IReadOnlyList<MobDropEntryData> entries)
    {
        if (entries.Count == 0) return "null";
        var parts = entries.Select(entry =>
            $"new MobDropEntry(\"{EscapeForCSharpString(entry.Item)}\", {entry.Rate.ToString(CultureInfo.InvariantCulture)}, {(entry.StealProtected ? "true" : "false")}, {FormatNullableString(entry.RandomOptionGroup)})");
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string EscapeForCSharpString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static int CountLines(string text, int exclusiveEnd)
    {
        var line = 1;
        for (var i = 0; i < exclusiveEnd; i++) if (text[i] == '\n') line++;
        return line;
    }

    // `event` mirrors pinned sscanf's `%77[^,]` - it stops at the next comma (never containing one
    // itself), so `[^,\t\r\n]+` is a faithful capture whether or not the source line wraps it in
    // literal quotes (the pinned tree's own convention for real death-event labels, verified: every
    // real event value found in the inventory is `"map::Label"`-quoted; the inert `0`/`1`
    // placeholder values are bare). Quotes, if present, are stripped by the caller so DeathEvent
    // always stores the same logical string regardless of source quoting style. `size`/`ai` are
    // bare non-negative integers per pinned `%11d` - zero real occurrences exist in the pinned
    // ordinary-monster domain today (verified exhaustively), but the groups exist so a future
    // pinned revision that adds one is captured, not silently truncated the way this regex used to
    // drop everything past delay2.
    //
    // w3 = `<mob name>{,<mob level>}` (pinned `sscanf(w3, "%23[^,],%11d", mobname, &mob_lv)`,
    // npc.cpp:5234): `spawnname` captures the name token up to an optional comma-separated level;
    // `level` is SIGNED (`-?\d+`, matching pinned's signed `%d`, not an unsigned-only grammar) so a
    // present negative value is captured distinctly from the field being entirely omitted (handled
    // in TryParseSpawnLine's own validity/effective-override logic - see MobSpawnData's doc
    // comment).
    //
    // w4's first token (`mobtoken`) is `[^,\t]+` rather than `\d+`-only: pinned npc_parse_mob accepts
    // either a numeric mob ID or an AegisName token (`strtol` first, `mobdb_search_aegisname`
    // fallback - npc.cpp:5258-5275) - resolved in TryParseSpawnLine, not by this regex.
    //
    // w1's `,<x>,<y>{,<xs>,<ys>}` suffix is ENTIRELY optional: pinned sscanf's own success
    // condition is `w1count >= 1` (npc.cpp:5233) - a bare map name with no coordinates at all is
    // valid pinned syntax (x/y/xs/ys all stay at their memset-zero spawn_data default). Every real
    // AegisName-mob-token declaration in the pinned tree (15 total, e.g.
    // npc/re/mobs/dungeons/sp_rudus.txt:26 `sp_rudus4\tmonster\tGiant Caput\tGIANT_CAPUT,75`) uses
    // exactly this bare form - an EARLIER version of this regex required `,x,y` unconditionally,
    // which silently dropped these 15 real declarations entirely (never generated, never analyzed) -
    // a genuine parser gap, not a deliberately narrower scope.
    //
    // KNOWN FOLLOW-UP GAP (deliberately NOT fixed here - see ai/follow-up/mob-spawn-map-token-gap.md
    // for the full deterministic inventory): `map` still uses the narrower `[A-Za-z0-9_]+`, not
    // pinned's actual w1 sscanf grammar (`"%15[^,],%6hd,%6hd,%6hd,%6hd"`, npc.cpp:3901's sibling
    // parse), which places NO character-class restriction on the map token beyond "not a comma".
    // Real pinned map names use `-` (e.g. `pvp_n_1-2`..`pvp_n_8-5`, `new_1-3`..`new_5-3`) and `@`
    // (instance-map convention, e.g. `1@md_gef`) that this narrower class rejects - 171 real, ACTIVE
    // (non-commented) declarations across 8 files are silently unmatched by SpawnLine today. Every
    // one of those 171 declarations' map AND MobId tokens independently resolve fine (verified
    // directly) - resolution is not the blocker, only this regex's character class is. Widening it
    // is deliberately NOT done in this hardening pass: it is shared by generate-mob-spawns
    // (Program.cs), so widening it changes that command's own resolved/generated declaration set -
    // out of scope for an analyzer-only hardening change, and it ALSO requires
    // `PascalCaseMapName`/folder-naming to be hardened first (`pvp_n_1-2`/`1@md_gef` do not produce
    // valid C# identifiers today). An earlier, EXPERIMENTAL over-broad widening attempt
    // (`[^,\t\r\n]+`) was tried and discarded specifically because it also matched commented-out
    // declarations (`//pvp_n_1-2,0,0\tmonster\t...`, with `map` capturing the literal `"//..."`
    // prefix) - a real correctness bug in that attempt, not evidence of a SECOND gap; see the
    // follow-up doc's "False lead" section. 10,068 (this branch's repository ordinary-monster count)
    // is therefore the count reachable by the CURRENTLY SUPPORTED grammar, not a claim of exhaustive
    // pinned coverage.
    private static readonly Regex SpawnLine = new(@"^(?<map>[A-Za-z0-9_]+)(?:,(?<x>-?\d+),(?<y>-?\d+)(?:,(?<xs>\d+),(?<ys>\d+))?)?\t+monster\t+(?<spawnname>[^,\t]+)(?:,(?<level>-?\d+))?\t+(?<mobtoken>[^,\t]+),(?<count>\d+)(?:,(?<delay1>\d+)(?:,(?<delay2>\d+)(?:,(?<event>[^,\t\r\n]+)(?:,(?<size>\d+)(?:,(?<ai>\d+))?)?)?)?)?", RegexOptions.None);
    private static Regex SpawnLineRegex() => SpawnLine;
}
