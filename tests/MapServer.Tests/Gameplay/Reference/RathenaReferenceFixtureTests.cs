using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.Tests.World;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Gameplay.Reference;

public sealed class RathenaReferenceFixtureTests
{
    private static string Valid(string evidence = "ReferenceExecuted", string commit = RathenaReferenceFixture.PinnedCommit) => $$$"""
        {"schema":1,"reference":{"commit":"{{{commit}}}","compiler":"g++","runner":"r","configuration":"c"},
         "scenarios":[{"id":"X","evidence":"{{{evidence}}}","scope":"s","input":{},"output":{}}]}
        """;

    [Fact]
    public void CommittedFixture_IsValidAndAllReferenceExecuted()
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        Assert.Equal(["VF-01", "VF-07", "VF-08", "VF-14"], fixture.Ids.Order());
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

    // VF-07/VF-08: replays each reference sequence through CharacterStatusEffectState and compares the
    // retained val1/val2 and whether the second start moved the expiry (50ms later), then expiry.
    public static TheoryData<string, ushort, int> StatusSequences
    {
        get
        {
            var data = new TheoryData<string, ushort, int>();
            foreach (var (id, status) in new[] { ("VF-07", CharacterStatusEffectState.StatusIds.Blessing), ("VF-08", CharacterStatusEffectState.StatusIds.IncreaseAgi) })
                for (var i = 0; i < 3; i++) data.Add(id, status, i);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(StatusSequences))]
    public void StatusRefresh_MatchesReference(string scenario, ushort statusId, int index)
    {
        var seq = RathenaReferenceFixture.LoadCommitted().Output(scenario).GetProperty("sequences")[index];
        var levels = seq.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()).ToArray();
        var steps = seq.GetProperty("steps");
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock);
        DateTimeOffset previousDeadline = default;
        for (var i = 0; i < levels.Length; i++)
        {
            if (i > 0) clock.Advance(TimeSpan.FromMilliseconds(50));
            state.Start(statusId, 120000, levels[i]);
            Assert.True(state.TryGet(statusId, out var status));
            Assert.Equal((steps[i].GetProperty("val1").GetInt32(), steps[i].GetProperty("val2").GetInt32()), (status.Val1, status.Val2));
            if (i > 0) Assert.Equal(steps[i].GetProperty("deadline_moved").GetInt32() == 1, status.ExpiresAt != previousDeadline);
            previousDeadline = status.ExpiresAt;
        }
        // Reference status_change_end: nothing active afterwards.
        Assert.Equal(0, seq.GetProperty("after_end").GetProperty("present").GetInt32());
        clock.Advance(TimeSpan.FromMilliseconds(120000));
        state.ExpireDue(clock.GetUtcNow());
        Assert.False(state.TryGet(statusId, out _));
    }
}
