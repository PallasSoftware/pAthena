using System.Text;
namespace Athena.WorldCompiler.Generation;

// Pinned EnchantgradeDatabase (db/re/enchantgrade.yml, status.cpp:961-985). Scoped to the ONE field this
// project's weapon/armor refine-bonus formula actually consumes once an item carries a grade: each
// (Type, Level, Grade)'s flat Bonus - the multiplier numerator applied on top of the already-scaled refine
// bonus (`wa->atk2 += ((info->bonus/100) * enchantgrade_info->bonus) / 100`, status.cpp:3961,3980). Chances
// (the grade-UPGRADE success roll per refine level), Catalyst and Options (the grade upgrade action's own
// cost/break/downgrade data, parallel to RefineDataCompiler's own Chances[]) are NOT modeled here - that is
// its own separate mutation (grade upgrade, not refine), needing its own primitive and NPC-script surface
// (npc/re/merchants/enchantgrade.txt), not a silent gap: there is no mutation primitive yet to apply them to.
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

    // [type][level][grade] -> Bonus. level is the pinned YAML's own 1-based WeaponLevel/ArmorLevel numbering
    // (matching RefineDataCompiler's own convention); grade is one of Grades above. A (type,level) absent from
    // the real data (every level except Weapon 5 / Armor 2) legitimately has no entry at all - callers must
    // not treat a miss here as an error, matching pinned EnchantgradeDatabase::findCurrentLevelInfo's own
    // nullptr return for "cannot upgrade this weapon or armor level" (status.cpp:979-982).
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, uint>>> BuildTable(Func<string, string> readFile, string basePath = "db/re/enchantgrade.yml")
    {
        var yaml = readFile(basePath);
        var root = SimpleYaml.Parse(yaml, basePath);
        var body = root.Required("Body", basePath).Sequence($"{basePath} Body");

        var table = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, uint>>>(StringComparer.Ordinal);
        foreach (var (node, typeIndex) in body.Items.Select((n, i) => (n, i)))
        {
            var typeEntry = node.Map($"{basePath} Body[{typeIndex}]");
            var typeName = typeEntry.Required("Type", $"{basePath} Body[{typeIndex}]").Scalar("Type");
            if (!Types.Contains(typeName, StringComparer.Ordinal)) continue; // only Weapon/Armor are modeled.

            var levels = new Dictionary<int, IReadOnlyDictionary<string, uint>>();
            foreach (var levelNode in typeEntry.Required("Levels", typeName).Sequence($"{typeName} Levels").Items)
            {
                var levelEntry = levelNode.Map($"{typeName} Levels[]");
                var level = (int)levelEntry.Required("Level", typeName).UShort($"{typeName} Level");
                var context = $"{typeName} Level {level}";

                var grades = new Dictionary<string, uint>(StringComparer.Ordinal);
                foreach (var gradeNode in levelEntry.Required("Grades", context).Sequence($"{context} Grades").Items)
                {
                    var gradeEntry = gradeNode.Map($"{context} Grades[]");
                    var gradeName = gradeEntry.Required("Grade", context).Scalar($"{context} Grades[].Grade");
                    if (!Grades.Contains(gradeName, StringComparer.Ordinal))
                        throw new ArgumentException($"{context} Grades[].Grade '{gradeName}' is not one of {string.Join('/', Grades)}.");
                    var bonus = gradeEntry.Optional("Bonus") is { } bonusNode ? (uint)bonusNode.UShort($"{context} Grade {gradeName} Bonus") : 0u;
                    grades[gradeName] = bonus;
                }
                levels[level] = grades;
            }
            table[typeName] = levels;
        }

        foreach (var type in Types)
            if (!table.ContainsKey(type)) throw new ArgumentException($"{basePath} has no Type entry for '{type}'.");
        return table;
    }

    internal static string Generate(IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, uint>>> table, string commit)
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
                foreach (var (grade, bonus) in grades.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    builder.Append("                [\"").Append(grade).Append("\"] = ").Append(bonus).AppendLine(",");
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
        return builder.AppendLine("}").ToString();
    }
}
