using System.Text;
namespace Athena.WorldCompiler.Generation;

// Pinned RefineDatabase (db/re/refine.yml, status.cpp:122-406). Covers both halves this project
// now consumes: the flat Bonus/RandomBonus the status-calculation formula reads (status.cpp:3953/
// 3977/3986/4014/6325-6327), and the Chances[] cost table (Rate/Price/Material/BreakingRate/
// DowngradeAmount) the refine-ACTION mutation needs (CharacterRefineService) to validate and charge
// an actual refine attempt (clif.cpp:22746-22810's pc_payzeny/pc_delitem/rnd()%10000 sequence).
// BlacksmithBlessingAmount/BroadcastSuccess/BroadcastFailure and the client Refine UI packets
// (0xAA0-0xAA3, no capture evidence - ai/iro-2026-wire.md) remain out of scope.
//
// Only the Weapon and Armor groups are modeled: this project's EquipmentStats has no shadow-gear
// slot/weapon concept at all yet (Shadow_Armor/Shadow_Weapon are simply not in the emitted table -
// a lookup for them is a generation-time omission, not a runtime default-to-zero).
internal static class RefineDataCompiler
{
    private static readonly string[] Groups = ["Weapon", "Armor"];

    // Chances[].Type values (pinned e_refine_cost_type, status.hpp:79-83) - the YAML's own string
    // spelling, not re-derived from any generated constant table (refine.yml is loaded standalone,
    // before any script-constant table exists).
    internal static readonly string[] CostTypes = ["Normal", "HD", "Enriched"];

    // One Chances[] entry: Rate (0-10000), Price (zeny), Material (resolved AegisName -> real
    // item_db Id), BreakingRate (0-10000), DowngradeAmount (refine levels lost on a non-breaking
    // failure). Absent fields default to 0, matching the pinned YAML's own documented defaults.
    internal readonly record struct RefineCost(uint Rate, uint Price, int MaterialItemId, uint BreakingRate, uint DowngradeAmount);

    // Both fields this project's formulas consume from a (Group, Level, RefineLevel) row: Bonus (flat
    // ATK2/MATK/DEF refine term, status.cpp:3953/3977/4014) and RandomBonus - despite its name, NOT an
    // RNG roll: status_calc_pc always reads it as a fixed per-(level,refine) maximum, stored as
    // wd->overrefine = info->randombonus_max/100 (status.cpp:3986) and applied deterministically in the
    // live player MATK formula as `matk_min++; matk_max += overrefine - 1;` (status.cpp:6325-6327) -
    // i.e. an extra, asymmetric MATK-range widening on top of the wMatk/variance term, present only from
    // the refine level where the pinned table starts tabulating a nonzero RandomBonus (weapon level 1
    // starts at refine 9; see RealPinnedData_WeaponLevel1_RandomBonusIsZeroBeforeRefineNineThenRises).
    // Costs is null when the pinned RefineLevels[] entry has no Chances block at all (e.g. refine 0 is never
    // tabulated; some early refine levels on some groups have Bonus only) - a real "no cost data", not a
    // generation gap, matching Chances' own documented default (null).
    internal readonly record struct RefineLevelBonus(uint Bonus, uint RandomBonusMax, IReadOnlyDictionary<string, RefineCost>? Costs);

    // [group][level][refineLevel] -> (Bonus, RandomBonusMax, Costs). level/refineLevel are both the pinned
    // YAML's own 1-based numbering (WeaponLevel/ArmorLevel 1-5, RefineLevels[].Level 1-20) - callers
    // subtract 1 themselves to match this project's own 0-based arrays elsewhere, exactly like the
    // pinned loader's own "Database is 1 based, code is 0 based" comment (status.cpp:188-189).
    //
    // resolveMaterialItemId resolves a Chances[].Material AegisName (e.g. "Phracon") to its real item_db Id -
    // refine.yml stores the material by name, not by numeric id, and this file never duplicates item_db's own
    // loading, so the caller supplies the lookup (Program.cs builds it once from db/re/item_db_*.yml and
    // reuses it across every refine entry, rather than this file re-reading item_db itself per material).
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<int, RefineLevelBonus>>> BuildTable(
        Func<string, string> readFile, Func<string, int> resolveMaterialItemId, string basePath = "db/re/refine.yml")
    {
        var yaml = readFile(basePath);
        var root = SimpleYaml.Parse(yaml, basePath);
        var body = root.Required("Body", basePath).Sequence($"{basePath} Body");

        var table = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<int, RefineLevelBonus>>>(StringComparer.Ordinal);
        foreach (var (node, groupIndex) in body.Items.Select((n, i) => (n, i)))
        {
            var groupEntry = node.Map($"{basePath} Body[{groupIndex}]");
            var groupName = groupEntry.Required("Group", $"{basePath} Body[{groupIndex}]").Scalar("Group");
            if (!Groups.Contains(groupName, StringComparer.Ordinal)) continue; // Shadow_Armor/Shadow_Weapon: not modeled (see this file's own doc comment).

            var levels = new Dictionary<int, IReadOnlyDictionary<int, RefineLevelBonus>>();
            foreach (var levelNode in groupEntry.Required("Levels", groupName).Sequence($"{groupName} Levels").Items)
            {
                var levelEntry = levelNode.Map($"{groupName} Levels[]");
                var level = (int)levelEntry.Required("Level", groupName).UShort($"{groupName} Level");

                var refineLevels = new Dictionary<int, RefineLevelBonus>();
                foreach (var refineLevelNode in levelEntry.Required("RefineLevels", $"{groupName} Level {level}").Sequence($"{groupName} Level {level} RefineLevels").Items)
                {
                    var refineLevelEntry = refineLevelNode.Map($"{groupName} Level {level} RefineLevels[]");
                    var refineLevel = (int)refineLevelEntry.Required("Level", $"{groupName} Level {level}").UShort($"{groupName} Level {level} RefineLevels[].Level");
                    var context = $"{groupName} Level {level} RefineLevel {refineLevel}";
                    var bonus = refineLevelEntry.Optional("Bonus") is { } bonusNode ? (uint)bonusNode.UShort($"{context} Bonus") : 0u;
                    var randomBonus = refineLevelEntry.Optional("RandomBonus") is { } randomBonusNode ? (uint)randomBonusNode.UShort($"{context} RandomBonus") : 0u;

                    Dictionary<string, RefineCost>? costs = null;
                    if (refineLevelEntry.Optional("Chances") is { } chancesNode)
                    {
                        costs = new Dictionary<string, RefineCost>(StringComparer.Ordinal);
                        foreach (var chanceNode in chancesNode.Sequence($"{context} Chances").Items)
                        {
                            var chanceEntry = chanceNode.Map($"{context} Chances[]");
                            var costType = chanceEntry.Required("Type", context).Scalar($"{context} Chances[].Type");
                            if (!CostTypes.Contains(costType, StringComparer.Ordinal))
                                throw new ArgumentException($"{context} Chances[].Type '{costType}' is not one of {string.Join('/', CostTypes)}.");
                            var rate = chanceEntry.Optional("Rate") is { } rateNode ? (uint)rateNode.UShort($"{context} {costType} Rate") : 0u;
                            var price = chanceEntry.Optional("Price") is { } priceNode ? (uint)priceNode.ULong($"{context} {costType} Price") : 0u;
                            var materialName = chanceEntry.Required("Material", context).Scalar($"{context} {costType} Material");
                            var breakingRate = chanceEntry.Optional("BreakingRate") is { } breakingNode ? (uint)breakingNode.UShort($"{context} {costType} BreakingRate") : 0u;
                            var downgradeAmount = chanceEntry.Optional("DowngradeAmount") is { } downgradeNode ? (uint)downgradeNode.UShort($"{context} {costType} DowngradeAmount") : 0u;
                            costs[costType] = new RefineCost(rate, price, resolveMaterialItemId(materialName), breakingRate, downgradeAmount);
                        }
                    }
                    refineLevels[refineLevel] = new RefineLevelBonus(bonus, randomBonus, costs);
                }
                levels[level] = refineLevels;
            }
            table[groupName] = levels;
        }

        foreach (var group in Groups)
            if (!table.ContainsKey(group)) throw new ArgumentException($"{basePath} has no Group entry for '{group}'.");
        return table;
    }

    internal static string Generate(IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<int, RefineLevelBonus>>> table, string commit)
    {
        var builder = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .AppendLine("// Source: legacy/rathena/db/re/refine.yml (Weapon/Armor groups only - see RefineDataCompiler's own doc comment)")
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Refine;")
            .AppendLine()
            .AppendLine("internal static class GeneratedRefineBonus")
            .AppendLine("{")
            .AppendLine("    // [group][level][refineLevel] -> (Bonus, RandomBonusMax), both x100 (pinned s_refine_level_info.bonus")
            .AppendLine("    // / randombonus_max). All keys are the pinned YAML's own 1-based WeaponLevel/ArmorLevel/RefineLevels[].Level numbering.")
            .AppendLine("    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<int, (uint Bonus, uint RandomBonusMax)>>> Table = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<int, (uint, uint)>>>")
            .AppendLine("    {");
        foreach (var (group, levels) in table.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("        [\"").Append(group).AppendLine("\"] = new Dictionary<int, IReadOnlyDictionary<int, (uint, uint)>>");
            builder.AppendLine("        {");
            foreach (var (level, refineLevels) in levels.OrderBy(pair => pair.Key))
            {
                builder.Append("            [").Append(level).AppendLine("] = new Dictionary<int, (uint, uint)>");
                builder.AppendLine("            {");
                foreach (var (refineLevel, value) in refineLevels.OrderBy(pair => pair.Key))
                    builder.Append("                [").Append(refineLevel).Append("] = (").Append(value.Bonus).Append(", ").Append(value.RandomBonusMax).AppendLine("),");
                builder.AppendLine("            },");
            }
            builder.AppendLine("        },");
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    // [group][level][refineLevel][costType] -> (Rate, Price, MaterialItemId, BreakingRate, DowngradeAmount),");
        builder.AppendLine("    // the pinned Chances[] cost table (status.hpp:92-99). costType is the pinned YAML's own Type spelling");
        builder.AppendLine("    // (\"Normal\"/\"HD\"/\"Enriched\"). A (group,level,refineLevel) with no Chances block at all is simply absent");
        builder.AppendLine("    // here - see Cost's own doc comment.");
        builder.AppendLine("    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<int, IReadOnlyDictionary<string, (uint Rate, uint Price, int MaterialItemId, uint BreakingRate, uint DowngradeAmount)>>>> CostTable =");
        builder.AppendLine("        new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<int, IReadOnlyDictionary<string, (uint, uint, int, uint, uint)>>>>");
        builder.AppendLine("    {");
        foreach (var (group, levels) in table.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("        [\"").Append(group).AppendLine("\"] = new Dictionary<int, IReadOnlyDictionary<int, IReadOnlyDictionary<string, (uint, uint, int, uint, uint)>>>");
            builder.AppendLine("        {");
            foreach (var (level, refineLevels) in levels.OrderBy(pair => pair.Key))
            {
                builder.Append("            [").Append(level).AppendLine("] = new Dictionary<int, IReadOnlyDictionary<string, (uint, uint, int, uint, uint)>>");
                builder.AppendLine("            {");
                foreach (var (refineLevel, value) in refineLevels.OrderBy(pair => pair.Key))
                {
                    if (value.Costs is null) continue;
                    builder.Append("                [").Append(refineLevel).AppendLine("] = new Dictionary<string, (uint, uint, int, uint, uint)>");
                    builder.AppendLine("                {");
                    foreach (var (costType, cost) in value.Costs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                        builder.Append("                    [\"").Append(costType).Append("\"] = (").Append(cost.Rate).Append(", ").Append(cost.Price).Append(", ")
                            .Append(cost.MaterialItemId).Append(", ").Append(cost.BreakingRate).Append(", ").Append(cost.DowngradeAmount).AppendLine("),");
                    builder.AppendLine("                },");
                }
                builder.AppendLine("            },");
            }
            builder.AppendLine("        },");
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    // Pinned RefineDatabase::findCurrentLevelInfo (status.cpp:457-463): refine == 0 has no bonus at");
        builder.AppendLine("    // all (never looked up); refine > 0 reads the (refine-1)-th 0-based RefineLevels entry, i.e. the");
        builder.AppendLine("    // pinned YAML's own 1-based `Level: refine` entry directly - no further offset here. A miss on");
        builder.AppendLine("    // itemLevel genuinely means \"no bonus\", matching pinned util::umap_find's own null return for an");
        builder.AppendLine("    // item level this Group's data never tabulates (e.g. an item_db WeaponLevel/ArmorLevel value this");
        builder.AppendLine("    // pinned refine.yml snapshot has no Levels entry for) - NOT a generation gap to fail loudly on,");
        builder.AppendLine("    // since the pinned source itself returns nullptr for exactly this case rather than treating it as");
        builder.AppendLine("    // an error. A miss on refineLevel cannot occur for any of this table's tabulated item levels (every");
        builder.AppendLine("    // one covers refine 1-MAX_REFINE(20) completely - see RefineDataCompilerTests' own coverage check).");
        builder.AppendLine("    internal static uint Bonus(string group, int itemLevel, int refine)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (refine <= 0) return 0;");
        builder.AppendLine("        return Table.TryGetValue(group, out var levels) && levels.TryGetValue(itemLevel, out var refineLevels) && refineLevels.TryGetValue(refine, out var value) ? value.Bonus : 0;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    // wd->overrefine = info->randombonus_max / 100 (status.cpp:3986) - a deterministic per-(group,level,refine)");
        builder.AppendLine("    // maximum, NOT an RNG roll at refine time despite the pinned YAML field's name (see RefineDataCompiler's");
        builder.AppendLine("    // own doc comment on RefineLevelBonus). Consumed by the live player Renewal MATK formula as an extra,");
        builder.AppendLine("    // asymmetric matk_min/matk_max widening (status.cpp:6325-6327): matk_min++, matk_max += overrefine-1,");
        builder.AppendLine("    // applied only when this value is > 0.");
        builder.AppendLine("    internal static uint OverrefineBonus(string group, int itemLevel, int refine)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (refine <= 0) return 0;");
        builder.AppendLine("        return Table.TryGetValue(group, out var levels) && levels.TryGetValue(itemLevel, out var refineLevels) && refineLevels.TryGetValue(refine, out var value) ? value.RandomBonusMax : 0;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    // Pinned s_refine_cost (status.hpp:92-99), the Chances[] entry for one (group, item level, refine level,");
        builder.AppendLine("    // cost type) the player is ATTEMPTING to refine TO (clif_parse_refineui_refine reads refine_db.findLevelInfo");
        builder.AppendLine("    // for the item's CURRENT refine+1 level, clif.cpp:22688-22693) - null when the item cannot be refined");
        builder.AppendLine("    // further at this level/cost-type combination (no Chances entry, or no RefineLevels entry at all for");
        builder.AppendLine("    // refine+1, matching pinned findLevelInfo's own null return for \"no further refine possible\").");
        builder.AppendLine("    internal static (uint Rate, uint Price, int MaterialItemId, uint BreakingRate, uint DowngradeAmount)? Cost(string group, int itemLevel, int refine, string costType) =>");
        builder.AppendLine("        CostTable.TryGetValue(group, out var levels) && levels.TryGetValue(itemLevel, out var refineLevels) && refineLevels.TryGetValue(refine, out var costs) && costs.TryGetValue(costType, out var cost) ? cost : null;");
        return builder.AppendLine("}").ToString();
    }
}
