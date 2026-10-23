using Athena.Net.MapServer.World.Items;

namespace Athena.WorldCompiler.Generation;

// Port of the pinned ItemDBCombo::parseBodyNode (itemdb.cpp, s_item_combo_db) for db/re/item_combos.yml: each
// top-level Body entry carries one or more alternative Combo: item-id sets (any ONE of which activates the
// SAME shared Script) plus that Script. A row the pinned loader would skip (missing Combos/Script, or an
// AegisName that resolves to no real item) is a hard error here, never silently dropped - matching
// RefineDataCompiler's own existing AegisName-resolution convention (the real pinned data has no orphan rows).
internal static class ComboLoader
{
    internal static IReadOnlyList<PackCombo> Load(
        Func<string, string> readFile, Func<string, int> resolveItemId, Func<string, string, int> index, string path = "db/re/item_combos.yml")
    {
        var root = (YamlMap)RathenaYaml.Parse(readFile(path), path);
        var body = root.Get("Body") as YamlSequence ?? throw new InvalidDataException($"{path} has no Body sequence.");
        var combos = new List<PackCombo>();

        foreach (var node in body.Items.OfType<YamlMap>())
        {
            var location = $"{path}:{node.Line}";
            if (node.Get("Combos") is not YamlSequence comboNodes) throw new InvalidDataException($"{location}: combo row has no Combos.");
            var script = node.Scalar("Script") ?? throw new InvalidDataException($"{location}: combo row has no Script.");

            var alternatives = new List<IReadOnlyList<uint>>();
            foreach (var comboNode in comboNodes.Items.OfType<YamlMap>())
            {
                if (comboNode.Get("Combo") is not YamlSequence itemNodes) throw new InvalidDataException($"{location}: Combos entry has no Combo list.");
                var itemIds = itemNodes.Items.OfType<YamlScalar>()
                    .Select(scalar => checked((uint)resolveItemId(scalar.Value)))
                    .ToArray();
                if (itemIds.Length < 2) throw new InvalidDataException($"{location}: a combo needs at least 2 items, got {itemIds.Length}.");
                alternatives.Add(itemIds);
            }
            if (alternatives.Count == 0) throw new InvalidDataException($"{location}: combo row's Combos list is empty.");

            combos.Add(new PackCombo(alternatives, index(script, path)));
        }

        return combos;
    }
}
