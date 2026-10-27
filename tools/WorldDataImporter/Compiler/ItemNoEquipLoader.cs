using System.Globalization;

namespace Athena.WorldCompiler.Generation;

// Port of itemdb_read_noequip (itemdb.cpp:3769-3789) over db/re/item_noequip.txt (loaded via
// sv_readdb(..., "item_noequip.txt", ',', 2, 2, -1, &itemdb_read_noequip, ...), itemdb.cpp:4909).
// Format: "<item_id>,<bitmask>" rows (comments '//' and blank lines skipped, matching sv_readdb).
// A positive bitmask is OR'd into item_data.flag.no_equip; a negative value NOT-ANDs (unsets) those
// bits instead - both applied in file order, so a later negative row can clear an earlier positive one
// for the same item (see item_noequip.txt's own documented example: 519,-2 unsetting 519,322's PVP bit).
internal static class ItemNoEquipLoader
{
    internal const string Path = "db/re/item_noequip.txt";

    internal static void Apply(string text, Dictionary<uint, uint> noEquipById, List<string> warnings)
    {
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Split("//", 2, StringSplitOptions.None)[0].Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            if (parts.Length != 2
                || !uint.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flag))
            {
                warnings.Add($"{Path}: malformed row '{rawLine}', skipped.");
                continue;
            }

            if (!noEquipById.ContainsKey(itemId))
            {
                warnings.Add($"itemdb_read_noequip: Invalid item id {itemId}.");
                continue;
            }

            noEquipById[itemId] = flag >= 0
                ? noEquipById[itemId] | (uint)flag
                : noEquipById[itemId] & ~(uint)(-flag);
        }
    }
}
