using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Vertical-slice evaluation: proves a prototype binary mob pack + MobCatalogPrototype can round-trip REAL mob data -
// including nested Drops/MvpDrops/RaceGroups - with the same property-access shape ItemCatalog already provides for
// items. Not a production test of generated content; backs the tradeoff writeup for the "should monsters move to a
// binary pack" architectural decision. See MobPackPrototype.cs's own doc comment.
public sealed class MobPackPrototypeTests
{
    private static readonly MobDefinition[] Representative =
    [
        GeneratedMobs.Scorpion,       // baseline: flat fields, a populated Drops list, no RaceGroups/MvpDrops.
        GeneratedMobs.ArcherSkeleton, // has a populated RaceGroups list.
        GeneratedMobs.MGamemaster,    // has a populated MvpDrops list.
    ];

    [Fact]
    public void RoundTrip_PreservesEveryFieldIncludingNestedCollections()
    {
        var bytes = MobPackPrototypeFormat.Write("e985006171d2eb320ee512a653f4c83aea3d81b6", Representative);
        var catalog = MobCatalogPrototype.Create(bytes);

        Assert.Equal(Representative.Length, catalog.ById.Count);

        foreach (var original in Representative)
        {
            var loaded = catalog.ById[original.Id];
            Assert.Equal(original.AegisName, loaded.AegisName);
            Assert.Equal(original.Name, loaded.Name);
            Assert.Equal(original.Level, loaded.Level);
            Assert.Equal(original.MaxHp, loaded.MaxHp);
            Assert.Equal(original.Attack, loaded.Attack);
            Assert.Equal(original.Mode, loaded.Mode);
            Assert.Equal(original.EffectiveMode, loaded.EffectiveMode); // derived property, computed identically on read.
            Assert.Equal(original.Size, loaded.Size);
            Assert.Equal(original.Race, loaded.Race);
            Assert.Equal(original.Element, loaded.Element);
            Assert.Equal(original.Class, loaded.Class);

            Assert.Equal(original.RaceGroups ?? [], loaded.RaceGroups ?? []);
            Assert.Equal(original.Drops ?? [], loaded.Drops ?? []);
            Assert.Equal(original.MvpDrops ?? [], loaded.MvpDrops ?? []);
        }
    }

    [Fact]
    public void RoundTrip_PreservesRaceGroupNestedEntries()
    {
        var bytes = MobPackPrototypeFormat.Write("e985006171d2eb320ee512a653f4c83aea3d81b6", [GeneratedMobs.ArcherSkeleton]);
        var loaded = MobCatalogPrototype.Create(bytes).ById[GeneratedMobs.ArcherSkeleton.Id];

        Assert.NotNull(loaded.RaceGroups);
        Assert.Equal(GeneratedMobs.ArcherSkeleton.RaceGroups!.Count, loaded.RaceGroups!.Count);
        for (var i = 0; i < loaded.RaceGroups.Count; i++)
        {
            Assert.Equal(GeneratedMobs.ArcherSkeleton.RaceGroups[i].Name, loaded.RaceGroups[i].Name);
            Assert.Equal(GeneratedMobs.ArcherSkeleton.RaceGroups[i].Value, loaded.RaceGroups[i].Value);
        }
    }

    [Fact]
    public void RoundTrip_PreservesDropEntryAllFields()
    {
        var bytes = MobPackPrototypeFormat.Write("e985006171d2eb320ee512a653f4c83aea3d81b6", [GeneratedMobs.Scorpion]);
        var loaded = MobCatalogPrototype.Create(bytes).ById[GeneratedMobs.Scorpion.Id];

        Assert.NotNull(loaded.Drops);
        Assert.Equal(GeneratedMobs.Scorpion.Drops!.Count, loaded.Drops!.Count);
        for (var i = 0; i < loaded.Drops.Count; i++)
        {
            var (expected, actual) = (GeneratedMobs.Scorpion.Drops[i], loaded.Drops[i]);
            Assert.Equal(expected.Item, actual.Item);
            Assert.Equal(expected.Rate, actual.Rate);
            Assert.Equal(expected.StealProtected, actual.StealProtected);
            Assert.Equal(expected.RandomOptionGroup, actual.RandomOptionGroup);
        }
    }

    // Header reuse proof: the prototype reads/writes through the exact same GeneratedPackHeader ItemPackFormat uses -
    // a corrupted body is caught by the shared hash check without the mob format needing its own verification logic.
    [Fact]
    public void CorruptedBody_FailsContentHashVerification()
    {
        var bytes = MobPackPrototypeFormat.Write("e985006171d2eb320ee512a653f4c83aea3d81b6", [GeneratedMobs.Poring]);
        bytes[^1] ^= 0xFF; // flip the last body byte.

        Assert.Throws<InvalidDataException>(() => MobCatalogPrototype.Create(bytes));
    }
}
