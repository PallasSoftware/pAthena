using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.GameData.SizeFix;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Gameplay.Rules;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.Tests.World;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.Gameplay.Reference;


// Runs the pinned-rAthena golden fixtures against ONE region's concrete gameplay rules, composed through the production
// factory. The two sealed subclasses give iRO and kRO each their own independently executed set of conformance assertions
// over the same fixture inputs; the fixture values are never produced by the code under test.
public abstract partial class RathenaReferenceConformanceTests(GameplayRegion region)
{
    protected GameplayRuleServices Rules { get; } = TestGameplayRules.Services(region);

    private CharacterStatusEffectState NewStatuses(TimeProvider clock) => new(clock, Rules.StatusEffects, Rules.Stats);











    // VF-01 (aspd part): Novice, all stats 1, unarmed. Compares the reference adelay with Athena.NET.
    [Fact]
    public void Vf01_FreshNovice_AttackDelayMatchesReference()
    {
        var expected = RathenaReferenceFixture.LoadCommitted().Output("VF-01").GetProperty("battle_status").GetProperty("adelay").GetInt32();
        var stats = new EffectiveCharacterStats(1, 1, 1, 1, 1, 1, 0, 0);
        Assert.Equal(expected, Rules.Timing.AttackDelayMs(stats, null, 0));
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
        var state = NewStatuses(clock);
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
            .Select(_ => Rules.Combat.CalculateMonsterBasicAttack(mob, novice))
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

        var effective = NewStatuses(new FakeTimeProvider()).Recalculate(state);

        var expected = c.GetProperty("battle_status");
        Assert.Equal(
            [expected.GetProperty("str").GetInt32(), expected.GetProperty("agi").GetInt32(), expected.GetProperty("vit").GetInt32(),
             expected.GetProperty("int").GetInt32(), expected.GetProperty("dex").GetInt32(), expected.GetProperty("luk").GetInt32()],
            [effective.Strength, effective.Agility, effective.Vitality, effective.Intelligence, effective.Dexterity, effective.Luck]);
    }

    private DerivedStats Expected(System.Text.Json.JsonElement b) => new(
        b.GetProperty("batk").GetInt32(), b.GetProperty("hit").GetInt32(), b.GetProperty("flee").GetInt32(), b.GetProperty("flee2").GetInt32(),
        b.GetProperty("cri").GetInt32(), b.GetProperty("def2").GetInt32(), b.GetProperty("mdef2").GetInt32(), MagicAttackMin: b.GetProperty("matk_min").GetInt32(), MagicAttackMax: b.GetProperty("matk_max").GetInt32());

    private CharacterGameplayState StateFrom(System.Text.Json.JsonElement i)
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
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        Assert.Equal(Expected(c.GetProperty("battle_status")), Rules.Stats.CalculateDerivedStats(snapshot));
    }

    [Fact]
    public void Vf01_FreshNovice_DerivedStatsMatchReference()
    {
        var b = RathenaReferenceFixture.LoadCommitted().Output("VF-01").GetProperty("battle_status");
        var state = new CharacterGameplayState(1, 1, 0, 1, 1, 0, 0, 40, 10, 40, 10, 0, 0, 1, 1, 1, 1, 1, 1);
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        Assert.Equal(Expected(b), Rules.Stats.CalculateDerivedStats(snapshot));
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
        var statuses = NewStatuses(clock);
        var levels = seq.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()).ToArray();
        var steps = seq.GetProperty("steps");
        for (var i = 0; i < levels.Length; i++)
        {
            statuses.Start(statusId, 120000, levels[i]);
            Assert.Equal(Expected(steps[i]), Rules.Stats.CalculateDerivedStats(statuses.Capture(() => state)));
        }
        statuses.ExpireDue(clock.GetUtcNow().AddMilliseconds(120001));
        clock.Advance(TimeSpan.FromMilliseconds(120001));
        statuses.ExpireDue(clock.GetUtcNow());
        Assert.Equal(Expected(seq.GetProperty("after_end")), Rules.Stats.CalculateDerivedStats(statuses.Capture(() => state)));
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
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        Assert.Equal((c.GetProperty("max_hp").GetUInt32(), c.GetProperty("max_sp").GetUInt32()),
            (Rules.Stats.MaxHp(state, snapshot.Effective.Vitality), Rules.Stats.MaxSp(state, snapshot.Effective.Intelligence)));
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
        var after = Rules.Experience.CalculateProgression(before, 0, 0).After;
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

        var after = Rules.Stats.WithRecalculatedResources(before);

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
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
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
        var statuses = NewStatuses(clock);
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

    private void AssertTiming(System.Text.Json.JsonElement expected, CharacterStatusSnapshot snapshot) =>
        Assert.Equal((expected.GetProperty("adelay").GetInt32(), expected.GetProperty("speed").GetInt32(), expected.GetProperty("dmotion").GetInt32()),
            (Rules.Timing.AttackDelayMs(snapshot.Effective, null, snapshot.State.JobClass), Rules.Timing.WalkCellDurationMs(snapshot.Effective.MoveSpeedHaste),
             Rules.Timing.PlayerDamageMotionMs(snapshot.Effective.Agility)));

    // ---- Equipment (VF-05 / VF-06 / VF-15) ----

    private CharacterInventorySnapshot Wearing(IEnumerable<int> itemIds) => new(itemIds.Select((id, slot) => new CharacterInventoryItem(
        DurableId: (uint)slot + 1, SlotIndex: (uint)slot, ItemId: id, Amount: 1, Equip: ((GeneratedItems.ById[id] as IEquippableItemDefinition)!.EquipLocation), Identified: true, 0, 0, 0)).ToArray());

    public static TheoryData<int> LoadoutCases => [0, 1, 2, 3];

    [Theory]
    [MemberData(nameof(LoadoutCases))]
    public void Vf05_EquipmentLoadouts_StatsMatchReference(int index)
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        var loadout = fixture.Output("VF-05").GetProperty("loadouts")[index];
        var state = StateFrom(fixture.Input("VF-05"));
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        Assert.True(Rules.Stats.TryResolveEquipment(Wearing(loadout.GetProperty("items").EnumerateArray().Select(i => i.GetInt32())), GeneratedItems.ById, out var equipment));
        var b = loadout.GetProperty("battle_status");

        var derived = Rules.Stats.CalculateDerivedStats(snapshot, equipment);

        Assert.Equal((b.GetProperty("def").GetInt32(), b.GetProperty("watk").GetInt32()), (derived.HardDefense, equipment.WeaponAttack));
        Assert.Equal(Expected(b) with { HardDefense = derived.HardDefense }, derived);
        Assert.Equal(b.GetProperty("adelay").GetInt32(), Rules.Timing.AttackDelayMs(snapshot.Effective, equipment.Weapon?.WeaponType, snapshot.State.JobClass));
        Assert.Equal(b.GetProperty("range").GetInt32(), BasicAttackRangeResolver.Resolve(equipment.Weapon));
    }

    [Fact]
    public void EquipmentStats_UnknownOrUnsupportedEquippedItem_IsUnresolvedNotZero()
    {
        var unknown = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, 999999, 1, 0x10, true, 0, 0, 0)]);
        Assert.False(Rules.Stats.TryResolveEquipment(unknown, GeneratedItems.ById, out _));
        var unequippedUnknown = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, 999999, 1, 0, true, 0, 0, 0)]);
        Assert.True(Rules.Stats.TryResolveEquipment(unequippedUnknown, GeneratedItems.ById, out var stats));
        Assert.Equal(0, stats.HardDefense);
    }

    // S5 scope boundary: an item mixing a modeled flat bonus (bonus bStr,2;) with an unmodeled
    // bonus2 (bSkillAtk - card/combo/skill-target territory, explicitly deferred) must reject the
    // WHOLE equip, never silently apply just the modeled half - pinned db/re/item_db_equip.yml Id
    // 1427 "Spear_Of_Excellent": `bonus2 bSkillAtk,"SM_MAGNUM",25; bonus bStr,2;`.
    [Fact]
    public void EquipmentStats_ItemMixingModeledAndUnmodeledBonus_IsUnresolvedNotPartiallyApplied()
    {
        const int spearOfExcellent = 1427;
        var equipLocation = ((IEquippableItemDefinition)GeneratedItems.ById[spearOfExcellent]).EquipLocation;
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, spearOfExcellent, 1, equipLocation, true, 0, 0, 0)]);

        Assert.False(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));
        Assert.Equal(default, equipment); // never the bStr,2 half silently applied
    }

    // S5 bonus model: pinned db/re/item_db_equip.yml Id 2601 "Ring" (Both_Accessory, `bonus bStr,2;`) is a real equip
    // item whose flat equip-script bonus must flow through TryResolveEquipment into the character's effective STR,
    // proving the EquipmentBonusResolver integration end to end rather than only at the unit level.
    //
    // EXACT values, not positive-only: pinned status_calc_pc (status.cpp:4290-4301) bakes equipment into base_status
    // BEFORE status_calc_bl_main ever applies a status delta on top - equipment and status effects are NOT two
    // independent additive layers that each read the raw base separately (see IStatRules.ApplyEquipmentBonus's own
    // doc comment for the full trace). This test is the direct regression for that architecture: a character with
    // base STR=1 wearing the Ring (+2 STR) under Blessing (which ALSO changes STR, by +val1) must show
    // Effective.Strength = 1+2+val1 (equipment AND status both present, additively), and the resulting BaseAttack must
    // reflect STR=1+2+val1 throughout - a reintroduction of the original bug (equipment and status computed against
    // two diverging copies of the base, status.cpp:5964's delta math canceling whichever contribution isn't shared)
    // would make this fail by producing BaseAttack as if STR were only 1+val1 (equipment silently dropped) or 1+2
    // (status silently dropped), never both at once.
    [Fact]
    public void EquipmentStats_RealRingWithFlatStrBonus_CombinesAdditivelyWithBlessingInEffectiveStrength()
    {
        const int ring = 2601;
        var equipLocation = ((IEquippableItemDefinition)GeneratedItems.ById[ring]).EquipLocation;
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, ring, 1, equipLocation, true, 0, 0, 0)]);
        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));
        Assert.Equal(new EquipmentParamBonus(Strength: 2, 0, 0, 0, 0, 0), equipment.ParamBonus);

        var state = new CharacterGameplayState(CharacterId: 1, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
            BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10, StatPoints: 0, SkillPoints: 0,
            Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);
        var statuses = NewStatuses(new FakeTimeProvider());

        // No equipment, no status: Effective.Strength is the raw base (1).
        var bare = statuses.Capture(() => state);
        Assert.Equal(1, bare.Effective.Strength);

        // Equipment only (no status yet): Effective.Strength must be 1+2=3 - this is the line that was broken:
        // Capture (and therefore Effective) previously never saw equipment.ParamBonus at all.
        var withEquipmentOnly = statuses.Capture(() => state, equipment.ParamBonus);
        Assert.Equal(3, withEquipmentOnly.Effective.Strength);
        var derivedEquipmentOnly = Rules.Stats.CalculateDerivedStats(withEquipmentOnly, equipment);
        var expectedBatkEquipmentOnly = BaseAtkFormula(level: 1, str: 3, dex: 1, luk: 1);
        Assert.Equal(expectedBatkEquipmentOnly, derivedEquipmentOnly.BaseAttack);

        // Equipment AND Blessing together: Blessing's val2 (== val1 for a PC target) adds to STR on top of the
        // ALREADY-equipment-inclusive base - val1=10 is an arbitrary but fixed choice so val2=10 too.
        statuses.Start(CharacterStatusEffectState.StatusIds.Blessing, durationMilliseconds: 60000, val1: 10);
        var withBoth = statuses.Capture(() => state, equipment.ParamBonus);
        Assert.Equal(1 + 2 + 10, withBoth.Effective.Strength); // base + equipment + status, all three present at once.
        var derivedBoth = Rules.Stats.CalculateDerivedStats(withBoth, equipment);
        // Blessing's val2 also adds to DEX (status.cpp:7059-7061), not just STR/INT - the BaseAtk formula
        // reads DEX too, so the expected value must include that contribution as well.
        var expectedBatkBoth = BaseAtkFormula(level: 1, str: 1 + 2 + 10, dex: 1 + 10, luk: 1);
        Assert.Equal(expectedBatkBoth, derivedBoth.BaseAttack);
    }

    // Pinned status_base_atk, RENEWAL BL_PC non-ranged weapon, POW=0 (status.cpp:2475-2477) - the SAME formula
    // IroStatRules/KroStatRules.BaseAtk implements privately; reproduced here as an independent reference
    // calculation (not a call into the code under test) so this test is real external evidence, not a tautology.
    private static int BaseAtkFormula(int level, int str, int dex, int luk) =>
        Math.Clamp((str * 10 + dex * 10 / 5 + luk * 10 / 3 + level * 10 / 4) / 10, 0, ushort.MaxValue);

    // S6 refine bonus, EXACT values (pinned db/re/refine.yml Group: Weapon, Level 1 RefineLevels:
    // R1=200 R2=400 R3=600 R4=800 R5=1000 R10=2000 R20=6000 - confirmed by direct inspection, not
    // re-derived from the same generated table under test). Pinned status.cpp:3957
    // `wa->atk2 += info->bonus/100` divides the RAW tabulated value by 100 before accumulating - a
    // level-1 Sword (1101, Attack: 25) at +5 contributes exactly 1000/100=10 refine ATK (NOT 1000),
    // for a total WeaponAttack of exactly 25+10=35. This is the direct regression for the
    // originally-reported /100 scaling defect (a raw-accumulation bug would have produced 1025).
    [Theory]
    [InlineData(0, 0)]    // no refine -> no bonus at all (never looked up, refine<=0).
    [InlineData(1, 2)]    // 200/100=2
    [InlineData(2, 4)]    // 400/100=4
    [InlineData(3, 6)]    // 600/100=6
    [InlineData(4, 8)]    // 800/100=8
    [InlineData(5, 10)]   // 1000/100=10 - the report's own reproduction case.
    [InlineData(10, 20)]  // 2000/100=20
    [InlineData(20, 60)]  // 6000/100=60 - MAX_REFINE boundary.
    public void EquipmentStats_RealRefinedWeapon_RefineAtkMatchesExactPinnedScaling(byte refine, int expectedRefineAtk)
    {
        const int sword = 1101; // WeaponLevel: 1, Attack: 25.
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, sword, 1, 0x000002, true, refine, 0, 0)]);

        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));

        Assert.Equal(expectedRefineAtk, equipment.WeaponRefineAtk);
        Assert.Equal(25 + expectedRefineAtk, equipment.WeaponAttack);
    }

    // A1 correction (supersedes the former "Defect 6" test, which was WRONG): the earlier conclusion
    // read status_base_matk_min/max (status.cpp:2559-2591, the StatusMATK term alone) in isolation and
    // missed that status_calc_bl_main's own RENEWAL MATK block (status.cpp:6283-6361) adds WeaponMATK
    // and weapon-level variance on top for BL_PC: matk_min = StatusMATK + wMatk - variance, matk_max =
    // StatusMATK + wMatk + variance, wMatk = rhw.matk (static MagicAttack + refine bonus, non-bow only),
    // variance = rhw.matk * rhw.wlv / 10, PLUS an overrefine widening (matk_min++, matk_max +=
    // overrefine-1) when the weapon's pinned RandomBonus (a deterministic per-level/refine maximum,
    // never an RNG roll - see EquipmentStats's own doc comment) is nonzero. A refined weapon with zero
    // static MagicAttack (Sword 1101) THEREFORE DOES change live player MagicAttackMin/MagicAttackMax -
    // confirmed against VF-23, an independent executable-rAthena scenario (equips this exact Sword at
    // refine 0/5/10 through the real equipment pass of status_calc_pc and the real status_calc_bl_main
    // MATK path, not re-derived from the implementation under test).
    [Theory]
    [InlineData(0, 14, 14)]
    [InlineData(5, 23, 25)]
    [InlineData(10, 33, 44)] // includes the overrefine widening: refine 10's RandomBonus=900 -> overrefine 9.
    public void CalculateDerivedStats_RefinedWeapon_MatchesVf23ReferenceMatkMinMax(int refine, int expectedMin, int expectedMax)
    {
        const int sword = 1101; // WeaponLevel: 1, Attack: 25, MagicAttack: 0 (no explicit field).
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, sword, 1, 0x000002, true, Refine: (byte)refine, 0, 0)]);
        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));

        var state = StateFrom(RathenaReferenceFixture.LoadCommitted().Input("VF-23"));
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        var derived = Rules.Stats.CalculateDerivedStats(snapshot, equipment);

        Assert.Equal(expectedMin, derived.MagicAttackMin);
        Assert.Equal(expectedMax, derived.MagicAttackMax);
    }

    // Equipping nothing at all must still reproduce VF-23's own refine-0 StatusMATK-only baseline
    // (no weapon term applies when EquipmentStats.Weapon is null), confirming the wMatk/variance/
    // overrefine block above is correctly gated on an actual equipped weapon being present.
    [Fact]
    public void CalculateDerivedStats_NoWeaponEquipped_MatchesVf23StatusMatkBaseline()
    {
        var inventory = new CharacterInventorySnapshot([]);
        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));
        Assert.Null(equipment.Weapon);

        var state = StateFrom(RathenaReferenceFixture.LoadCommitted().Input("VF-23"));
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        var derived = Rules.Stats.CalculateDerivedStats(snapshot, equipment);

        Assert.Equal(14, derived.MagicAttackMin);
        Assert.Equal(14, derived.MagicAttackMax);
    }

    // Discriminating rounding-order test (A3): Armor Level 1 Refine {3,7} (300/1000, both already
    // multiples of 100) cannot actually distinguish "round once after summing" from "round per row and
    // sum" - both land on 13 for those two numbers, as the test below this one's own comment already
    // acknowledges. No real pinned item combines a resolvable (plain bStr/bAgi/bVit/bInt/bDex/bLuk-only,
    // non-Gradable) equip script with ArmorLevel 2 at two refine levels whose Bonus values actually
    // diverge under the two rounding orders - item_db_equip.yml's ArmorLevel-2 entries are either
    // Gradable (rejected outright, enchant-grade multiplier unmodeled) or carry unsupported bonus script
    // forms (bSMatk/bCon/skill-indexed bonus2, etc., none of which EquipmentBonusResolver parses). 2LvHat
    // (400360: ArmorLevel 2, Refineable, no Jobs restriction, NO Script at all - resolves trivially with
    // zero ParamBonus) is the real pinned item used here instead, carrying two rows at refine levels
    // whose REAL pinned Armor Level 2 Bonus values (direct inspection of db/re/refine.yml, not re-derived
    // from the table under test) genuinely diverge under the two rounding orders: Refine 1 Bonus=120,
    // Refine 2 Bonus=240. Per-row: (120+50)/100 + (240+50)/100 = 1+2 = 3. Pinned (sum-then-round):
    // (120+240+50)/100 = 410/100 = 4. These differ, so this test fails immediately if the implementation
    // ever regresses to per-row rounding.
    [Fact]
    public void EquipmentStats_MultiPieceArmorRefineDefense_RealDivergingBonusValues_RoundsOnceAfterSummingAllRows()
    {
        const int hat = 400360; // 2LvHat: ArmorLevel 2, Defense 0 (unset), Refineable, no Jobs, no Script.
        var row1 = new CharacterInventoryItem(1, 0, hat, 1, 0x000100, true, Refine: 1, 0, 0);
        var row2 = new CharacterInventoryItem(2, 1, hat, 1, 0x000100, true, Refine: 2, 0, 0);
        var inventory = new CharacterInventorySnapshot([row1, row2]);

        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));
        Assert.Equal(120 + 240, equipment.ArmorRefineDefense);
        Assert.Equal(EquipmentParamBonus.None, equipment.ParamBonus); // no script on this item - confirms it contributes nothing else.

        var state = StateFrom(RathenaReferenceFixture.LoadCommitted().Input("VF-01"));
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        var derived = Rules.Stats.CalculateDerivedStats(snapshot, equipment);

        // Defense is unset on this item (defaults to 0 x2 rows = 0) plus the ONCE-rounded refine sum:
        // (120+240+50)/100 = 4. A per-row-rounded regression would instead compute 0 + (1+2) = 3.
        Assert.Equal(0 + (120 + 240 + 50) / 100, derived.HardDefense);
    }

    // Retained as a basic multi-row-accumulation regression even though 300/1000 cannot discriminate the
    // two rounding orders (see the discriminating test immediately above, which uses real pinned values
    // that do diverge): this still exercises summing across distinct inventory rows of the SAME item id,
    // independent of slot-exclusivity, which the test above does not (its two rows occupy the same
    // Head_Top bit so would conflict at actual equip time - TryResolveEquipment itself does not enforce
    // slot exclusivity, that is CharacterEquipmentMutationService's own concern).
    [Fact]
    public void EquipmentStats_MultiPieceArmorRefineDefense_RoundsOnceAfterSummingAllRows()
    {
        const int cottonShirtSlotted = 2302; // ArmorLevel default 1 (no explicit field), Defense: 10, Slots: 1.
        // TryResolveEquipment sums every row with Equip != 0 independently of slot-exclusivity
        // (that is CharacterEquipmentMutationService's own concern at equip time) - two rows of the
        // same real armor item on two different inventory slots validly exercise the "sum across
        // rows, round once" accumulation this test targets.
        var row1 = new CharacterInventoryItem(1, 0, cottonShirtSlotted, 1, 0x000010, true, Refine: 3, 0, 0);
        var row2 = new CharacterInventoryItem(2, 1, cottonShirtSlotted, 1, 0x000010, true, Refine: 7, 0, 0);
        var inventory = new CharacterInventorySnapshot([row1, row2]);

        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));
        Assert.Equal(300 + 1000, equipment.ArmorRefineDefense);

        var state = StateFrom(RathenaReferenceFixture.LoadCommitted().Input("VF-01"));
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        var derived = Rules.Stats.CalculateDerivedStats(snapshot, equipment);

        // (refinedef+50)/100 applied ONCE to the SUM (1300+50)/100=13, truncated - plus the summed
        // Defense (10+10=20) from both rows. Never two independently-rounded per-row contributions.
        Assert.Equal(20 + (300 + 1000 + 50) / 100, derived.HardDefense);
    }

    // A3 "legitimately equippable combination across distinct slots": Cotton_Shirt (2301, Armor slot,
    // Defense 10, ArmorLevel 1, no script) and Sandals (2401, Shoes slot, Defense 5, ArmorLevel 1, no
    // script) worn together, each refined differently - a combination an actual character could equip
    // simultaneously (distinct EquipLocation bits, no slot conflict), unlike the two rounding tests
    // above which deliberately double up the SAME slot to isolate the accumulation math.
    [Fact]
    public void EquipmentStats_CottonShirtAndSandalsOnDistinctSlots_SumsBothRowsHardDefense()
    {
        const int cottonShirt = 2301;
        const int sandals = 2401;
        var armorRow = new CharacterInventoryItem(1, 0, cottonShirt, 1, 0x000010, true, Refine: 4, 0, 0); // Armor slot, Bonus 400.
        var shoesRow = new CharacterInventoryItem(2, 1, sandals, 1, 0x000040, true, Refine: 6, 0, 0); // Shoes slot, Bonus 800.
        var inventory = new CharacterInventorySnapshot([armorRow, shoesRow]);

        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));
        Assert.Equal(10 + 5, equipment.HardDefense); // Cotton_Shirt Defense 10 + Sandals Defense 5.
        Assert.Equal(400 + 800, equipment.ArmorRefineDefense); // pinned Armor Level 1 Refine 4=400, Refine 6=800.

        var state = StateFrom(RathenaReferenceFixture.LoadCommitted().Input("VF-01"));
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        var derived = Rules.Stats.CalculateDerivedStats(snapshot, equipment);

        Assert.Equal(15 + (400 + 800 + 50) / 100, derived.HardDefense);
    }

    // S6 enchant grade (supersedes the former rejection test): EnchantGrade is this project's own
    // 0-indexed e_enchantgrade byte (0=None..4=A, matching ItemGrantResolver's MaxEnchantGrade=4) -
    // GeneratedEnchantGradeBonus.Grades indexes it directly. A graded Sword (1101, WeaponLevel 1) resolves
    // successfully with NO grade term added, because enchant grade's real pinned data only tabulates
    // Weapon Level 5 / Armor Level 2 at all (status.cpp:3961's own enchantgrade_info == nullptr case for
    // every other level) - this is the correct behavior, not a silently-dropped bonus.
    [Fact]
    public void EquipmentStats_RefinedWeaponAtAnUntabulatedLevel_WithEnchantGrade_ResolvesWithNoGradeTerm()
    {
        const int sword = 1101; // WeaponLevel 1 - not tabulated in enchantgrade.yml at all.
        var graded = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, sword, 1, 0x000002, true, Refine: 5, 0, 0,
            Detail: ItemInstanceDetail.Empty with { EnchantGrade = 3 /* B */, Revision = 0 })]); // pinned Weapon Level 1 Refine 5 Bonus=1000.

        Assert.True(Rules.Stats.TryResolveEquipment(graded, GeneratedItems.ById, out var equipment));
        Assert.Equal(10, equipment.WeaponRefineAtk); // 1000/100=10, no grade term added at an untabulated level.
    }

    // Pinned status.cpp:3961: `wa->atk2 += ((info->bonus/100) * enchantgrade_info->bonus) / 100` - the grade
    // multiplier is ADDED on top of (not multiplied into) the plain refine bonus, using Weapon Level 5's real
    // pinned Refine 1 Bonus=800 and Grade B's real pinned Bonus=100 (both direct inspection, not re-derived
    // from the generated tables under test).
    [Fact]
    public void EquipmentStats_RealGradedWeaponAtItsOwnTabulatedLevel_AddsTheGradeMultiplierOnTopOfRefine()
    {
        const int axe = 28144; // 5LvBattle_Axe: WeaponLevel 5, Refineable, no script.
        var graded = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, axe, 1, 0x000002, true, Refine: 1, 0, 0,
            Detail: ItemInstanceDetail.Empty with { EnchantGrade = 3 /* B */, Revision = 0 })]);
        var ungraded = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, axe, 1, 0x000002, true, Refine: 1, 0, 0)]);

        Assert.True(Rules.Stats.TryResolveEquipment(graded, GeneratedItems.ById, out var gradedEquipment));
        Assert.True(Rules.Stats.TryResolveEquipment(ungraded, GeneratedItems.ById, out var ungradedEquipment));

        // Plain refine alone: 800/100 = 8. With Grade B (Bonus 100): 8 + (100 * 8 / 100) = 8 + 8 = 16.
        Assert.Equal(8, ungradedEquipment.WeaponRefineAtk);
        Assert.Equal(16, gradedEquipment.WeaponRefineAtk);
    }

    // Grade "None" (EnchantGrade=0) has a real pinned Bonus of 10 at Weapon Level 5 - small enough that the
    // grade term's own `* bonus / 100` integer division truncates to 0 for a low refine bonus, proving the
    // pinned formula's own truncation (not a bug) rather than a nonzero grade silently contributing nothing
    // due to a sign/order mistake in this project's own implementation.
    [Fact]
    public void EquipmentStats_RealGradedWeapon_GradeNoneBonusTruncatesToZeroAtLowRefine()
    {
        const int axe = 28144;
        var graded = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, axe, 1, 0x000002, true, Refine: 1, 0, 0,
            Detail: ItemInstanceDetail.Empty with { EnchantGrade = 0 /* None */, Revision = 0 })]);

        Assert.True(Rules.Stats.TryResolveEquipment(graded, GeneratedItems.ById, out var equipment));
        Assert.Equal(8, equipment.WeaponRefineAtk); // 8 + (10*8/100 = 0) = 8.
    }

    // A grade byte beyond the pinned enum's own range (ENCHANTGRADE_A=4 is the maximum) is a data/generation
    // invariant violation, not a legitimate persisted state - rejected explicitly rather than throwing an
    // IndexOutOfRangeException from GeneratedEnchantGradeBonus.Grades.
    [Fact]
    public void EquipmentStats_EnchantGradeBeyondA_IsRejected_NeverThrows()
    {
        const int axe = 28144;
        var invalid = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, axe, 1, 0x000002, true, Refine: 1, 0, 0,
            Detail: ItemInstanceDetail.Empty with { EnchantGrade = 5, Revision = 0 })]);

        Assert.False(Rules.Stats.TryResolveEquipment(invalid, GeneratedItems.ById, out var equipment));
        Assert.Equal(default, equipment);
    }

    // A4: equipped ammo (routine, common gameplay for several classes - archers equip arrows constantly) must
    // NOT discard every other equipped item's contribution. Real pinned Arrow (1750, Type: Ammo, no script) worn
    // alongside a refined Cotton_Shirt proves the armor's hard DEF/refine bonus still resolves correctly with
    // ammo equipped - before this fix, TryResolveEquipment's switch had no AmmoItemDefinition case at all, so it
    // fell to `default: return false`, zeroing the Cotton_Shirt's contribution too.
    [Fact]
    public void EquipmentStats_AmmoEquippedAlongsideArmor_DoesNotDiscardArmorContribution()
    {
        const int arrow = 1750;
        const int cottonShirt = 2301;
        var armorRow = new CharacterInventoryItem(1, 0, cottonShirt, 1, 0x000010, true, Refine: 4, 0, 0);
        var ammoRow = new CharacterInventoryItem(2, 1, arrow, 1, 0x008000, true, 0, 0, 0);
        var inventory = new CharacterInventorySnapshot([armorRow, ammoRow]);

        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));
        Assert.Equal(10, equipment.HardDefense); // Cotton_Shirt's Defense still counted.
        Assert.Equal(400, equipment.ArmorRefineDefense); // pinned Armor Level 1 Refine 4=400.
    }

    // A4: a left-hand (dual-wield) weapon - a real, currently-reachable state via
    // CharacterEquipmentMutationService's own EQP_ARMS logic - is a known, unmodeled contribution (no
    // left-hand/dual-wield combat support in this project yet, see BasicAttackRangeResolver's own doc comment),
    // not an unresolvable one. It must not discard the right-hand weapon's own refine contribution.
    [Fact]
    public void EquipmentStats_LeftHandWeaponAlongsideRightHandWeapon_DoesNotDiscardRightHandContribution()
    {
        const int sword = 1101;
        var rightHand = new CharacterInventoryItem(1, 0, sword, 1, 0x000002, true, Refine: 5, 0, 0);
        var leftHand = new CharacterInventoryItem(2, 1, sword, 1, 0x000020, true, 0, 0, 0); // EQI_HAND_L bit only.
        var inventory = new CharacterInventorySnapshot([rightHand, leftHand]);

        Assert.True(Rules.Stats.TryResolveEquipment(inventory, GeneratedItems.ById, out var equipment));
        Assert.NotNull(equipment.Weapon); // the right-hand sword still resolves as THE weapon.
        Assert.Equal(1000, equipment.WeaponRefineAtk * 100); // pinned Weapon Level 1 Refine 5 Bonus=1000, still counted.
    }

    // A4: shadow gear's static Defense is modeled data (ShadowGearItemDefinition.Defense, documented to feed hard
    // DEF like armor) that TryResolveEquipment's switch previously had no case for at all, discarding it AND every
    // other equipped item's contribution. No real pinned ShadowGear item has a resolvable equip script (every one
    // in db/re/item_db_equip.yml uses bMaxHP/bSkillAtk/bSkillUseSPrate forms EquipmentBonusResolver does not
    // parse), so this one assertion uses a minimal synthetic fixture - documented here, not fabricated to dodge a
    // real counterexample that does not exist in the pinned data.
    [Fact]
    public void EquipmentStats_ShadowGearDefense_CountsTowardHardDefense_SyntheticFixture()
    {
        var shadowGear = new ShadowGearItemDefinition(90001, "Synthetic_Shadow_Armor", "Synthetic Shadow Armor",
            Stackable: false, ClientViewId: 0, new WorldSourceInfo("synthetic", "n/a", "n/a", 0), Defense: 7, EquipLocation: 0x000010);
        var items = new Dictionary<int, ItemDefinition>(GeneratedItems.ById) { [shadowGear.Id] = shadowGear };
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(1, 0, shadowGear.Id, 1, 0x000010, true, 0, 0, 0)]);

        Assert.True(Rules.Stats.TryResolveEquipment(inventory, items, out var equipment));
        Assert.Equal(7, equipment.HardDefense);
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
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);

        var observed = Enumerable.Range(0, 20000)
            .Select(_ => Rules.Combat.CalculatePlayerBasicAttack(new BasicAttackContext(snapshot.Effective, state.BaseLevel, GeneratedItems.Knife, mob)))
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
            .Select(_ => Rules.Combat.CalculateMonsterBasicAttack(mob, novice))
            .Where(r => !r.IsMiss).Select(r => (long)r.Damage).Distinct().Order().ToArray();

        Assert.Equal(expected, observed);
    }



    // ---- VF-16: job coverage ----

    public static TheoryData<int> JobCases => new(Enumerable.Range(0, 88));

    private (CharacterGameplayState State, System.Text.Json.JsonElement Battle) JobCase(int index)
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
    private bool VerifiedOrDocumented(CharacterGameplayState state)
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
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        var e = snapshot.Effective;
        Assert.Equal(
            [b.GetProperty("str").GetInt32(), b.GetProperty("agi").GetInt32(), b.GetProperty("vit").GetInt32(), b.GetProperty("int").GetInt32(), b.GetProperty("dex").GetInt32(), b.GetProperty("luk").GetInt32()],
            [e.Strength, e.Agility, e.Vitality, e.Intelligence, e.Dexterity, e.Luck]);
        Assert.Equal(Expected(b), Rules.Stats.CalculateDerivedStats(snapshot));
    }

    [Theory]
    [MemberData(nameof(JobCases))]
    public void Vf16_JobMaxHpSp_MatchReference(int index)
    {
        var (state, b) = JobCase(index);
        if (!VerifiedOrDocumented(state)) return;
        var snapshot = NewStatuses(new FakeTimeProvider()).Capture(() => state);
        Assert.Equal((b.GetProperty("max_hp").GetUInt32(), b.GetProperty("max_sp").GetUInt32()),
            (Rules.Stats.MaxHp(state, snapshot.Effective.Vitality), Rules.Stats.MaxSp(state, snapshot.Effective.Intelligence)));
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
        var effective = NewStatuses(new FakeTimeProvider()).Capture(() => state).Effective;
        var weaponType = (WeaponType)c.GetProperty("weapon_type").GetInt32();

        if (weaponType is WeaponType.Bow or WeaponType.Musical or WeaponType.Whip)
        {
            // DEX-branch weapons are an explicit boundary: recorded by the reference, rejected (not approximated) here.
            Assert.Throws<NotSupportedException>(() => Rules.Timing.AttackDelayMs(effective, weaponType, job));
            return;
        }

        Assert.Equal(c.GetProperty("adelay").GetInt32(), Rules.Timing.AttackDelayMs(effective, weaponType, job));
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
        var statuses = NewStatuses(new FakeTimeProvider());
        statuses.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 120000, c.GetProperty("increase_agi_level").GetInt32());
        var effective = statuses.Capture(() => state).Effective;

        Assert.Equal((c.GetProperty("agi").GetInt32(), c.GetProperty("dex").GetInt32()), (effective.Agility, effective.Dexterity));
        Assert.Equal(c.GetProperty("adelay").GetInt32(), Rules.Timing.AttackDelayMs(effective, null, state.JobClass));
    }
}

public sealed class IroRathenaReferenceConformanceTests() : RathenaReferenceConformanceTests(GameplayRegion.Iro);

public sealed class KroRathenaReferenceConformanceTests() : RathenaReferenceConformanceTests(GameplayRegion.Kro);

// Fixture structure/provenance validation (region-independent).
public sealed class RathenaReferenceFixtureValidationTests
{
    private static string Valid(string evidence = "ReferenceExecuted", string commit = RathenaReferenceFixture.PinnedCommit) => $$$"""
        {"schema":1,"reference":{"commit":"{{{commit}}}","compiler":"g++","runner":"r","configuration":"c"},
         "scenarios":[{"id":"X","evidence":"{{{evidence}}}","scope":"s","input":{},"output":{}}]}
        """;
    [Fact]
    public void CommittedFixture_IsValidAndAllReferenceExecuted()
    {
        var fixture = RathenaReferenceFixture.LoadCommitted();
        Assert.Equal(["VF-01", "VF-02", "VF-04", "VF-05", "VF-06", "VF-07", "VF-08", "VF-11", "VF-14", "VF-15", "VF-16", "VF-17", "VF-20", "VF-21", "VF-22", "VF-23"], fixture.Ids.Order());
        Assert.All(fixture.Ids, id => Assert.Equal("ReferenceExecuted", fixture.Evidence(id)));
    }
    [Fact] public void Parse_AcceptsWellFormedFixture() => RathenaReferenceFixture.Parse(Valid());
    [Fact] public void Parse_RejectsWrongCommit() => Assert.Throws<InvalidDataException>(() => RathenaReferenceFixture.Parse(Valid(commit: "abc")));
    [Fact] public void Parse_RejectsUnknownEvidence() => Assert.Throws<InvalidDataException>(() => RathenaReferenceFixture.Parse(Valid("Guessed")));
    [Fact] public void Parse_RejectsMissingProvenance() => Assert.Throws<InvalidDataException>(() => RathenaReferenceFixture.Parse(Valid().Replace("\"compiler\":\"g++\",", "")));
    [Fact] public void Parse_RejectsNoScenarios() => Assert.Throws<InvalidDataException>(() => RathenaReferenceFixture.Parse(
        $$"""{"schema":1,"reference":{"commit":"{{RathenaReferenceFixture.PinnedCommit}}","compiler":"g","runner":"r","configuration":"c"},"scenarios":[]}"""));
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
}
