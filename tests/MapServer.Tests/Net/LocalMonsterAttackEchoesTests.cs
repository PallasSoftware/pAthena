using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class LocalMonsterAttackEchoesTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    [Fact]
    public void ARegisteredAction_IsConsumedExactlyOnce_AndAnUnknownOneNever()
    {
        var echoes = new LocalMonsterAttackEchoes();
        var id = Guid.NewGuid();
        echoes.Register(id);

        Assert.True(echoes.TryConsume(id));
        Assert.False(echoes.TryConsume(id));
        Assert.False(echoes.TryConsume(Guid.NewGuid())); // another gateway's action is never skipped.
        Assert.Equal(0, echoes.Count);
    }

    [Fact]
    public void AForgottenAction_LeavesNothingBehind()
    {
        var echoes = new LocalMonsterAttackEchoes();
        var id = Guid.NewGuid();
        echoes.Register(id);
        echoes.Forget(id);

        Assert.False(echoes.TryConsume(id));
        Assert.Equal(0, echoes.Count);
    }

    [Fact]
    public void NeverConsumedActions_AreBoundedByCapAndAge()
    {
        var time = new ManualTime();
        var echoes = new LocalMonsterAttackEchoes(time);
        var old = Guid.NewGuid();
        echoes.Register(old);
        time.Advance(LocalMonsterAttackEchoes.MaxAge + TimeSpan.FromSeconds(1));

        // Exceeding the cap triggers pruning: the expired id goes, the count stays at or below the cap.
        for (var i = 0; i < LocalMonsterAttackEchoes.MaxEntries + 10; i++)
        {
            echoes.Register(Guid.NewGuid());
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.False(echoes.TryConsume(old));
        Assert.True(echoes.Count <= LocalMonsterAttackEchoes.MaxEntries);
    }
}
