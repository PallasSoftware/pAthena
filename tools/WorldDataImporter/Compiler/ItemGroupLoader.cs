using System.Globalization;
using Athena.Net.MapServer.World.Items;

namespace Athena.WorldCompiler.Generation;

// Port of the pinned ItemGroupDatabase::parseBodyNode / loadingFinished (itemdb.cpp:3365-3764, Renewal) for db/re/item_group_db.yml:
// group ids come from the IG_<name> script constants, sub groups keep their algorithm (default SharedPool), entries are keyed by Index
// and a repeated Index updates only the fields it names. Empty sub groups are dropped and each sub group's TotalRate is the sum of its
// entry rates. A row the pinned loader would skip is a hard error here (the pinned data has none), never silently dropped.
internal static class ItemGroupLoader
{
    private const uint MaxAmount = 30_000;
    private const uint MaxRefine = 20;
    private const byte MaxEnchantGrade = 4;     // ENCHANTGRADE_A

    private sealed class Entry
    {
        public uint ItemId, Rate, Amount = 1, Duration, RefineMinimum, RefineMaximum;
        public bool Announced, Guid, Stacked = true, Named;
        public byte Bound, GradeMinimum, GradeMaximum;
        public string? RandomOptionGroup;
    }

    private sealed class SubGroup { public byte Algorithm = PackGroupAlgorithm.SharedPool; public SortedDictionary<uint, Entry> Entries = []; }

    private sealed class Group(uint id, string name) { public uint Id { get; } = id; public string Name { get; } = name; public SortedDictionary<uint, SubGroup> SubGroups = []; }

    internal static IReadOnlyList<PackItemGroup> Load(
        Func<string, string> readFile, RathenaConstantTable constants, IReadOnlyDictionary<string, PackItem> itemsByAegisName, string path = "db/re/item_group_db.yml")
    {
        var root = (YamlMap)RathenaYaml.Parse(readFile(path), path);
        var body = root.Get("Body") as YamlSequence ?? throw new InvalidDataException($"{path} has no Body sequence.");
        var groups = new Dictionary<uint, Group>();

        foreach (var node in body.Items.OfType<YamlMap>())
        {
            var location = $"{path}:{node.Line}";
            var name = node.Scalar("Group") ?? throw new InvalidDataException($"{location}: group row has no Group.");
            if (!constants.TryGetConstant("IG_" + name, out var idValue)) throw new InvalidDataException($"{location}: unknown item group '{name}' (the IG_ prefix is added automatically).");
            var id = checked((uint)idValue);
            if (!groups.TryGetValue(id, out var group)) groups[id] = group = new Group(id, name);

            if (node.Get("SubGroups") is not YamlSequence subNodes) continue;
            foreach (var sub in subNodes.Items.OfType<YamlMap>())
            {
                if (sub.Scalar("Clear") is { } clear)
                {
                    if (!group.SubGroups.Remove(uint.Parse(clear, CultureInfo.InvariantCulture))) throw new InvalidDataException($"{location}: Clear of missing sub group {clear}.");
                    continue;
                }

                var subNumber = uint.Parse(sub.Scalar("SubGroup") ?? throw new InvalidDataException($"{location}: sub group row has no SubGroup."), CultureInfo.InvariantCulture);
                if (sub.Get("List") is not YamlSequence list) throw new InvalidDataException($"{location}: sub group {subNumber} has no List.");
                group.SubGroups.TryGetValue(subNumber, out var current);
                var target = current ?? (group.SubGroups[subNumber] = new SubGroup());

                if (sub.Scalar("Algorithm") is { } algorithm)
                {
                    if (!constants.TryGetConstant("GROUP_ALGORITHM_" + algorithm, out var algorithmValue)) throw new InvalidDataException($"{location}: invalid algorithm {algorithm}.");
                    target.Algorithm = (byte)algorithmValue;
                }

                foreach (var row in list.Items.OfType<YamlMap>())
                    ApplyEntry(row, target, group, location, constants, itemsByAegisName);
            }
        }

        return [.. groups.Values.OrderBy(group => group.Id).Select(group => new PackItemGroup(group.Id, group.Name,
            [.. group.SubGroups.Where(pair => pair.Value.Entries.Count > 0).Select(pair => new PackSubGroup(pair.Key, pair.Value.Algorithm, (uint)pair.Value.Entries.Values.Sum(entry => (long)entry.Rate),
                [.. pair.Value.Entries.Select(entry => ToPack(entry.Key, entry.Value))]))]))];
    }

    private static void ApplyEntry(YamlMap row, SubGroup target, Group group, string location, RathenaConstantTable constants, IReadOnlyDictionary<string, PackItem> items)
    {
        var index = uint.Parse(row.Scalar("Index") ?? throw new InvalidDataException($"{location}: list row has no Index."), CultureInfo.InvariantCulture);
        if (row.Scalar("Clear") is { } clear)
        {
            if (Bool(clear, location) && !target.Entries.Remove(index)) throw new InvalidDataException($"{location}: Clear of missing index {index}.");
            return;
        }

        var exists = target.Entries.TryGetValue(index, out var entry);
        if (entry is null)
        {
            if (row.Scalar("Item") is null) throw new InvalidDataException($"{location}: new list entry {index} has no Item.");
            entry = target.Entries[index] = new Entry();
        }

        if (row.Scalar("Item") is { } itemName)
        {
            if (!items.TryGetValue(itemName.ToLowerInvariant(), out var item)) throw new InvalidDataException($"{location}: unknown item '{itemName}' in group {group.Name}.");
            entry.ItemId = item.Id;
            if (!exists) entry.Guid = item.Flags.HasFlag(PackItemFlags.UniqueId);
        }

        if (row.Scalar("Rate") is { } rate) entry.Rate = ushort.Parse(rate, CultureInfo.InvariantCulture);
        if (target.Algorithm == PackGroupAlgorithm.All && entry.Rate > 0) entry.Rate = 0;                           // "Item cannot have a rate with the All algorithm"
        if (target.Algorithm != PackGroupAlgorithm.All && entry.Rate == 0) throw new InvalidDataException($"{location}: entry {index} of group {group.Name} has no rate.");

        if (row.Scalar("Amount") is { } amount) entry.Amount = Math.Clamp(ushort.Parse(amount, CultureInfo.InvariantCulture), 1u, MaxAmount);
        if (row.Scalar("Duration") is { } duration) entry.Duration = (ushort)uint.Parse(duration, CultureInfo.InvariantCulture);   // pinned asUInt16 truncates (Duration 525600 loads as 1312; verified against the loader dump)
        if (row.Scalar("Announced") is { } announced) entry.Announced = Bool(announced, location);
        if (row.Scalar("UniqueId") is { } guid) entry.Guid = Bool(guid, location);
        if (row.Scalar("Stacked") is { } stacked) entry.Stacked = Bool(stacked, location);
        if (row.Scalar("Named") is { } named) entry.Named = Bool(named, location);
        if (row.Scalar("Bound") is { } bound)
        {
            if (!constants.TryGetConstant("BOUND_" + bound, out var boundValue) || boundValue is < 0 or > 4) throw new InvalidDataException($"{location}: invalid Bound '{bound}'.");
            entry.Bound = (byte)boundValue;
        }
        if (row.Scalar("RandomOptionGroup") is { } randomOptions) entry.RandomOptionGroup = randomOptions;
        if (row.Scalar("RefineMinimum") is { } refineMinimum) entry.RefineMinimum = Math.Min(ushort.Parse(refineMinimum, CultureInfo.InvariantCulture), MaxRefine);
        if (row.Scalar("RefineMaximum") is { } refineMaximum) entry.RefineMaximum = Math.Min(ushort.Parse(refineMaximum, CultureInfo.InvariantCulture), MaxRefine);
        if (row.Scalar("GradeMinimum") is { } gradeMinimum) entry.GradeMinimum = Grade(gradeMinimum, constants, location);
        if (row.Scalar("GradeMaximum") is { } gradeMaximum) entry.GradeMaximum = Grade(gradeMaximum, constants, location);
    }

    private static byte Grade(string name, RathenaConstantTable constants, string location) =>
        constants.TryGetConstant("ENCHANTGRADE_" + name, out var value) && value is >= 0 and <= MaxEnchantGrade ? (byte)value : throw new InvalidDataException($"{location}: invalid enchant grade '{name}'.");

    private static PackGroupEntry ToPack(uint index, Entry e) =>
        new(index, e.ItemId, e.Rate, e.Amount, e.Duration, e.Announced, e.Guid, e.Stacked, e.Named, e.Bound, e.RandomOptionGroup, e.RefineMinimum, e.RefineMaximum, e.GradeMinimum, e.GradeMaximum);

    private static bool Bool(string text, string location) => text.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => throw new InvalidDataException($"{location}: '{text}' is not a boolean."),
    };
}
