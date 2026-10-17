using System.Text;

namespace Athena.WorldCompiler.Generation;

// Reader for the block-YAML subset the pinned rAthena databases (db/re/*.yml) use: maps, sequences (including
// "- Key: value" inline maps and sequences at the same indent as their key), plain/quoted scalars, trailing "# comments"
// and literal block scalars ("Script: |"). It keeps line numbers for provenance and never interprets scalars: typing
// is the database loader's job. SimpleYaml is intentionally not reused: it strips '#' inside scripts and has no block scalars.
internal abstract record YamlNode(int Line);
internal sealed record YamlScalar(string Value, int Line) : YamlNode(Line);
internal sealed record YamlMap(IReadOnlyList<KeyValuePair<string, YamlNode>> Entries, int Line) : YamlNode(Line)
{
    // rapidyaml (used by rAthena) resolves node["Key"] to the FIRST entry when a map repeats a key; the pinned data does so (e.g. item 400276 Trade).
    internal YamlNode? Get(string key) => Entries.FirstOrDefault(entry => entry.Key == key).Value;
    internal bool Has(string key) => Entries.Any(entry => entry.Key == key);
    internal string? Scalar(string key) => (Get(key) as YamlScalar)?.Value;
}
internal sealed record YamlSequence(IReadOnlyList<YamlNode> Items, int Line) : YamlNode(Line);

internal static class RathenaYaml
{
    private sealed class Line(int number, string raw)
    {
        internal int Number { get; } = number;
        internal string Raw { get; set; } = raw;
        internal int Indent => Raw.TakeWhile(c => c == ' ').Count();
        internal string Text => Raw.Trim();
        internal bool IsBlank => Text.Length == 0 || Text[0] == '#';
    }

    internal static YamlNode Parse(string source, string sourceName)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((text, index) => new Line(index + 1, text)).ToList();
        var position = 0;
        SkipBlank(lines, ref position);
        return position >= lines.Count ? new YamlMap([], 0) : ParseBlock(lines, ref position, lines[position].Indent, sourceName);
    }

    private static void SkipBlank(List<Line> lines, ref int position)
    {
        while (position < lines.Count && lines[position].IsBlank) position++;
    }

    private static bool IsDash(Line line) => line.Text == "-" || line.Text.StartsWith("- ", StringComparison.Ordinal);

    private static YamlNode ParseBlock(List<Line> lines, ref int position, int indent, string sourceName) =>
        IsDash(lines[position]) ? ParseSequence(lines, ref position, indent, sourceName) : ParseMap(lines, ref position, indent, sourceName);

    private static YamlMap ParseMap(List<Line> lines, ref int position, int indent, string sourceName)
    {
        var first = lines[position].Number;
        var entries = new List<KeyValuePair<string, YamlNode>>();
        while (true)
        {
            SkipBlank(lines, ref position);
            if (position >= lines.Count || lines[position].Indent != indent || IsDash(lines[position])) break;

            var line = lines[position++];
            var (key, rest) = SplitKey(line, sourceName);
            YamlNode value;
            if (rest is "|" or "|-" or "|+")
            {
                value = new YamlScalar(ReadBlockScalar(lines, ref position, indent), line.Number);
            }
            else if (rest.Length > 0)
            {
                value = new YamlScalar(Unquote(StripComment(rest)), line.Number);
            }
            else
            {
                var probe = position;
                SkipBlank(lines, ref probe);
                if (probe < lines.Count && (lines[probe].Indent > indent || (lines[probe].Indent == indent && IsDash(lines[probe]))))
                {
                    position = probe;
                    value = ParseBlock(lines, ref position, lines[position].Indent, sourceName);
                }
                else
                {
                    value = new YamlMap([], line.Number);
                }
            }
            entries.Add(new(key, value));
        }
        return new(entries, first);
    }

    private static YamlSequence ParseSequence(List<Line> lines, ref int position, int indent, string sourceName)
    {
        var first = lines[position].Number;
        var items = new List<YamlNode>();
        while (true)
        {
            SkipBlank(lines, ref position);
            if (position >= lines.Count || lines[position].Indent != indent || !IsDash(lines[position])) break;

            var line = lines[position];
            var content = line.Text.Length == 1 ? "" : line.Text[2..].TrimStart();
            if (content.Length == 0)
            {
                position++;
                SkipBlank(lines, ref position);
                items.Add(position < lines.Count && lines[position].Indent > indent
                    ? ParseBlock(lines, ref position, lines[position].Indent, sourceName)
                    : new YamlScalar("", line.Number));
            }
            else if (LooksLikeKey(content))
            {
                // "- Key: value" opens a map whose remaining keys sit at the column of "Key".
                var column = indent + (line.Text.Length - content.Length);
                line.Raw = new string(' ', column) + content;
                items.Add(ParseMap(lines, ref position, column, sourceName));
            }
            else
            {
                position++;
                items.Add(new YamlScalar(Unquote(StripComment(content)), line.Number));
            }
        }
        return new(items, first);
    }

    private static bool LooksLikeKey(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0) return false;
        if (colon != text.Length - 1 && text[colon + 1] != ' ') return false;
        return text[..colon].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static (string Key, string Remainder) SplitKey(Line line, string sourceName)
    {
        var text = line.Text;
        var colon = text.IndexOf(':');
        while (colon >= 0 && colon != text.Length - 1 && text[colon + 1] != ' ') colon = text.IndexOf(':', colon + 1);
        if (colon <= 0) throw new ArgumentException($"{sourceName}:{line.Number}: expected a 'key: value' entry but found '{text}'.");
        return (text[..colon].Trim(), text[(colon + 1)..].Trim());
    }

    // Literal block: every following line indented deeper than the key (blank lines included); the common indent is removed.
    private static string ReadBlockScalar(List<Line> lines, ref int position, int keyIndent)
    {
        var collected = new List<string>();
        var blockIndent = -1;
        while (position < lines.Count)
        {
            var line = lines[position];
            if (line.Text.Length == 0) { collected.Add(""); position++; continue; }
            if (line.Indent <= keyIndent) break;
            if (blockIndent < 0) blockIndent = line.Indent;
            collected.Add(line.Raw.Length >= blockIndent ? line.Raw[blockIndent..] : line.Raw.TrimStart());
            position++;
        }
        while (collected.Count > 0 && collected[^1].Length == 0) collected.RemoveAt(collected.Count - 1);
        return string.Join('\n', collected);
    }

    private static string StripComment(string value)
    {
        if (value.Length > 0 && value[0] is '"' or '\'') return value;
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? value[..hash].TrimEnd() : value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"')
        {
            var close = value.LastIndexOf('"');
            if (close > 0)
            {
                var inner = value[1..close];
                var builder = new StringBuilder();
                for (var i = 0; i < inner.Length; i++)
                {
                    if (inner[i] == '\\' && i + 1 < inner.Length)
                    {
                        i++;
                        builder.Append(inner[i] switch { 'n' => '\n', 't' => '\t', var other => other });
                    }
                    else builder.Append(inner[i]);
                }
                return builder.ToString();
            }
        }
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        return value;
    }
}
