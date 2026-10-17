using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Tests.World;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules;

// Both regions initially reproduce the same pinned-rAthena Renewal behavior. These tests compare the independent iRO and
// kRO concrete implementations directly over seeded input grids (the reference-fixture conformance tests separately prove
// each of them against the executed rAthena fixtures). When a verified regional difference is introduced, the matching
// parity assertion here is deliberately changed or removed for that mechanic only.
public sealed class RegionalRulesParityTests
{
    private static readonly GameplayRuleServices Iro = TestGameplayRules.Services(GameplayRegion.Iro);
    private static readonly GameplayRuleServices Kro = TestGameplayRules.Services(GameplayRegion.Kro);

    private static readonly JobClass[] Jobs = [JobClass.Novice, JobClass.Swordman, JobClass.Mage, JobClass.Archer, JobClass.Acolyte, JobClass.Merchant, JobClass.Thief,
        JobClass.Knight, JobClass.Wizard, JobClass.Hunter, JobClass.Taekwon, JobClass.RuneKnight, JobClass.Summoner, JobClass.NoviceHigh, JobClass.DragonKnight];

    private static CharacterGameplayState RandomState(Random random)
    {
        var job = Jobs[random.Next(Jobs.Length)];
        var definition = GeneratedProgressionRegistry.Get((ushort)job);
        ushort Stat() => (ushort)random.Next(1, Math.Min((int)definition.MaxBaseStat, 99) + 1);
        var baseLevel = (ushort)random.Next(1, Math.Min((int)definition.MaxBaseLevel, 99) + 1);
        var jobLevel = (ushort)random.Next(1, Math.Min((int)definition.MaxJobLevel, 50) + 1);
        return new CharacterGameplayState(1, 1, (ushort)job, baseLevel, jobLevel, 0, 0, (uint)random.Next(1, 500), (uint)random.Next(1, 100), 500, 100,
            (uint)random.Next(0, 2000), 3, Stat(), Stat(), Stat(), Stat(), Stat(), Stat());
    }

    private static CharacterStatusSnapshot Snapshot(GameplayRuleServices rules, CharacterGameplayState state, int blessing, int incAgi)
    {
        var statuses = new CharacterStatusEffectState(new FakeTimeProvider(), rules.StatusEffects, rules.Stats);
        if (blessing > 0) statuses.Start(CharacterStatusEffectState.StatusIds.Blessing, 120000, blessing);
        if (incAgi > 0) statuses.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 120000, incAgi);
        return statuses.Capture(() => state);
    }

    [Fact]
    public void Stats_PrimaryDerivedResourcesAndAllocation_AreIdentical()
    {
        var random = new Random(150);
        for (var i = 0; i < 400; i++)
        {
            var state = RandomState(random);
            int blessing = random.Next(0, 11), incAgi = random.Next(0, 11), hardDef = random.Next(0, 80), amount = 1 + random.Next(0, 3);
            var iroSnapshot = Snapshot(Iro, state, blessing, incAgi);
            var kroSnapshot = Snapshot(Kro, state, blessing, incAgi);
            var equipment = new EquipmentStats(hardDef, i % 2 == 0 ? GeneratedItems.Knife : null);

            Assert.Equal(iroSnapshot.Effective, kroSnapshot.Effective);
            Assert.Equal(Iro.Stats.BasePrimaryStats(state), Kro.Stats.BasePrimaryStats(state));
            Assert.Equal(Iro.Stats.CalculateDerivedStats(iroSnapshot, equipment), Kro.Stats.CalculateDerivedStats(kroSnapshot, equipment));
            Assert.Equal(Iro.Stats.MaxHp(state, state.Vitality), Kro.Stats.MaxHp(state, state.Vitality));
            Assert.Equal(Iro.Stats.MaxSp(state, state.Intelligence), Kro.Stats.MaxSp(state, state.Intelligence));
            Assert.Equal(Iro.Stats.WithRecalculatedResources(state), Kro.Stats.WithRecalculatedResources(state));
            foreach (var stat in Enum.GetValues<CharacterBaseStat>())
                Assert.Equal(Iro.Stats.ValidateStatIncrease(state, stat, amount), Kro.Stats.ValidateStatIncrease(state, stat, amount));
        }
    }

    [Fact]
    public void Timing_AttackDelayMotionWalkAndDamageMotion_AreIdentical()
    {
        var random = new Random(151);
        var weapons = new WeaponType?[] { null, WeaponType.Fist, WeaponType.Dagger, WeaponType.OneHandSword, WeaponType.Mace, WeaponType.Staff, WeaponType.Knuckle };
        for (var i = 0; i < 600; i++)
        {
            var state = RandomState(random);
            var weapon = weapons[random.Next(weapons.Length)];
            var iroSnapshot = Snapshot(Iro, state, random.Next(0, 11), random.Next(0, 11));
            var delay = Iro.Timing.AttackDelayMs(iroSnapshot.Effective, weapon, state.JobClass);

            Assert.Equal(delay, Kro.Timing.AttackDelayMs(iroSnapshot.Effective, weapon, state.JobClass));
            Assert.Equal(Iro.Timing.AttackMotionMs(delay), Kro.Timing.AttackMotionMs(delay));
            Assert.Equal(Iro.Timing.WalkCellDurationMs(iroSnapshot.Effective.MoveSpeedHaste), Kro.Timing.WalkCellDurationMs(iroSnapshot.Effective.MoveSpeedHaste));
            Assert.Equal(Iro.Timing.PlayerDamageMotionMs(iroSnapshot.Effective.Agility), Kro.Timing.PlayerDamageMotionMs(iroSnapshot.Effective.Agility));
        }
        foreach (var weapon in new[] { WeaponType.Bow, WeaponType.Musical, WeaponType.Whip, WeaponType.Rifle }) // same explicit boundary in both
        {
            var stats = new EffectiveCharacterStats(9, 9, 9, 9, 9, 9, 0, 0);
            Assert.Throws<NotSupportedException>(() => Iro.Timing.AttackDelayMs(stats, weapon, 0));
            Assert.Throws<NotSupportedException>(() => Kro.Timing.AttackDelayMs(stats, weapon, 0));
        }
    }

    [Fact]
    public void StatusEffects_CreateRetainAndApply_AreIdentical()
    {
        var now = DateTimeOffset.UnixEpoch;
        foreach (var id in new[] { CharacterStatusEffectState.StatusIds.Blessing, CharacterStatusEffectState.StatusIds.IncreaseAgi })
            for (var a = 1; a <= 10; a++)
                for (var b = 1; b <= 10; b++)
                {
                    var iroStatus = Iro.StatusEffects.CreateStatus(id, a, 0, 0, now.AddSeconds(60));
                    Assert.Equal(iroStatus, Kro.StatusEffects.CreateStatus(id, a, 0, 0, now.AddSeconds(60)));
                    Assert.Equal(Iro.StatusEffects.RetainsActiveOverNewStart(iroStatus, now, b), Kro.StatusEffects.RetainsActiveOverNewStart(iroStatus, now, b));
                    Assert.Equal(Iro.StatusEffects.RetainsActiveOverNewStart(iroStatus, now.AddSeconds(61), b), Kro.StatusEffects.RetainsActiveOverNewStart(iroStatus, now.AddSeconds(61), b));
                }
    }

    [Fact]
    public void Combat_PlayerAndMonsterBasicAttacks_AreIdentical_ForEveryDeterministicRoll()
    {
        var mobs = new[] { GeneratedMobs.Poring, GeneratedMobs.Fabre, GeneratedMobs.Wolf, GeneratedMobs.Poporing, GeneratedMobs.GPoring };
        var random = new Random(152);
        for (var i = 0; i < 400; i++)
        {
            var mob = mobs[random.Next(mobs.Length)];
            var stats = new EffectiveCharacterStats((ushort)random.Next(1, 130), (ushort)random.Next(1, 130), (ushort)random.Next(1, 130), (ushort)random.Next(1, 130), (ushort)random.Next(1, 130), (ushort)random.Next(1, 130), 0, 0);
            var weapon = i % 3 == 0 ? null : GeneratedItems.Knife;
            Func<int, int, int> pick = random.Next(3) switch { 0 => (min, _) => min, 1 => (_, max) => max, _ => (min, max) => (min + max) / 2 };
            var context = new BasicAttackContext(stats, (ushort)random.Next(1, 100), weapon, mob);
            var snapshot = new PlayerCombatSnapshot(1, "prontera", 0, 0, true, false, (ushort)random.Next(1, 100), stats.Vitality, stats.Agility, random.Next(0, 90), random.Next(0, 60));

            Assert.Equal(TestGameplayRules.Combat(pick, region: GameplayRegion.Iro).CalculatePlayerBasicAttack(context),
                         TestGameplayRules.Combat(pick, region: GameplayRegion.Kro).CalculatePlayerBasicAttack(context));
            Assert.Equal(TestGameplayRules.Combat(rollMonsterAtk: pick, region: GameplayRegion.Iro).CalculateMonsterBasicAttack(mob, snapshot),
                         TestGameplayRules.Combat(rollMonsterAtk: pick, region: GameplayRegion.Kro).CalculateMonsterBasicAttack(mob, snapshot));
        }
    }

    [Fact]
    public void Experience_Progression_IsIdentical()
    {
        var random = new Random(153);
        for (var i = 0; i < 300; i++)
        {
            var state = RandomState(random);
            var definition = GeneratedProgressionRegistry.Get(state.JobClass);
            state = state with { BaseExperience = (ulong)random.Next(0, 200), JobExperience = (ulong)random.Next(0, 100) };
            var baseAward = (ulong)(definition.BaseExperienceToNext[Math.Min((int)state.BaseLevel, definition.BaseExperienceToNext.Count - 1)] * (ulong)random.Next(0, 3));
            var jobAward = (ulong)random.Next(0, 5000);

            Assert.Equal(Iro.Experience.CalculateProgression(state, baseAward, jobAward), Kro.Experience.CalculateProgression(state, baseAward, jobAward));
        }
    }
}
