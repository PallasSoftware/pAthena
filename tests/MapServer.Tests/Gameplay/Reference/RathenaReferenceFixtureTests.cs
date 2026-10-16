using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Gameplay.Reference;

public sealed class RathenaReferenceFixtureTests
{
    private const string Epic152 = "Known Athena.NET discrepancy, tracked for Epic 15.2 (docs/gameplay/renewal-status-implementation-plan.md).";

    private static string Valid(string evidence = "ReferenceExecuted", string commit = RathenaReferenceFixture.PinnedCommit) => $$$"""
        {"schema":1,"reference":{"commit":"{{{commit}}}","compiler":"g++","runner":"r","configuration":"c"},
         "scenarios":[{"id":"X","evidence":"{{{evidence}}}","scope":"s","input":{},"output":{}}]}
        """;

    [Fact]
    public void CommittedFixture_IsValidAndAllReferenceExecuted()
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        Assert.Equal(["VF-01", "VF-07", "VF-14"], fixture.Ids.Order());
        Assert.All(fixture.Ids, id => Assert.Equal("ReferenceExecuted", fixture.Evidence(id)));
    }

    [Fact] public void Parse_AcceptsWellFormedFixture() => RathenaReferenceFixture.Parse(Valid());
    [Fact] public void Parse_RejectsWrongCommit() => Assert.Throws<InvalidDataException>(() => RathenaReferenceFixture.Parse(Valid(commit: "abc")));
    [Fact] public void Parse_RejectsUnknownEvidence() => Assert.Throws<InvalidDataException>(() => RathenaReferenceFixture.Parse(Valid("Guessed")));
    [Fact] public void Parse_RejectsMissingProvenance() => Assert.Throws<InvalidDataException>(() => RathenaReferenceFixture.Parse(Valid().Replace("\"compiler\":\"g++\",", "")));
    [Fact] public void Parse_RejectsNoScenarios() => Assert.Throws<InvalidDataException>(() => RathenaReferenceFixture.Parse(
        $$"""{"schema":1,"reference":{"commit":"{{RathenaReferenceFixture.PinnedCommit}}","compiler":"g","runner":"r","configuration":"c"},"scenarios":[]}"""));

    // VF-01 (aspd part): Novice, all stats 1, unarmed. Compares the reference adelay with Athena.NET.
    [Fact]
    public void Vf01_FreshNovice_AttackDelayMatchesReference()
    {
        var expected = RathenaReferenceFixture.LoadCommitted().Output("VF-01").GetProperty("battle_status").GetProperty("adelay").GetInt32();
        var stats = new EffectiveCharacterStats(1, 1, 1, 1, 1, 1, 0, 0);
        Assert.Equal(expected, AttackDelayCalculator.AttackDelayMs(stats, null));
    }

    // VF-07: replays each reference sequence through CharacterStatusEffectState and compares val1/val2.
    public static TheoryData<int> BlessingSequences => [1, 2];

    [Theory]
    [MemberData(nameof(BlessingSequences))]
    public void Vf07_Blessing_RefreshMatchesReference(int sequence) => AssertBlessingSequence(sequence);

    [Fact(Skip = Epic152 + " Blessing 10 then 3: reference keeps level 10, Athena.NET overwrites with level 3.")]
    public void Vf07_Blessing_WeakerRefreshKeepsStronger() => AssertBlessingSequence(0);

    private static void AssertBlessingSequence(int index)
    {
        var seq = RathenaReferenceFixture.LoadCommitted().Output("VF-07").GetProperty("sequences")[index];
        var levels = seq.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()).ToArray();
        var state = new CharacterStatusEffectState(new ControllableTimeProvider());
        var steps = seq.GetProperty("steps");
        for (var i = 0; i < levels.Length; i++)
        {
            state.Start(CharacterStatusEffectState.StatusIds.Blessing, 120000, levels[i]);
            Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var status));
            Assert.Equal((steps[i].GetProperty("val1").GetInt32(), steps[i].GetProperty("val2").GetInt32()), (status.Val1, status.Val2));
        }
    }
}
