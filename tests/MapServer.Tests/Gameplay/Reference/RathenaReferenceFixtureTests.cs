using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.GameData.SizeFix;
using Athena.Net.MapServer.Net;
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
        Assert.Equal(["VF-01", "VF-02", "VF-04", "VF-05", "VF-06", "VF-07", "VF-08", "VF-11", "VF-14", "VF-15", "VF-16", "VF-17"], fixture.Ids.Order());
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
        Assert.Equal(expected, AttackDelayCalculator.AttackDelayMs(stats, null, 0));
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
        b.GetProperty("cri").GetInt32(), b.GetProperty("def2").GetInt32(), b.GetProperty("mdef2").GetInt32(), MagicAttackMin: b.GetProperty("matk_min").GetInt32(), MagicAttackMax: b.GetProperty("matk_max").GetInt32());

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

    // VF-02/07/08 timing: Novice fist ASPD (amotion/adelay) and walking speed from the same captured snapshot.
    [Theory]
    [MemberData(nameof(JobBonusCases))]
    public void Vf02_AttackAndWalkTiming_MatchReference(int index)
    {
        var c = RathenaReferenceFixture.LoadCommitted().Output("VF-02").GetProperty("cases")[index];
        var state = StateFrom(c.GetProperty("input"));
        var snapshot = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state);
        var expected = c.GetProperty("battle_status");
        AssertTiming(expected, snapshot);
    }

    [Theory]
    [MemberData(nameof(StatusSequences))]
    public void StatusSequences_AttackAndWalkTiming_MatchReference(string scenario, ushort statusId, int index)
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        var seq = fixture.Output(scenario).GetProperty("sequences")[index];
        var state = StateFrom(fixture.Input(scenario));
        var clock = new FakeTimeProvider();
        var statuses = new CharacterStatusEffectState(clock);
        var levels = seq.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()).ToArray();
        var steps = seq.GetProperty("steps");
        for (var i = 0; i < levels.Length; i++)
        {
            statuses.Start(statusId, 120000, levels[i]);
            AssertTiming(steps[i], statuses.Capture(() => state));
        }
        clock.Advance(TimeSpan.FromMilliseconds(120001));
        statuses.ExpireDue(clock.GetUtcNow());
        AssertTiming(seq.GetProperty("after_end"), statuses.Capture(() => state));
    }

    private static void AssertTiming(System.Text.Json.JsonElement expected, CharacterStatusSnapshot snapshot) =>
        Assert.Equal((expected.GetProperty("adelay").GetInt32(), expected.GetProperty("speed").GetInt32(), expected.GetProperty("dmotion").GetInt32()),
            (AttackDelayCalculator.AttackDelayMs(snapshot.Effective, null, snapshot.State.JobClass), MovementSpeedCalculator.CellDurationMs(snapshot.Effective.MoveSpeedHaste),
             PlayerDamageMotionCalculator.Calculate(snapshot.Effective.Agility)));

    // ---- Equipment (VF-05 / VF-06 / VF-15) ----

    private static CharacterInventorySnapshot Wearing(IEnumerable<int> itemIds) => new(itemIds.Select((id, slot) => new CharacterInventoryItem(
        DurableId: (uint)slot + 1, SlotIndex: (uint)slot, ItemId: id, Amount: 1, Equip: ((GeneratedItems.ById[id] as IEquippableItemDefinition)!.EquipLocation), Identified: true, 0, 0, 0)).ToArray());

    public static TheoryData<int> LoadoutCases => [0, 1, 2, 3];

    [Theory]
    [MemberData(nameof(LoadoutCases))]
    public void Vf05_EquipmentLoadouts_StatsMatchReference(int index)
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        var loadout = fixture.Output("VF-05").GetProperty("loadouts")[index];
        var state = StateFrom(fixture.Input("VF-05"));
        var snapshot = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state);
        Assert.True(EquipmentStats.TryFromInventory(Wearing(loadout.GetProperty("items").EnumerateArray().Select(i => i.GetInt32())), GeneratedItems.ById, out var equipment));
        var b = loadout.GetProperty("battle_status");

        var derived = DerivedStatCalculator.Calculate(snapshot, equipment);

        Assert.Equal((b.GetProperty("def").GetInt32(), b.GetProperty("watk").GetInt32()), (derived.HardDefense, equipment.WeaponAttack));
        Assert.Equal(Expected(b) with { HardDefense = derived.HardDefense }, derived);
        Assert.Equal(b.GetProperty("adelay").GetInt32(), AttackDelayCalculator.AttackDelayMs(snapshot.Effective, equipment.Weapon?.WeaponType, snapshot.State.JobClass));
        Assert.Equal(b.GetProperty("range").GetInt32(), BasicAttackRangeResolver.Resolve(equipment.Weapon));
    }

    [Fact]
    public void EquipmentStats_UnknownOrUnsupportedEquippedItem_IsUnresolvedNotZero()
    {
        var unknown = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, 999999, 1, 0x10, true, 0, 0, 0)]);
        Assert.False(EquipmentStats.TryFromInventory(unknown, GeneratedItems.ById, out _));
        var unequippedUnknown = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, 999999, 1, 0, true, 0, 0, 0)]);
        Assert.True(EquipmentStats.TryFromInventory(unequippedUnknown, GeneratedItems.ById, out var stats));
        Assert.Equal(0, stats.HardDefense);
    }

    public static TheoryData<int> KnifeTargets => [0, 1, 2];

    // Reference: a Knife-wielding Novice's non-critical, non-miss damage values against real mobs. Accuracy and
    // criticals are not modeled by Athena yet, so only the reachable normal-hit value set is compared.
    [Theory]
    [MemberData(nameof(KnifeTargets))]
    public void Vf06_KnifeAttack_ReachableNormalDamageMatchesReference(int index)
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        var attack = fixture.Output("VF-06").GetProperty("attacks")[index];
        var mob = attack.GetProperty("mob_id").GetInt32() switch { 1002 => GeneratedMobs.Poring, 1007 => GeneratedMobs.Fabre, _ => GeneratedMobs.Wolf };
        var expected = attack.GetProperty("normal").EnumerateObject().Select(p => long.Parse(p.Name)).Order().ToArray();
        var state = StateFrom(fixture.Input("VF-06").GetProperty("attacker"));
        var snapshot = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state);

        var observed = Enumerable.Range(0, 20000)
            .Select(_ => WeaponAttackCalculator.Calculate(snapshot.Effective, state.BaseLevel, GeneratedItems.Knife, mob))
            .Where(r => !r.IsMiss).Select(r => (long)r.Damage).Distinct().Order().ToArray();

        Assert.Equal(expected, observed);
    }

    [Theory]
    [InlineData(0, 1031)]
    [InlineData(1, 1013)]
    public void Vf15_MobAttackAgainstArmor_ReachableNormalDamageMatchesReference(int index, int mobId)
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        var output = fixture.Output("VF-15");
        var target = output.GetProperty("target_battle_status");
        var attack = output.GetProperty("attacks")[index];
        Assert.Equal(mobId, attack.GetProperty("mob_id").GetInt32());
        var expected = attack.GetProperty("normal").EnumerateObject().Select(p => long.Parse(p.Name)).Order().ToArray();
        var mob = mobId == 1031 ? GeneratedMobs.Poporing : GeneratedMobs.Wolf;
        var novice = new PlayerCombatSnapshot(1, "prontera", 0, 0, true, false, 10, 12, 15, SoftDefense: target.GetProperty("def2").GetInt32(), HardDefense: target.GetProperty("def").GetInt32());

        var observed = Enumerable.Range(0, 20000)
            .Select(_ => MobBasicAttackCalculator.Calculate(mob, novice))
            .Where(r => !r.IsMiss).Select(r => (long)r.Damage).Distinct().Order().ToArray();

        Assert.Equal(expected, observed);
    }

    // The generated size-fix table is exactly what the pinned loader built (db/size_fix.yml + Renewal imports).
    [Fact]
    public void GeneratedSizeFix_EqualsTheTableLoadedByThePinnedServer()
    {
        var loaded = RathenaReferenceFixture.LoadCommitted().Output("VF-06").GetProperty("loaded_size_fix_db");
        Assert.Equal(24, loaded.GetArrayLength());
        foreach (var weapon in loaded.EnumerateArray())
        {
            var type = (WeaponType)weapon.GetProperty("weapon").GetInt32();
            var entry = weapon.GetProperty("entry").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Assert.Equal(entry, new[] { GeneratedSizeFix.Percent(type, MobSize.Small), GeneratedSizeFix.Percent(type, MobSize.Medium), GeneratedSizeFix.Percent(type, MobSize.Big) });
        }
    }

    // ---- VF-16: job coverage ----

    public static TheoryData<int> JobCases => new(Enumerable.Range(0, 88));

    private static (CharacterGameplayState State, System.Text.Json.JsonElement Battle) JobCase(int index)
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        var c = fixture.Output("VF-16").GetProperty("cases")[index];
        var i = fixture.Input("VF-16");
        ushort U(string n) => (ushort)i.GetProperty(n).GetInt32();
        var state = new CharacterGameplayState(1, 1, (ushort)c.GetProperty("job").GetInt32(), (ushort)c.GetProperty("base_level").GetInt32(), (ushort)c.GetProperty("job_level").GetInt32(),
            0, 0, 1, 1, 1, 1, 0, 0, U("str"), U("agi"), U("vit"), U("int"), U("dex"), U("luk"));
        return (state, c.GetProperty("battle_status"));
    }

    // Jobs outside JobSupport.Verified are not asserted equal; they must carry a documented known divergence instead.
    private static bool VerifiedOrDocumented(CharacterGameplayState state)
    {
        if (JobSupport.IsReferenceVerified(state.JobClass)) return true;
        Assert.NotEqual(JobSupport.ParameterRules.None, JobSupport.KnownDivergence(state.JobClass));
        return false;
    }

    [Fact]
    public void Vf16_EveryFixtureJob_IsEitherVerifiedOrDocumentedAsDivergent_AndVerifiedMeansItPasses()
    {
        var jobs = Enumerable.Range(0, 88).Select(JobCase).Select(c => c.State.JobClass).Distinct().ToArray();
        Assert.Equal(44, jobs.Length);
        Assert.All(jobs, job => Assert.True(JobSupport.IsReferenceVerified(job) ^ (JobSupport.KnownDivergence(job) != JobSupport.ParameterRules.None)));
    }

    [Theory]
    [MemberData(nameof(JobCases))]
    public void Vf16_JobStats_MatchReference(int index)
    {
        var (state, b) = JobCase(index);
        if (!VerifiedOrDocumented(state)) return;
        var snapshot = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state);
        var e = snapshot.Effective;
        Assert.Equal(
            [b.GetProperty("str").GetInt32(), b.GetProperty("agi").GetInt32(), b.GetProperty("vit").GetInt32(), b.GetProperty("int").GetInt32(), b.GetProperty("dex").GetInt32(), b.GetProperty("luk").GetInt32()],
            [e.Strength, e.Agility, e.Vitality, e.Intelligence, e.Dexterity, e.Luck]);
        Assert.Equal(Expected(b), DerivedStatCalculator.Calculate(snapshot));
    }

    [Theory]
    [MemberData(nameof(JobCases))]
    public void Vf16_JobMaxHpSp_MatchReference(int index)
    {
        var (state, b) = JobCase(index);
        if (!VerifiedOrDocumented(state)) return;
        var snapshot = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state);
        Assert.Equal((b.GetProperty("max_hp").GetUInt32(), b.GetProperty("max_sp").GetUInt32()),
            (StatusCalculator.MaxHp(state, snapshot.Effective.Vitality), StatusCalculator.MaxSp(state, snapshot.Effective.Intelligence)));
    }

    // ---- VF-17: job- and weapon-dependent attack motion ----

    public static TheoryData<int> AspdCases => new(Enumerable.Range(0, 440));

    [Theory]
    [MemberData(nameof(AspdCases))]
    public void Vf17_AttackMotionPerJobAndWeapon_MatchesReference(int index)
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        var c = fixture.Output("VF-17").GetProperty("cases")[index];
        var i = fixture.Input("VF-17");
        ushort U(string n) => (ushort)i.GetProperty(n).GetInt32();
        var job = (ushort)c.GetProperty("job").GetInt32();
        var state = new CharacterGameplayState(1, 1, job, U("base_level"), U("job_level"), 0, 0, 1, 1, 1, 1, 0, 0, U("str"), U("agi"), U("vit"), U("int"), U("dex"), U("luk"));
        if (!VerifiedOrDocumented(state)) return; // unverified jobs only need their divergence documented (JobSupport)
        var effective = new CharacterStatusEffectState(new FakeTimeProvider()).Capture(() => state).Effective;
        var weaponType = (WeaponType)c.GetProperty("weapon_type").GetInt32();

        if (weaponType is WeaponType.Bow or WeaponType.Musical or WeaponType.Whip)
        {
            // DEX-branch weapons are an explicit boundary: recorded by the reference, rejected (not approximated) here.
            Assert.Throws<NotSupportedException>(() => AttackDelayCalculator.AttackDelayMs(effective, weaponType, job));
            return;
        }

        Assert.Equal(c.GetProperty("adelay").GetInt32(), AttackDelayCalculator.AttackDelayMs(effective, weaponType, job));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Vf17_AspdCap_MatchesReference(int index)
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        var c = fixture.Output("VF-17").GetProperty("cap_cases")[index];
        var state = new CharacterGameplayState(1, 1, (ushort)c.GetProperty("job").GetInt32(), 99, 10, 0, 0, 1, 1, 1, 1, 0, 0, 99, 99, 99, 99, 99, 99);
        var statuses = new CharacterStatusEffectState(new FakeTimeProvider());
        statuses.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 120000, c.GetProperty("increase_agi_level").GetInt32());
        var effective = statuses.Capture(() => state).Effective;

        Assert.Equal((c.GetProperty("agi").GetInt32(), c.GetProperty("dex").GetInt32()), (effective.Agility, effective.Dexterity));
        Assert.Equal(c.GetProperty("adelay").GetInt32(), AttackDelayCalculator.AttackDelayMs(effective, null, state.JobClass));
    }
}
