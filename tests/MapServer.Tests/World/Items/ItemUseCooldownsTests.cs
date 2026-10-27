using Athena.Net.MapServer.Tests.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.World.Items;

// Pinned pc_itemcd_check (pc.cpp:14766-14785): a delay-group cooldown is keyed by the item_db
// Delay.Status SC_ constant, always re-armed (never stacked) on Start, and reports whether it is
// still running via Remaining. Reuses the same FakeTimeProvider pattern as
// CharacterStatusEffectStateTests (deterministic, no real clock).
public sealed class ItemUseCooldownsTests
{
    [Fact]
    public void NoCooldownStarted_RemainingIsNull()
    {
        var cooldowns = new ItemUseCooldowns(new FakeTimeProvider());
        Assert.Null(cooldowns.Remaining(delayGroupKey: 5));
    }

    [Fact]
    public void AfterStart_RemainingReflectsTheArmedDuration()
    {
        var clock = new FakeTimeProvider();
        var cooldowns = new ItemUseCooldowns(clock);

        cooldowns.Start(delayGroupKey: 5, TimeSpan.FromSeconds(10));

        var remaining = cooldowns.Remaining(5);
        Assert.NotNull(remaining);
        Assert.Equal(TimeSpan.FromSeconds(10), remaining.Value);
    }

    [Fact]
    public void RemainingCountsDownAsTimeAdvances_AndExpiresAtZero()
    {
        var clock = new FakeTimeProvider();
        var cooldowns = new ItemUseCooldowns(clock);
        cooldowns.Start(delayGroupKey: 5, TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(TimeSpan.FromSeconds(4), cooldowns.Remaining(5));

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(cooldowns.Remaining(5)); // expired exactly at the armed duration
    }

    [Fact]
    public void DifferentDelayGroupKeys_AreIndependent()
    {
        var clock = new FakeTimeProvider();
        var cooldowns = new ItemUseCooldowns(clock);
        cooldowns.Start(delayGroupKey: 5, TimeSpan.FromSeconds(10));

        // A different item sharing no group with key 5 must never be blocked by it.
        Assert.Null(cooldowns.Remaining(6));
    }

    [Fact]
    public void Restart_OverwritesRatherThanStacks()
    {
        var clock = new FakeTimeProvider();
        var cooldowns = new ItemUseCooldowns(clock);
        cooldowns.Start(delayGroupKey: 5, TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));

        // pc_itemcd_check only calls sc_start when nothing is currently active, but Start() itself
        // must still behave as an unconditional overwrite (matching sc_start's own semantics) if
        // ever called while a cooldown is running - never averaging or adding to the previous one.
        cooldowns.Start(delayGroupKey: 5, TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), cooldowns.Remaining(5));
    }
}
