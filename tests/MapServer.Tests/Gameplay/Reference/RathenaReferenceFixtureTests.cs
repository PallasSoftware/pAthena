using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
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
        Assert.Equal(["VF-01", "VF-02", "VF-04", "VF-07", "VF-08", "VF-11", "VF-14"], fixture.Ids.Order());
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

    // VF-14: real spawned mob -> real battle_calc_attack vs the VF-01 Novice (def2 = 1), 2000 seeded samples each.
    // The reference's non-miss damage histogram gives the exact set of reachable damage values
    // (roll in [watk, watk2) + batk - def2). Accuracy/FLEE is not modeled by Athena, so reference misses are ignored;
    // the Athena side samples its production roll and must produce exactly that value set.
    public static TheoryData<int> MobAttackMobs => [1002, 1031, 1013];

    [Theory]
    [MemberData(nameof(MobAttackMobs))]
    public void MobAttack_ReachableDamageValuesMatchReference(int mobId)
    {
        var attack = RathenaReferenceFixture.LoadCommitted().Output("VF-14").GetProperty("attacks").EnumerateArray().Single(a => a.GetProperty("mob_id").GetInt32() == mobId);
        var expected = attack.GetProperty("damage_histogram").EnumerateObject().Select(p => long.Parse(p.Name)).Order().ToArray();
        var mob = mobId switch { 1002 => GeneratedMobs.Poring, 1031 => GeneratedMobs.Poporing, _ => GeneratedMobs.Wolf };
        var novice = new PlayerCombatSnapshot(AccountId: 1, Map: "prontera", X: 0, Y: 0, IsAlive: true, IsWalking: false, BaseLevel: 1, Vitality: 1, Agility: 1,
            SoftDefense: RathenaReferenceFixture.LoadCommitted().Output("VF-01").GetProperty("battle_status").GetProperty("def2").GetInt32());

        var observed = Enumerable.Range(0, 20000)
            .Select(_ => MobBasicAttackCalculator.Calculate(mob, novice))
            .Where(r => !r.IsMiss).Select(r => (long)r.Damage).Distinct().Order().ToArray();

        Assert.Equal(expected, observed);
    }

    // VF-02: Novice job-level bonuses. Reference battle_status six stats == allocated + cumulative job bonus
    // (no statuses/equipment), as consumed by every Recalculate caller.
    public static TheoryData<int> JobBonusCases => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    [Theory]
    [MemberData(nameof(JobBonusCases))]
    public void NoviceJobBonus_EffectiveSixStatsMatchReference(int index)
    {
        var c = RathenaReferenceFixture.LoadCommitted().Output("VF-02").GetProperty("cases")[index];
        var i = c.GetProperty("input");
        ushort U(string n) => (ushort)i.GetProperty(n).GetInt32();
        var state = new CharacterGameplayState(CharacterId: 1, Version: 1, JobClass: 0, BaseLevel: U("base_level"), JobLevel: U("job_level"),
            BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10, StatPoints: 0, SkillPoints: 0,
            Strength: U("str"), Agility: U("agi"), Vitality: U("vit"), Intelligence: U("int"), Dexterity: U("dex"), Luck: U("luk"));

        var effective = new CharacterStatusEffectState(new FakeTimeProvider()).Recalculate(state);

        var expected = c.GetProperty("battle_status");
        Assert.Equal(
            [expected.GetProperty("str").GetInt32(), expected.GetProperty("agi").GetInt32(), expected.GetProperty("vit").GetInt32(),
             expected.GetProperty("int").GetInt32(), expected.GetProperty("dex").GetInt32(), expected.GetProperty("luk").GetInt32()],
            [effective.Strength, effective.Agility, effective.Vitality, effective.Intelligence, effective.Dexterity, effective.Luck]);
    }

    private static DerivedStats Expected(System.Text.Json.JsonElement b) => new(
        b.GetProperty("batk").GetInt32(), b.GetProperty("hit").GetInt32(), b.GetProperty("flee").GetInt32(), b.GetProperty("flee2").GetInt32(),
        b.GetProperty("cri").GetInt32(), b.GetProperty("def2").GetInt32(), b.GetProperty("mdef2").GetInt32());

    private static CharacterGameplayState StateFrom(System.Text.Json.JsonElement i)
    {
        ushort U(string n) => (ushort)i.GetProperty(n).GetInt32();
        return new CharacterGameplayState(CharacterId: 1, Version: 1, JobClass: 0, BaseLevel: U("base_level"), JobLevel: U("job_level"),
            BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10, StatPoints: 0, SkillPoints: 0,
            Strength: U("str"), Agility: U("agi"), Vitality: U("vit"), Intelligence: U("int"), Dexterity: U("dex"), Luck: U("luk"));
    }

    [Theory]
    [MemberData(nameof(JobBonusCases))]
    public void Vf02_DerivedStatsMatchReference(int index)
    {
        var c = RathenaReferenceFixture.LoadCommitted().Output("VF-02").GetProperty("cases")[index];
        var state = StateFrom(c.GetProperty("input"));
        var snapshot = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state);
        Assert.Equal(Expected(c.GetProperty("battle_status")), DerivedStatCalculator.Calculate(snapshot));
    }

    [Fact]
    public void Vf01_FreshNovice_DerivedStatsMatchReference()
    {
        var b = RathenaReferenceFixture.LoadCommitted().Output("VF-01").GetProperty("battle_status");
        var state = new CharacterGameplayState(1, 1, 0, 1, 1, 0, 0, 40, 10, 40, 10, 0, 0, 1, 1, 1, 1, 1, 1);
        var snapshot = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state);
        Assert.Equal(Expected(b), DerivedStatCalculator.Calculate(snapshot));
    }

    // VF-07/VF-08: every step and the post-end state of each sequence, full derived-stat comparison.
    [Theory]
    [MemberData(nameof(StatusSequences))]
    public void StatusSequences_DerivedStatsMatchReference(string scenario, ushort statusId, int index)
    {
        var output = RathenaReferenceFixture.LoadCommitted().Output(scenario);
        var seq = output.GetProperty("sequences")[index];
        var input = RathenaReferenceFixture.LoadCommitted().Input(scenario);
        var state = StateFrom(input);
        var clock = new FakeTimeProvider();
        var statuses = new CharacterStatusEffectState(clock);
        var levels = seq.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()).ToArray();
        var steps = seq.GetProperty("steps");
        for (var i = 0; i < levels.Length; i++)
        {
            statuses.Start(statusId, 120000, levels[i]);
            Assert.Equal(Expected(steps[i]), DerivedStatCalculator.Calculate(statuses.Capture(() => state)));
        }
        statuses.ExpireDue(clock.GetUtcNow().AddMilliseconds(120001));
        clock.Advance(TimeSpan.FromMilliseconds(120001));
        statuses.ExpireDue(clock.GetUtcNow());
        Assert.Equal(Expected(seq.GetProperty("after_end")), DerivedStatCalculator.Calculate(statuses.Capture(() => state)));
    }

    public static TheoryData<int> MaxResourceCases => new(Enumerable.Range(0, 40));

    [Theory]
    [MemberData(nameof(MaxResourceCases))]
    public void Vf04_MaxHpSp_MatchReference(int index)
    {
        var c = RathenaReferenceFixture.LoadCommitted().Output("VF-04").GetProperty("cases")[index];
        var i = c.GetProperty("input");
        var state = new CharacterGameplayState(1, 1, 0, (ushort)i.GetProperty("base_level").GetInt32(), 1, 0, 0, 1, 1, 1, 1, 0, 0,
            1, 1, (ushort)i.GetProperty("vit").GetInt32(), (ushort)i.GetProperty("int").GetInt32(), 1, 1);
        var snapshot = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state);
        Assert.Equal((c.GetProperty("max_hp").GetUInt32(), c.GetProperty("max_sp").GetUInt32()),
            (StatusCalculator.MaxHp(state, snapshot.Effective.Vitality), StatusCalculator.MaxSp(state, snapshot.Effective.Intelligence)));
    }

    // The progression path (level-up) must use the same maxima as the calculator.
    [Theory]
    [MemberData(nameof(MaxResourceCases))]
    public void Vf04_ProgressionMaxima_MatchReference(int index)
    {
        var c = RathenaReferenceFixture.LoadCommitted().Output("VF-04").GetProperty("cases")[index];
        var i = c.GetProperty("input");
        var level = (ushort)i.GetProperty("base_level").GetInt32();
        if (level == 1) return; // level 1 is not reachable through a level-up
        var definition = Athena.Net.MapServer.Generated.Progression.GeneratedProgressionRegistry.Get((ushort)0);
        var before = new CharacterGameplayState(1, 1, 0, (ushort)(level - 1), 1, definition.BaseExperienceToNext[level - 1], 0, 40, 11, 40, 11, 0, 0, 1, 1,
            (ushort)i.GetProperty("vit").GetInt32(), (ushort)i.GetProperty("int").GetInt32(), 1, 1);
        var after = CharacterProgressionService.Calculate(before, 0, 0).After;
        Assert.Equal(level, after.BaseLevel);
        Assert.Equal((c.GetProperty("max_hp").GetUInt32(), c.GetProperty("max_sp").GetUInt32()), (after.MaxHp, after.MaxSp));
    }

    public static TheoryData<int> ResourceTransitionCases => new(Enumerable.Range(0, 8));

    [Theory]
    [MemberData(nameof(ResourceTransitionCases))]
    public void Vf11_ResourceTransition_MatchesReference(int index)
    {
        var c = RathenaReferenceFixture.LoadCommitted().Output("VF-11").GetProperty("cases")[index];
        var i = c.GetProperty("input");
        var before = new CharacterGameplayState(1, 1, 0, (ushort)i.GetProperty("base_level").GetInt32(), 1, 0, 0,
            i.GetProperty("hp").GetUInt32(), i.GetProperty("sp").GetUInt32(), c.GetProperty("before").GetProperty("max_hp").GetUInt32(), c.GetProperty("before").GetProperty("max_sp").GetUInt32(),
            0, 0, 1, 1, (ushort)i.GetProperty("new_vit").GetInt32(), (ushort)i.GetProperty("new_int").GetInt32(), 1, 1);

        var after = StatusCalculator.WithRecalculatedResources(before);

        var expected = c.GetProperty("after");
        Assert.Equal((expected.GetProperty("max_hp").GetUInt32(), expected.GetProperty("max_sp").GetUInt32(), expected.GetProperty("hp").GetUInt32(), expected.GetProperty("sp").GetUInt32()),
            (after.MaxHp, after.MaxSp, after.CurrentHp, after.CurrentSp));
    }
}
