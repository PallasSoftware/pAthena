using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// The World-owned presence ownership lease: a presence registered by a gateway lives only while that gateway
// keeps renewing it, expiry is decided on World's own clock, and every removal/renewal is fenced by PresenceId
// (and, for renewal, GatewayId). Real grain on a real TestCluster; the clock is real time plus an explicit
// offset, so expiry is proven by moving the clock - never by waiting out the TTL.
public sealed class WorldPresenceLeaseTests : IAsyncLifetime
{
    private const string Map = "izlude";

    internal sealed class OffsetClock : TimeProvider
    {
        private long _offsetTicks;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
        public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
    }

    private static readonly OffsetClock Clock = new();
    private TestCluster _cluster = null!;

    public sealed class Configurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.Services
            .AddSingleton<IWorldPartitionResolver>(WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["prontera", "prt_fild08d", "izlude", "geffen"]))
            .AddSingleton<IMovementPathProvider, UnverifiedGridLineMovementPathProvider>()
            .AddSingleton<IMapCollisionProvider>(EmptyMapCollisionProvider.Instance)
            .AddSingleton<TimeProvider>(Clock);
    }

    public async Task InitializeAsync()
    {
        Clock.Reset();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<Configurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Grain() => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>("world-rest");
    private static readonly WorldPlayerPublicState State = new("P", 0, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly Guid GatewayA = Guid.NewGuid();
    private static readonly Guid GatewayB = Guid.NewGuid();
    private static WorldPlayerPresence Presence(uint characterId, Guid gateway, Guid? presenceId = null) =>
        new(presenceId ?? Guid.NewGuid(), ActorId: characterId + 1000, characterId, Map, 100, 100, GatewayId: gateway);

    private async Task<WorldPlayerFeedPage> PollAsync(WorldPlayerFeedCursor? cursor = null) => await Grain().PollPlayerFeedAsync(cursor, Map);
    private static WorldPlayerFeedCursor CursorOf(WorldPlayerFeedPage page) => new(page.Epoch, page.AsOfSequence);
    private static LeaseRef Ref(WorldPlayerPresence p) => new(p.CharacterId, p.PresenceId);
    private sealed record LeaseRef(uint CharacterId, Guid PresenceId)
    {
        public static implicit operator WorldPresenceLeaseRef(LeaseRef r) => new(r.CharacterId, r.PresenceId);
    }

    private Task<IReadOnlyList<WorldPresenceLeaseResult>> RenewAsync(Guid gateway, params WorldPlayerPresence[] presences) =>
        Grain().RenewPresenceLeasesAsync(Map, gateway, [.. presences.Select(p => (WorldPresenceLeaseRef)Ref(p))]);

    [Fact]
    public async Task OwnerThatKeepsRenewing_IsNeverExpired_EvenAcrossManyTtls()
    {
        var p = Presence(1, GatewayA);
        await Grain().RegisterPresenceAsync(p, State);
        for (var i = 0; i < 20; i++)
        {
            Clock.Advance(WorldPresenceLease.RenewInterval);
            Assert.Equal(WorldPresenceLeaseStatus.Renewed, Assert.Single(await RenewAsync(GatewayA, p)).Status);
        }
        Assert.Single((await PollAsync()).Snapshot!);
    }

    [Fact]
    public async Task UnrenewedPresence_IsRemovedAfterTheTtl_WithExactlyOneUnregisteredEntry()
    {
        var healthy = Presence(1, GatewayA);
        var orphan = Presence(2, GatewayB);
        await Grain().RegisterPresenceAsync(healthy, State);
        await Grain().RegisterPresenceAsync(orphan, State);
        var cursor = CursorOf(await PollAsync());

        // Gateway B dies; gateway A keeps renewing inside the TTL.
        for (var i = 0; i < 4; i++)
        {
            Clock.Advance(WorldPresenceLease.RenewInterval);
            await RenewAsync(GatewayA, healthy);
        }

        var page = await PollAsync(cursor);
        var removal = Assert.Single(page.Entries!);
        Assert.Equal((WorldPlayerFeedEntryKind.Unregistered, 1002u), (removal.Kind, removal.ActorId));
        Assert.Equal([1001u], (await PollAsync()).Snapshot!.Select(e => e.Presence.ActorId));
        // Polling again changes nothing (no second Unregistered).
        Assert.Empty((await PollAsync(CursorOf(page))).Entries!);
    }

    [Fact]
    public async Task JustInsideTheTtl_NothingIsRemoved()
    {
        var p = Presence(1, GatewayA);
        await Grain().RegisterPresenceAsync(p, State);
        Clock.Advance(WorldPresenceLease.Ttl - TimeSpan.FromSeconds(1));
        Assert.Single((await PollAsync()).Snapshot!);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty((await PollAsync()).Snapshot!);
    }

    [Fact]
    public async Task PresenceWithoutAGateway_HasNoLease_AndNeverExpires()
    {
        await Grain().RegisterPresenceAsync(Presence(1, Guid.Empty), State);
        Clock.Advance(TimeSpan.FromHours(6));
        Assert.Single((await PollAsync()).Snapshot!);
    }

    [Fact]
    public async Task RenewalIsFenced_ByPresenceId_AndByGateway_AndAbsentReportsExpiry()
    {
        var p = Presence(1, GatewayA);
        await Grain().RegisterPresenceAsync(p, State);

        Assert.Equal(WorldPresenceLeaseStatus.PresenceMismatch, Assert.Single(await Grain().RenewPresenceLeasesAsync(Map, GatewayA, [new(p.CharacterId, Guid.NewGuid())])).Status);
        Assert.Equal(WorldPresenceLeaseStatus.NotOwner, Assert.Single(await RenewAsync(GatewayB, p)).Status);
        Assert.Equal(WorldPresenceLeaseStatus.Absent, Assert.Single(await Grain().RenewPresenceLeasesAsync(Map, GatewayA, [new(999, Guid.NewGuid())])).Status);

        // Neither refused renewal extended anything: the lease still lapses on schedule.
        Clock.Advance(WorldPresenceLease.Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(WorldPresenceLeaseStatus.Absent, Assert.Single(await RenewAsync(GatewayA, p)).Status);
        Assert.Empty((await PollAsync()).Snapshot!);
    }

    [Fact]
    public async Task AnExpiredGateway_CanReassertItsOwnPresence_ButNeverOverANewerOne()
    {
        var old = Presence(1, GatewayA);
        await Grain().RegisterPresenceAsync(old, State);
        Clock.Advance(WorldPresenceLease.Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(WorldPresenceLeaseStatus.Absent, Assert.Single(await RenewAsync(GatewayA, old)).Status);

        // Nobody else claimed the character: the (still connected) session re-asserts the SAME presence.
        Assert.Equal(WorldPresenceRegistrationStatus.Registered, (await Grain().RegisterPresenceAsync(old, State)).Status);
        Assert.Equal(WorldPresenceLeaseStatus.Renewed, Assert.Single(await RenewAsync(GatewayA, old)).Status);

        // Once the character is claimed by a newer presence, the older one can no longer come back or renew.
        Clock.Advance(WorldPresenceLease.Ttl + TimeSpan.FromSeconds(1));
        var newer = Presence(1, GatewayB);
        Assert.Equal(WorldPresenceRegistrationStatus.Registered, (await Grain().RegisterPresenceAsync(newer, State)).Status);
        Assert.Equal(WorldPresenceRegistrationStatus.Conflict, (await Grain().RegisterPresenceAsync(old, State)).Status);
        Assert.Equal(WorldPresenceLeaseStatus.PresenceMismatch, Assert.Single(await RenewAsync(GatewayA, old)).Status);
    }

    [Fact]
    public async Task StaleUnregisterAfterANewerRegistration_NeverRemovesTheNewPresence_AndRepeatsAreIdempotent()
    {
        var old = Presence(1, GatewayA);
        await Grain().RegisterPresenceAsync(old, State);
        // While the old session's gateway is dead, a reconnect cannot register until the lease lapses...
        Assert.Equal(WorldPresenceRegistrationStatus.Conflict, (await Grain().RegisterPresenceAsync(Presence(1, GatewayB), State)).Status);
        Clock.Advance(WorldPresenceLease.Ttl + TimeSpan.FromSeconds(1));
        // ...and then it takes over; the old presence is gone.
        var fresh = Presence(1, GatewayB);
        Assert.Equal(WorldPresenceRegistrationStatus.Registered, (await Grain().RegisterPresenceAsync(fresh, State)).Status);
        var cursor = CursorOf(await PollAsync());

        // The long-dead session's unregister finally arrives: fenced by PresenceId, nothing changes.
        Assert.Equal(WorldPresenceUnregistrationStatus.PresenceMismatch, (await Grain().UnregisterPresenceAsync(Map, 1, old.PresenceId)).Status);
        Assert.Empty((await PollAsync(cursor)).Entries!);
        Assert.Equal(fresh.PresenceId, Assert.Single((await PollAsync()).Snapshot!).Presence.PresenceId);

        // The legitimate unregister works once; repeating it is a harmless AlreadyAbsent with no extra entry.
        Assert.Equal(WorldPresenceUnregistrationStatus.Removed, (await Grain().UnregisterPresenceAsync(Map, 1, fresh.PresenceId)).Status);
        Assert.Equal(WorldPresenceUnregistrationStatus.AlreadyAbsent, (await Grain().UnregisterPresenceAsync(Map, 1, fresh.PresenceId)).Status);
        Assert.Single((await PollAsync(cursor)).Entries!);
    }

    [Fact]
    public async Task AGracefulUnregister_LeavesNoLeaseBehind_ToTriggerALaterSpuriousExpiry()
    {
        var p = Presence(1, GatewayA);
        await Grain().RegisterPresenceAsync(p, State);
        await Grain().UnregisterPresenceAsync(Map, 1, p.PresenceId);
        var cursor = CursorOf(await PollAsync());
        Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty((await PollAsync(cursor)).Entries!);
    }

    [Fact]
    public async Task SamePartitionTransfer_KeepsTheOwnerAndRefreshesTheLease()
    {
        var region = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>("prontera-region");
        var p = Presence(1, GatewayA) with { MapId = "prontera" };
        await region.RegisterPresenceAsync(p, State);
        Clock.Advance(WorldPresenceLease.Ttl - TimeSpan.FromSeconds(2));
        var result = await region.TransferPlayerAsync(new WorldTransferCommand(Guid.NewGuid(), p.PresenceId, 1, "prontera", "prt_fild08d", 10, 20));
        Assert.Equal(WorldTransferStatus.Completed, result.Status);

        // Still owned by gateway A on the destination map, with a fresh lease (and renewal works from there).
        Clock.Advance(TimeSpan.FromSeconds(10));
        var renewed = Assert.Single(await region.RenewPresenceLeasesAsync("prt_fild08d", GatewayA, [new(1, p.PresenceId)]));
        Assert.Equal(WorldPresenceLeaseStatus.Renewed, renewed.Status);
        Assert.Single((await region.PollPlayerFeedAsync(null, "prt_fild08d")).Snapshot!);
    }

    [Fact]
    public async Task EpochRotationAndResync_NeverResurrectAnExpiredPlayer()
    {
        var orphan = Presence(2, GatewayB);
        await Grain().RegisterPresenceAsync(orphan, State);
        var stale = CursorOf(await PollAsync());
        Clock.Advance(WorldPresenceLease.Ttl + TimeSpan.FromSeconds(1));

        // A consumer holding a pre-expiry cursor from a foreign epoch is answered with a snapshot that
        // already lacks the expired player; a late-joining consumer sees the same.
        var resync = await PollAsync(new WorldPlayerFeedCursor(WorldSimulationEpoch.NewEpoch(), stale.Sequence));
        Assert.Equal(WorldPlayerFeedStatus.ResyncRequired, resync.Status);
        Assert.Empty(resync.Snapshot!);
        Assert.Empty((await PollAsync()).Snapshot!);
    }
}
