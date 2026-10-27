using System.Text.RegularExpressions;

namespace Athena.WorldCompiler.Generation;

// Names of the pinned script engine's buildin commands/functions, read from the BUILDIN_DEF table in src/map/script.cpp
// (BUILDIN_DEF(name, sig), BUILDIN_DEF2(impl, "name", sig) and the deprecated forms). Lookup is case-insensitive like rAthena.
internal static partial class RathenaBuiltins
{
    internal static IReadOnlySet<string> Names(string scriptCpp)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Plain().Matches(scriptCpp)) names.Add(match.Groups[1].Value);
        foreach (Match match in Aliased().Matches(scriptCpp)) names.Add(match.Groups[1].Value);
        return names;
    }

    [GeneratedRegex(@"BUILDIN_DEF(?:_DEPRECATED)?\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,")]
    private static partial Regex Plain();

    [GeneratedRegex(@"BUILDIN_DEF2(?:_DEPRECATED)?\(\s*[A-Za-z_][A-Za-z0-9_]*\s*,\s*""([^""]+)""")]
    private static partial Regex Aliased();
}
