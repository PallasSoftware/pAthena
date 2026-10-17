using Athena.Net.MapServer.Tests.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Minimal deterministic TimeProvider fake: no real clock, no timers, advanced only
// by explicit test calls. Avoids adding a new package dependency for one test file.
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}

public abstract class StatusEffectRulesTests(GameplayRegion region)
{
    private GameplayRuleServices Rules { get; } = TestGameplayRules.Services(region);

    [Fact]
    public void BlessingAddsLevelToStrIntDex()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10);

        var effective = state.Recalculate(BaseState());

        Assert.Equal((ushort)(BaseState().Strength + 10), effective.Strength);
        Assert.Equal((ushort)(BaseState().Intelligence + 10), effective.Intelligence);
        Assert.Equal((ushort)(BaseState().Dexterity + 10), effective.Dexterity);
        Assert.Equal(BaseState().Agility, effective.Agility);
        Assert.Equal(BaseState().Vitality, effective.Vitality);
        Assert.Equal(BaseState().Luck, effective.Luck);
    }

    [Fact]
    public void BlessingDoesNotMutateBasePersistedStats()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10);
        var baseState = BaseState();

        state.Recalculate(baseState);

        Assert.Equal(BaseState(), baseState);
    }

    [Fact]
    public void IncreaseAgiAddsTwoPlusLevelToAgiAndGrantsMoveHasteAndAspdBonus()
    {
        // Pinned legacy/rathena/src/map/status.cpp:10844-10854 (status_change_start_post_delay's
        // val-settings switch) sets val2 = 2 + val1 for SC_INCREASEAGI regardless of caller
        // (script sc_start or the real AL_INCAGI skill cast both pass val2=0 and let this
        // switch compute it); status_calc_agi (status.cpp:6843-6844) adds that val2 to AGI.
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        state.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 240000, 10);

        var effective = state.Recalculate(BaseState());

        Assert.Equal((ushort)(BaseState().Agility + 12), effective.Agility);
        Assert.Equal(25, effective.MoveSpeedHaste);
        Assert.Equal(10, effective.AttackSpeedBonus);
    }

    [Fact]
    public void ReapplyingAStrongerOrEqualStatusOverwritesItsValuesAndDurationRatherThanStacking()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        Assert.Equal(StatusStartOutcome.Started, state.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 3));
        Assert.Equal(StatusStartOutcome.Replaced, state.Start(CharacterStatusEffectState.StatusIds.Blessing, 100000, 10));

        var effective = state.Recalculate(BaseState());

        Assert.Equal((ushort)(BaseState().Strength + 10), effective.Strength);
        clock.Advance(TimeSpan.FromMilliseconds(100001));
        Assert.False(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
    }

    // Pinned status_change_start default overlap branch (status.cpp:10803): an active higher val1 wins and the
    // weaker start is a no-op - level, value and deadline stay (fixture VF-07 [10,3]).
    [Fact]
    public void WeakerStartKeepsStrongerActiveStatusAndItsDeadline()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10);
        Assert.Equal(StatusStartOutcome.Retained, state.Start(CharacterStatusEffectState.StatusIds.Blessing, 100000, 3));

        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var status));
        Assert.Equal((10, 10), (status.Val1, status.Val2));
        clock.Advance(TimeSpan.FromMilliseconds(239999));
        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
    }

    [Fact]
    public void WeakerStartReplacesAnAlreadyExpiredStrongerEntry()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 1000, 10);
        clock.Advance(TimeSpan.FromMilliseconds(1001)); // expired but not yet swept by ExpireDue
        Assert.Equal(StatusStartOutcome.Replaced, state.Start(CharacterStatusEffectState.StatusIds.Blessing, 1000, 3));
        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var status));
        Assert.Equal(3, status.Val1);
    }

    // Capture must return a real instant. The writer always publishes state N (Strength = N) BEFORE starting Blessing
    // level N, so Blessing's level can never exceed the state's Strength at any instant; a "read state, then read
    // statuses" capture could observe state N-1 with Blessing N and violate that. 
    [Fact]
    public async Task Capture_NeverMixesAnOlderStateWithANewerStatus()
    {
        var state = new CharacterStatusEffectState(new FakeTimeProvider(), Rules.StatusEffects, Rules.Stats);
        var published = BaseState() with { Strength = 1 };
        var stop = false;
        var writer = Task.Run(() =>
        {
            for (ushort n = 2; n < 20000 && !Volatile.Read(ref stop); n++)
            {
                Volatile.Write(ref published, published with { Strength = n });
                state.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, n);
            }
        });
        var violations = 0;
        for (var i = 0; i < 200000 && !writer.IsCompleted; i++)
        {
            var snapshot = state.Capture(() => Volatile.Read(ref published));
            var blessing = snapshot.Effective.Strength - snapshot.State.Strength;
            if (blessing > snapshot.State.Strength) violations++;
        }
        Volatile.Write(ref stop, true);
        await writer;
        Assert.Equal(0, violations);
    }

    [Fact]
    public void StatusExpiresAfterItsDuration()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 1000, 10);

        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.False(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));

        var effective = state.Recalculate(BaseState());
        Assert.Equal(BaseState().Strength, effective.Strength);
    }

    [Fact]
    public void MultipleActiveStatusesExpireIndependently()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 1000, 10);
        state.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 5000, 10);

        clock.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.False(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out _));
        Assert.Single(state.ActiveStatuses);

        var effective = state.Recalculate(BaseState());
        Assert.Equal(BaseState().Strength, effective.Strength);
        Assert.Equal(25, effective.MoveSpeedHaste);
    }

    [Fact]
    public void EachStatusStateInstanceIsIndependent()
    {
        var clock = new FakeTimeProvider();
        var sessionOne = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        var sessionTwo = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);

        sessionOne.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10);

        Assert.True(sessionOne.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.False(sessionTwo.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));

        var effectiveTwo = sessionTwo.Recalculate(BaseState());
        Assert.Equal(BaseState().Strength, effectiveTwo.Strength);
    }

    [Fact]
    public void NonPositiveDurationIsRejected()
    {
        var state = new CharacterStatusEffectState(new FakeTimeProvider(), Rules.StatusEffects, Rules.Stats);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Start(CharacterStatusEffectState.StatusIds.Blessing, 0, 10));
    }

    [Fact]
    public void ExpireDueRemovesAndReturnsOnlyStatusesAtOrBeforeNow()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 1000, 10);
        state.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 5000, 10);

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var due = state.ExpireDue(clock.GetUtcNow());

        Assert.Equal(CharacterStatusEffectState.StatusIds.Blessing, Assert.Single(due).StatusId);
        Assert.False(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out _));
    }

    [Fact]
    public void NextExpirationReflectsEarliestActiveDeadline()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock, Rules.StatusEffects, Rules.Stats);
        Assert.Null(state.NextExpiration);

        state.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 5000, 10);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 1000, 10);

        Assert.Equal(clock.GetUtcNow().AddMilliseconds(1000), state.NextExpiration);

        state.ExpireDue(clock.GetUtcNow().AddMilliseconds(1000));
        Assert.Equal(clock.GetUtcNow().AddMilliseconds(5000), state.NextExpiration);
    }

    private static CharacterGameplayState BaseState() => new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 2, 3, 4, 5, 6);

    // Regression test for the CI race: RunStatusExpirationLoopAsync repeatedly reads
    // NextExpiration/ExpireDue on a background task while generated NPC scripts concurrently
    // call Start on other async paths of the same session - both against the same
    // CharacterStatusEffectState instance. Uses TimeProvider.System (real time) with genuinely
    // parallel Task.Run workers, since a shared FakeTimeProvider gives no real interleaving
    // across threads. Before the CharacterStatusEffectState fix, this reliably threw
    // InvalidOperationException ("Collection was modified") within a few hundred milliseconds.
    [Fact]
    public async Task ConcurrentStartAndSchedulerReads_NeverThrowsCollectionModified()
    {
        var state = new CharacterStatusEffectState(TimeProvider.System, Rules.StatusEffects, Rules.Stats);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var statusId = i % 2 == 0 ? CharacterStatusEffectState.StatusIds.Blessing : CharacterStatusEffectState.StatusIds.IncreaseAgi;
                    state.Start(statusId, 5, 10);
                }
            }
            catch (Exception ex) { exceptions.Add(ex); }
        }));

        var readers = Enumerable.Range(0, 4).Select(readerIndex => Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    _ = state.NextExpiration;
                    _ = state.ExpireDue(TimeProvider.System.GetUtcNow());
                    _ = state.ActiveStatuses;
                    _ = state.Recalculate(BaseState());
                    _ = state.RecalculateBeforeExpiration(BaseState());
                    _ = state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _);
                }
            }
            catch (Exception ex) { exceptions.Add(ex); }
        }));

        await Task.WhenAll(writers.Concat(readers));

        Assert.Empty(exceptions);
    }
}

public sealed class IroStatusEffectRulesTests() : StatusEffectRulesTests(GameplayRegion.Iro);

public sealed class KroStatusEffectRulesTests() : StatusEffectRulesTests(GameplayRegion.Kro);
