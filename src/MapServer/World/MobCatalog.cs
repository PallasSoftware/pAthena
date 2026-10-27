using System.Collections.Frozen;

namespace Athena.Net.MapServer.World;

// The runtime mob registry: every effective mob_db definition, built once from the generated mob data pack
// (MapData/AthenaMobs.bin), mirroring ItemCatalog. The generated GeneratedMobs.<Name> members are one-line facades over
// ById, so existing call sites keep compile-time names while the pack is the single source of mob data.
public sealed class MobCatalog
{
    private static readonly Lazy<MobCatalog> Production = new(() => Open(Path.Combine(AppContext.BaseDirectory, "MapData", "AthenaMobs.bin")));

    public static MobCatalog Current => Production.Value;

    public string RathenaCommit { get; }
    public FrozenDictionary<int, MobDefinition> ById { get; }

    private MobCatalog(string commit, FrozenDictionary<int, MobDefinition> byId)
    {
        RathenaCommit = commit;
        ById = byId;
    }

    public static MobCatalog Open(string path) => Create(MobPackFormat.Read(File.ReadAllBytes(path)));

    public static MobCatalog Create(MobPack pack) => new(pack.RathenaCommit, pack.Mobs.ToFrozenDictionary(mob => mob.Id));
}
