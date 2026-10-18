using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class MobCatalogTests
{
    private static void AssertSameDefinition(MobDefinition expected, MobDefinition actual)
    {
        // Record equality compares the nested lists by reference, so compare them by content and the rest by value.
        Assert.Equal(expected with { RaceGroups = null, Drops = null, MvpDrops = null }, actual with { RaceGroups = null, Drops = null, MvpDrops = null });
        Assert.Equal(expected.RaceGroups is null, actual.RaceGroups is null);
        Assert.Equal(expected.Drops is null, actual.Drops is null);
        Assert.Equal(expected.MvpDrops is null, actual.MvpDrops is null);
        Assert.Equal(expected.RaceGroups ?? [], actual.RaceGroups ?? []);
        Assert.Equal(expected.Drops ?? [], actual.Drops ?? []);
        Assert.Equal(expected.MvpDrops ?? [], actual.MvpDrops ?? []);
    }

    // Every generated mob resolves to the same complete definition the mob data pack holds: all 2,675 ids, every field
    // including provenance and the nested RaceGroups/Drops/MvpDrops lists.
    [Fact]
    public void EveryGeneratedMob_IsTheCatalogDefinition()
    {
        Assert.Equal(2675, MobCatalog.Current.ById.Count);
        Assert.Equal(MobCatalog.Current.ById.Keys.Order(), GeneratedMobRegistry.Ids.Order());
        foreach (var mob in GeneratedMobRegistry.All) AssertSameDefinition(mob, MobCatalog.Current.ById[mob.Id]);
    }

    [Fact]
    public void Pack_RoundTripsEveryDefinition()
    {
        var pack = new MobPack(MobCatalog.Current.RathenaCommit, [.. MobCatalog.Current.ById.Values]);
        var reread = MobCatalog.Create(MobPackFormat.Read(MobPackFormat.Write(pack)));

        Assert.Equal(pack.Mobs.Count, reread.ById.Count);
        foreach (var mob in pack.Mobs) AssertSameDefinition(mob, reread.ById[mob.Id]);
        Assert.Equal(MobPackFormat.Write(pack), MobPackFormat.Write(new MobPack(pack.RathenaCommit, [.. reread.ById.Values])));
    }

    [Fact]
    public void CorruptedBody_FailsContentHashVerification()
    {
        var bytes = MobPackFormat.Write(new MobPack(MobCatalog.Current.RathenaCommit, [MobCatalog.Current.ById[1002]]));
        bytes[^1] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => MobPackFormat.Read(bytes));
    }
}
