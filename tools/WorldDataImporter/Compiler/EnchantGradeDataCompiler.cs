using System.Text;
namespace Athena.WorldCompiler.Generation;

// Pinned EnchantgradeDatabase (db/re/enchantgrade.yml, status.cpp:961-985). Each (Type, Level, Grade) carries the
// flat Bonus the refine-bonus formula consumes once an item carries a grade (status.cpp:3961,3980), PLUS the
// grade-UPGRADE action's own data (clif_parse_enchantgrade_start, clif.cpp:24059-24243): Chances (success roll per
// CURRENT refine level), Catalyst (an optional item that raises the chance, stepped up to MaximumSteps), and
// Options (per-option-index cost: required item/amount/zeny/breaking/downgrade - parallel to RefineDataCompiler's
// own per-CostType Chances[], just indexed by a numeric Option instead of a named CostType).
//
// Only Weapon and Armor are modeled, matching RefineDataCompiler's own scope; the real pinned data itself
// only tabulates Weapon Level 5 and Armor Level 2 (the top tier each - enchant grade is an endgame-only
// mechanic), so this compiler's own "no entry for this (type,level)" IS the correct behavior for every
// other weapon/armor level, not a generation gap.
internal static class EnchantGradeDataCompiler
{
    private static readonly string[] Types = ["Weapon", "Armor"];

    // Pinned e_enchantgrade (mmo.hpp:124-130): None=0, D=1, C=2, B=3, A=4. The real pinned data's own Grade:
    // string spelling, used as the generated table's key directly (not re-encoded to the numeric enum, which
    // only matters to the pinned C++ std::map's own storage, not to this lookup's contract).
    internal static readonly string[] Grades = ["None", "D", "C", "B", "A"];

    internal readonly record struct Catalyst(int MaterialItemId, uint AmountPerStep, uint MaximumSteps, uint ChanceIncrease);
    internal readonly record struct Option(int MaterialItemId, uint Amount, uint Zeny, uint BreakingRate, uint DowngradeAmount);

    internal readonly record struct GradeLevel(
        uint Bonus, IReadOnlyDictionary<int, uint> ChancesByRefine, Catalyst? Catalyst, IReadOnlyDictionary<int, Option> Options);

    // [type][level][grade] -> GradeLevel. level is the pinned YAML's own 1-based WeaponLevel/ArmorLevel numbering
    // (matching RefineDataCompiler's own convention); grade is one of Grades above. A (type,level) absent from
    // the real data (every level except Weapon 5 / Armor 2) legitimately has no entry at all - callers must
    // not treat a miss here as an error, matching pinned EnchantgradeDatabase::findCurrentLevelInfo's own
    // nullptr return for "cannot upgrade this weapon or armor level" (status.cpp:979-982).
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, GradeLevel>>> BuildTable(
        Func<string, string> readFile, Func<string, int> resolveMaterialItemId, string basePath = "db/re/enchantgrade.yml")
    {
        var yaml = readFile(basePath);
        var root = SimpleYaml.Parse(yaml, basePath);
        var body = root.Required("Body", basePath).Sequence($"{basePath} Body");

        var table = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, GradeLevel>>>(StringComparer.Ordinal);
        foreach (var (node, typeIndex) in body.Items.Select((n, i) => (n, i)))
        {
            var typeEntry = node.Map($"{basePath} Body[{typeIndex}]");
            var typeName = typeEntry.Required("Type", $"{basePath} Body[{typeIndex}]").Scalar("Type");
            if (!Types.Contains(typeName, StringComparer.Ordinal)) continue; // only Weapon/Armor are modeled.

            var levels = new Dictionary<int, IReadOnlyDictionary<string, GradeLevel>>();
            foreach (var levelNode in typeEntry.Required("Levels", typeName).Sequence($"{typeName} Levels").Items)
            {
                var levelEntry = levelNode.Map($"{typeName} Levels[]");
                var level = (int)levelEntry.Required("Level", typeName).UShort($"{typeName} Level");
                var context = $"{typeName} Level {level}";

                var grades = new Dictionary<string, GradeLevel>(StringComparer.Ordinal);
                foreach (var gradeNode in levelEntry.Required("Grades", context).Sequence($"{context} Grades").Items)
                {
                    var gradeEntry = gradeNode.Map($"{context} Grades[]");
                    var gradeName = gradeEntry.Required("Grade", context).Scalar($"{context} Grades[].Grade");
                    if (!Grades.Contains(gradeName, StringComparer.Ordinal))
                        throw new ArgumentException($"{context} Grades[].Grade '{gradeName}' is not one of {string.Join('/', Grades)}.");
                    var gradeContext = $"{context} Grade {gradeName}";
                    var bonus = gradeEntry.Optional("Bonus") is { } bonusNode ? (uint)bonusNode.UShort($"{gradeContext} Bonus") : 0u;

                    var chances = new Dictionary<int, uint>();
                    if (gradeEntry.Optional("Chances") is { } chancesNode)
                        foreach (var chanceNode in chancesNode.Sequence($"{gradeContext} Chances").Items)
                        {
                            var chanceEntry = chanceNode.Map($"{gradeContext} Chances[]");
                            var refine = (int)chanceEntry.Required("Refine", gradeContext).UShort($"{gradeContext} Chances[].Refine");
                            chances[refine] = (uint)chanceEntry.Required("Chance", gradeContext).UShort($"{gradeContext} Chances[].Chance");
                        }

                    Catalyst? catalyst = null;
                    if (gradeEntry.Optional("Catalyst") is { } catalystNode)
                    {
                        var catalystEntry = catalystNode.Map($"{gradeContext} Catalyst");
                        var itemName = catalystEntry.Required("Item", gradeContext).Scalar($"{gradeContext} Catalyst.Item");
                        catalyst = new Catalyst(
                            resolveMaterialItemId(itemName),
                            (uint)catalystEntry.Required("AmountPerStep", gradeContext).UShort($"{gradeContext} Catalyst.AmountPerStep"),
                            (uint)catalystEntry.Required("MaximumSteps", gradeContext).UShort($"{gradeContext} Catalyst.MaximumSteps"),
                            (uint)catalystEntry.Required("ChanceIncrease", gradeContext).UShort($"{gradeContext} Catalyst.ChanceIncrease"));
                    }

                    var options = new Dictionary<int, Option>();
                    if (gradeEntry.Optional("Options") is { } optionsNode)
                        foreach (var optionNode in optionsNode.Sequence($"{gradeContext} Options").Items)
                        {
                            var optionEntry = optionNode.Map($"{gradeContext} Options[]");
                            var optionIndex = (int)optionEntry.Required("Option", gradeContext).UShort($"{gradeContext} Options[].Option");
                            var itemName = optionEntry.Required("Item", gradeContext).Scalar($"{gradeContext} Options[].Item");
                            var amount = optionEntry.Optional("Amount") is { } amountNode ? (uint)amountNode.UShort($"{gradeContext} Options[].Amount") : 1u;
                            var zeny = optionEntry.Optional("Zeny") is { } zenyNode ? (uint)zenyNode.ULong($"{gradeContext} Options[].Zeny") : 0u;
                            var breakingRate = optionEntry.Optional("BreakingRate") is { } breakingNode ? (uint)breakingNode.UShort($"{gradeContext} Options[].BreakingRate") : 0u;
                            var downgradeAmount = optionEntry.Optional("DowngradeAmount") is { } downgradeNode ? (uint)downgradeNode.UShort($"{gradeContext} Options[].DowngradeAmount") : 0u;
                            options[optionIndex] = new Option(resolveMaterialItemId(itemName), amount, zeny, breakingRate, downgradeAmount);
                        }

                    grades[gradeName] = new GradeLevel(bonus, chances, catalyst, options);
                }
                levels[level] = grades;
            }
            table[typeName] = levels;
        }

        foreach (var type in Types)
            if (!table.ContainsKey(type)) throw new ArgumentException($"{basePath} has no Type entry for '{type}'.");
        return table;
    }

    internal static string Generate(IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, GradeLevel>>> table, string commit)
    {
        var builder = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .AppendLine("// Source: legacy/rathena/db/re/enchantgrade.yml (Weapon/Armor types only - see EnchantGradeDataCompiler's own doc comment)")
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Refine;")
            .AppendLine()
            .AppendLine("internal static class GeneratedEnchantGradeBonus")
            .AppendLine("{")
            .AppendLine("    // Pinned e_enchantgrade (mmo.hpp:124-130): None=0, D=1, C=2, B=3, A=4 - this project's own")
            .AppendLine("    // CharacterInventoryItem.EnchantGrade byte already stores this 0-indexed value directly (see")
            .AppendLine("    // ItemGrantResolver's MaxEnchantGrade=4 cap), so a caller indexes this array with that byte")
            .AppendLine("    // directly - never the pinned WIRE byte's own 1-indexed scheme (which exists only because that")
            .AppendLine("    // byte uses 0 to mean \"never graded\", a distinction this domain model does not carry separately).")
            .AppendLine("    internal static readonly string[] Grades = [\"None\", \"D\", \"C\", \"B\", \"A\"];")
            .AppendLine()
            .AppendLine("    internal readonly record struct Catalyst(int MaterialItemId, uint AmountPerStep, uint MaximumSteps, uint ChanceIncrease);")
            .AppendLine("    internal readonly record struct Option(int MaterialItemId, uint Amount, uint Zeny, uint BreakingRate, uint DowngradeAmount);")
            .AppendLine()
            .AppendLine("    // [type][level][grade] -> Bonus (pinned s_enchantgradelevel.bonus). level is the pinned YAML's own")
            .AppendLine("    // 1-based WeaponLevel/ArmorLevel numbering; grade is the pinned Grade string (\"None\"/\"D\"/\"C\"/\"B\"/\"A\").")
            .AppendLine("    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, uint>>> Table = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, uint>>>")
            .AppendLine("    {");
        foreach (var (type, levels) in table.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("        [\"").Append(type).AppendLine("\"] = new Dictionary<int, IReadOnlyDictionary<string, uint>>");
            builder.AppendLine("        {");
            foreach (var (level, grades) in levels.OrderBy(pair => pair.Key))
            {
                builder.Append("            [").Append(level).AppendLine("] = new Dictionary<string, uint>");
                builder.AppendLine("            {");
                foreach (var (grade, info) in grades.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    builder.Append("                [\"").Append(grade).Append("\"] = ").Append(info.Bonus).AppendLine(",");
                builder.AppendLine("            },");
            }
            builder.AppendLine("        },");
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    // Pinned s_enchantgradelevel.chances[refine] (the grade-UPGRADE success roll, clif.cpp:24104):");
        builder.AppendLine("    // [type][level][grade][refine] -> chance out of 0..10000. The pinned chances array is indexed by the");
        builder.AppendLine("    // ITEM'S CURRENT refine level at the moment of the upgrade attempt, not the target grade's own level.");
        builder.AppendLine("    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyDictionary<int, uint>>>> ChancesTable = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyDictionary<int, uint>>>>")
            .AppendLine("    {");
        foreach (var (type, levels) in table.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("        [\"").Append(type).AppendLine("\"] = new Dictionary<int, IReadOnlyDictionary<string, IReadOnlyDictionary<int, uint>>>");
            builder.AppendLine("        {");
            foreach (var (level, grades) in levels.OrderBy(pair => pair.Key))
            {
                builder.Append("            [").Append(level).AppendLine("] = new Dictionary<string, IReadOnlyDictionary<int, uint>>");
                builder.AppendLine("            {");
                foreach (var (grade, info) in grades.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    builder.Append("                [\"").Append(grade).AppendLine("\"] = new Dictionary<int, uint>");
                    builder.AppendLine("                {");
                    foreach (var (refine, chance) in info.ChancesByRefine.OrderBy(pair => pair.Key))
                        builder.Append("                    [").Append(refine).Append("] = ").Append(chance).AppendLine(",");
                    builder.AppendLine("                },");
                }
                builder.AppendLine("            },");
            }
            builder.AppendLine("        },");
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    // Pinned s_enchantgradelevel.catalyst (clif.cpp:24123-24172) - null when the real data has no Catalyst block.");
        builder.AppendLine("    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, Catalyst?>>> CatalystTable = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, Catalyst?>>>")
            .AppendLine("    {");
        foreach (var (type, levels) in table.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("        [\"").Append(type).AppendLine("\"] = new Dictionary<int, IReadOnlyDictionary<string, Catalyst?>>");
            builder.AppendLine("        {");
            foreach (var (level, grades) in levels.OrderBy(pair => pair.Key))
            {
                builder.Append("            [").Append(level).AppendLine("] = new Dictionary<string, Catalyst?>");
                builder.AppendLine("            {");
                foreach (var (grade, info) in grades.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var c = info.Catalyst;
                    builder.Append("                [\"").Append(grade).Append("\"] = ")
                        .Append(c is null ? "null" : $"new Catalyst({c.Value.MaterialItemId}, {c.Value.AmountPerStep}, {c.Value.MaximumSteps}, {c.Value.ChanceIncrease})")
                        .AppendLine(",");
                }
                builder.AppendLine("            },");
            }
            builder.AppendLine("        },");
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    // Pinned s_enchantgradelevel.options[optionIndex] (clif.cpp:24111-24186) - keyed by the pinned client");
        builder.AppendLine("    // Option index (clif_parse_enchantgrade_start's own p->material_index), not a named CostType like refine's.");
        builder.AppendLine("    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyDictionary<int, Option>>>> OptionsTable = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyDictionary<int, Option>>>>")
            .AppendLine("    {");
        foreach (var (type, levels) in table.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("        [\"").Append(type).AppendLine("\"] = new Dictionary<int, IReadOnlyDictionary<string, IReadOnlyDictionary<int, Option>>>");
            builder.AppendLine("        {");
            foreach (var (level, grades) in levels.OrderBy(pair => pair.Key))
            {
                builder.Append("            [").Append(level).AppendLine("] = new Dictionary<string, IReadOnlyDictionary<int, Option>>");
                builder.AppendLine("            {");
                foreach (var (grade, info) in grades.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    builder.Append("                [\"").Append(grade).AppendLine("\"] = new Dictionary<int, Option>");
                    builder.AppendLine("                {");
                    foreach (var (optionIndex, option) in info.Options.OrderBy(pair => pair.Key))
                        builder.Append("                    [").Append(optionIndex).Append("] = new Option(")
                            .Append(option.MaterialItemId).Append(", ").Append(option.Amount).Append(", ").Append(option.Zeny).Append(", ")
                            .Append(option.BreakingRate).Append(", ").Append(option.DowngradeAmount).AppendLine("),");
                    builder.AppendLine("                },");
                }
                builder.AppendLine("            },");
            }
            builder.AppendLine("        },");
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    // Pinned EnchantgradeDatabase::findCurrentLevelInfo (status.cpp:961-985): a (type,level) this table");
        builder.AppendLine("    // has no entry for genuinely means \"cannot grade-upgrade this weapon/armor level\" (nullptr in the");
        builder.AppendLine("    // pinned source) - NOT a generation gap. The pinned lookup also only runs at all when the item's");
        builder.AppendLine("    // persisted enchantgrade byte is > 0 (status.cpp:3935); grade here is already the resolved string");
        builder.AppendLine("    // (\"None\"/\"D\"/\"C\"/\"B\"/\"A\"), the caller's own responsibility to map from that persisted 1-based byte.");
        builder.AppendLine("    internal static uint Bonus(string type, int itemLevel, string grade)");
        builder.AppendLine("    {");
        builder.AppendLine("        return Table.TryGetValue(type, out var levels) && levels.TryGetValue(itemLevel, out var grades) && grades.TryGetValue(grade, out var bonus) ? bonus : 0;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    // currentRefine is the item's OWN persisted refine level (clif.cpp:24104's own current-item index) - the");
        builder.AppendLine("    // chance a grade-upgrade attempt from this (type,level,grade) succeeds BEFORE any catalyst bonus. 0 (no");
        builder.AppendLine("    // tabulated chance at this refine level) matches the pinned \"No chance to increase\" early return.");
        builder.AppendLine("    internal static uint ChanceAtRefine(string type, int itemLevel, string grade, int currentRefine)");
        builder.AppendLine("    {");
        builder.AppendLine("        return ChancesTable.TryGetValue(type, out var levels) && levels.TryGetValue(itemLevel, out var grades)");
        builder.AppendLine("            && grades.TryGetValue(grade, out var chances) && chances.TryGetValue(currentRefine, out var chance) ? chance : 0;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    internal static Catalyst? CatalystFor(string type, int itemLevel, string grade)");
        builder.AppendLine("    {");
        builder.AppendLine("        return CatalystTable.TryGetValue(type, out var levels) && levels.TryGetValue(itemLevel, out var grades)");
        builder.AppendLine("            && grades.TryGetValue(grade, out var catalyst) ? catalyst : null;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    internal static Option? OptionCost(string type, int itemLevel, string grade, int optionIndex)");
        builder.AppendLine("    {");
        builder.AppendLine("        return OptionsTable.TryGetValue(type, out var levels) && levels.TryGetValue(itemLevel, out var grades)");
        builder.AppendLine("            && grades.TryGetValue(grade, out var options) && options.TryGetValue(optionIndex, out var option) ? option : null;");
        builder.AppendLine("    }");
        return builder.AppendLine("}").ToString();
    }
}
