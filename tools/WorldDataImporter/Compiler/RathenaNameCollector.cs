using System.Text.RegularExpressions;
using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Rathena.Syntax;

namespace Athena.WorldCompiler.Generation;

// Lists every identifier the item-family databases can ask the pinned script engine to resolve, so the REAL rAthena
// (tools/rathena-reference/run-data.ps1) can answer them once and the answers be committed as rathena-constants.tsv:
//   - every name the item loader looks up (IT_/W_/EAJ_/EQP_/SC_... + key),
//   - every identifier token in any *Script block of the item-family YAML databases,
//   - every name exported by src/map/script_constants.hpp (so later stages never meet an unresolved constant).
internal static partial class RathenaNameCollector
{
    internal static readonly string[] ScriptBearingFiles =
    [
        "db/re/item_db_usable.yml", "db/re/item_db_equip.yml", "db/re/item_db_etc.yml", "db/re/item_combos.yml",
        "db/re/item_randomopt_db.yml", "db/re/item_enchant.yml", "db/re/item_reform.yml", "db/re/laphine_synthesis.yml", "db/re/laphine_upgrade.yml",
    ];

    internal static IReadOnlyList<string> Collect(Func<string, string> readFile)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in ItemDatabaseLoader.ImportOrder)
            foreach (var node in ((YamlSequence)((YamlMap)RathenaYaml.Parse(readFile(file), file)).Get("Body")!).Items.OfType<YamlMap>())
                AddLoaderNames(node, names);
        foreach (var name in new[] { "EQP_Left_Hand", "EQP_Shadow_Armor", "EQP_Shadow_Weapon", "EQP_Shadow_Shield", "EQP_Shadow_Shoes", "EQP_Shadow_Right_Accessory", "EQP_Shadow_Left_Accessory" })
            names.Add(name);

        foreach (var file in ScriptBearingFiles)
            foreach (var script in Scripts(RathenaYaml.Parse(readFile(file), file)))
                foreach (var token in new RathenaLexer(script, file).Lex())
                    if (token.Kind == TokenKind.Identifier) names.Add(token.Text);

        foreach (Match match in ExportedName().Matches(readFile("src/map/script_constants.hpp")))
            names.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);

        return [.. names];
    }

    // The prefixed names ItemDatabaseLoader.ParseNode resolves for one item row.
    private static void AddLoaderNames(YamlMap node, SortedSet<string> names)
    {
        if (node.Scalar("Type") is { } type) names.Add("IT_" + type);
        if (node.Scalar("SubType") is { } subType) { names.Add("W_" + subType); names.Add("AMMO_" + subType); names.Add("CARD_" + subType); }
        if (node.Get("Jobs") is YamlMap jobs) foreach (var job in jobs.Entries) names.Add("EAJ_" + job.Key);
        if (node.Get("Classes") is YamlMap classes) foreach (var entry in classes.Entries) names.Add("ITEMJ_" + entry.Key);
        if (node.Scalar("Gender") is { } gender) names.Add("SEX_" + gender);
        if (node.Get("Locations") is YamlMap locations) foreach (var entry in locations.Entries) names.Add("EQP_" + entry.Key);
        if (node.Get("Flags") is YamlMap flags && flags.Scalar("DropEffect") is { } effect) names.Add("DROPEFFECT_" + effect);
        if (node.Get("Delay") is YamlMap delay && delay.Scalar("Status") is { } status) names.Add("SC_" + status);
    }

    private static IEnumerable<string> Scripts(YamlNode node)
    {
        switch (node)
        {
            case YamlMap map:
                foreach (var entry in map.Entries)
                {
                    if (entry.Key.EndsWith("Script", StringComparison.Ordinal) && entry.Value is YamlScalar script) yield return script.Value;
                    else foreach (var nested in Scripts(entry.Value)) yield return nested;
                }
                break;
            case YamlSequence sequence:
                foreach (var item in sequence.Items)
                    foreach (var nested in Scripts(item)) yield return nested;
                break;
        }
    }

    // export_constant(NAME), export_constant2("Name", VALUE), export_parameter("Name", ID) and the deprecated variants.
    [GeneratedRegex(@"export_(?:deprecated_)?(?:constant|parameter)\d?\(\s*(?:([A-Za-z_][A-Za-z0-9_]*)\s*\)|""([^""]+)"")")]
    private static partial Regex ExportedName();
}
