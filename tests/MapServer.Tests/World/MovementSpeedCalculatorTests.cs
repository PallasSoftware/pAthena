using Athena.Net.MapServer.Tests.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public abstract class WalkSpeedRulesTests(GameplayRegion region)
{
    private GameplayRuleServices Rules { get; } = TestGameplayRules.Services(region);

    [Fact]
    public void NoHaste_UsesDefaultWalkSpeed()
    {
        Assert.Equal(150, Rules.Timing.WalkCellDurationMs(moveSpeedHaste: 0));
    }

    [Fact]
    public void IncreaseAgiHaste_Twenty_FiveReducesCellDurationBy25Percent()
    {
        // status_calc_speed: speed_rate = 100 - 25 = 75; speed = 150 * 75 / 100 = 112 (floor).
        Assert.Equal(112, Rules.Timing.WalkCellDurationMs(moveSpeedHaste: 25));
    }

    [Fact]
    public void SpeedRateFloorsAtForty_EvenForExtremeHaste()
    {
        // status.cpp:8203-8204: `if (speed_rate < 40) speed_rate = 40;`
        // speed = 150 * 40 / 100 = 60.
        Assert.Equal(60, Rules.Timing.WalkCellDurationMs(moveSpeedHaste: 999));
    }

    [Fact]
    public void ResultNeverBelowMinWalkSpeed()
    {
        Assert.True(Rules.Timing.WalkCellDurationMs(moveSpeedHaste: 999) >= 20);
    }

    [Fact]
    public void NegativeHaste_SlowsDownWithoutGoingBelowMinWalkSpeed()
    {
        // Not currently a supported real scenario (no slow status modeled), but the clamp must still hold.
        var result = Rules.Timing.WalkCellDurationMs(moveSpeedHaste: -50);
        Assert.True(result <= 1000 && result >= 20);
    }
}

public sealed class IroWalkSpeedRulesTests() : WalkSpeedRulesTests(GameplayRegion.Iro);

public sealed class KroWalkSpeedRulesTests() : WalkSpeedRulesTests(GameplayRegion.Kro);
