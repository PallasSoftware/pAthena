using System.Globalization;
using Athena.Net.MapServer.World.Items;

namespace Athena.WorldCompiler.Generation;

// Port of the pinned ItemRandomOptionDatabase::parseBodyNode (itemdb.cpp, s_random_opt_data) for db/re/item_randomopt_db.yml:
// each row is Id (the client-matching option id from enumvar.lub) + Option (a constant name kept for provenance/diagnostics
// only) + Script (the bonus statement(s) run through getrandomoptinfo(), lowered exactly like an item's own equip script -
// same RathenaLexer/Parser -> Semantics -> ItemScriptLowerer pipeline, no second script path). A row the pinned loader
// would skip (missing Id/Script) is a hard error here, never silently dropped.
internal static class RandomOptionLoader
{
    internal static IReadOnlyList<PackRandomOption> Load(Func<string, string> readFile, Func<string, string, int> index, string path = "db/re/item_randomopt_db.yml")
    {
        var root = (YamlMap)RathenaYaml.Parse(readFile(path), path);
        var body = root.Get("Body") as YamlSequence ?? throw new InvalidDataException($"{path} has no Body sequence.");
        var options = new List<PackRandomOption>();

        foreach (var node in body.Items.OfType<YamlMap>())
        {
            var location = $"{path}:{node.Line}";
            var idText = node.Scalar("Id") ?? throw new InvalidDataException($"{location}: random option row has no Id.");
            var id = uint.Parse(idText, CultureInfo.InvariantCulture);
            var option = node.Scalar("Option") ?? throw new InvalidDataException($"{location}: random option row {id} has no Option.");
            var script = node.Scalar("Script") ?? throw new InvalidDataException($"{location}: random option row {id} has no Script.");
            options.Add(new PackRandomOption(id, option, index(script, path)));
        }

        return options;
    }
}
