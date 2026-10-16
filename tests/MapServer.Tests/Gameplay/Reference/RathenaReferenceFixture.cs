using System.Text.Json;

namespace Athena.Net.MapServer.Tests.Gameplay.Reference;

// Loads and validates the committed fixture produced by tools/rathena-reference/run.ps1.
// Expected values come only from that file; nothing here computes them.
internal sealed class RathenaReferenceFixture
{
    public const string PinnedCommit = "e985006171d2eb320ee512a653f4c83aea3d81b6";
    private static readonly string[] EvidenceClasses = ["ReferenceExecuted", "SourceDerived", "IROVerified", "Unverified"];

    private readonly Dictionary<string, JsonElement> _scenarios = [];

    public static RathenaReferenceFixture LoadCommitted() =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gameplay", "Reference", "rathena-reference.json")));

    public static RathenaReferenceFixture Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        if (Str(root, "schema", number: true) != "1") throw new InvalidDataException("Unsupported fixture schema.");
        var reference = Obj(root, "reference");
        if (Str(reference, "commit") != PinnedCommit) throw new InvalidDataException("Fixture commit is not the pinned rAthena commit.");
        Str(reference, "compiler");
        Str(reference, "runner");
        Str(reference, "configuration");
        var fixture = new RathenaReferenceFixture();
        foreach (var scenario in Obj(root, "scenarios", array: true).EnumerateArray())
        {
            var id = Str(scenario, "id");
            var evidence = Str(scenario, "evidence");
            if (!EvidenceClasses.Contains(evidence)) throw new InvalidDataException($"{id}: unknown evidence class '{evidence}'.");
            Str(scenario, "scope");
            Obj(scenario, "input");
            Obj(scenario, "output");
            if (!fixture._scenarios.TryAdd(id, scenario)) throw new InvalidDataException($"{id}: duplicate scenario.");
        }
        if (fixture._scenarios.Count == 0) throw new InvalidDataException("Fixture has no scenarios.");
        return fixture;
    }

    public JsonElement Output(string id) => _scenarios[id].GetProperty("output");
    public JsonElement Input(string id) => _scenarios[id].GetProperty("input");
    public string Evidence(string id) => _scenarios[id].GetProperty("evidence").GetString()!;
    public IEnumerable<string> Ids => _scenarios.Keys;

    private static string Str(JsonElement e, string name, bool number = false)
    {
        if (!e.TryGetProperty(name, out var p)) throw new InvalidDataException($"Missing '{name}'.");
        var s = number ? p.GetRawText() : p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return string.IsNullOrWhiteSpace(s) ? throw new InvalidDataException($"'{name}' is empty or not a string.") : s;
    }

    private static JsonElement Obj(JsonElement e, string name, bool array = false)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != (array ? JsonValueKind.Array : JsonValueKind.Object))
            throw new InvalidDataException($"Missing or malformed '{name}'.");
        return p;
    }
}
