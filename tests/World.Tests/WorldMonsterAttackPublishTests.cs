using Athena.Net.World.Contracts;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// Item 14 phase 4: PublishMonsterAttackActionAsync is the World side of "monster attacks are
// projected across replicas". The MapServer resolves and applies the attack locally; this RPC only
// publishes it to the monster feed so OTHER replicas can project it. Real grain, no fakes.
public sealed class WorldMonsterAttackPublishTests : IAsyncLifetime
{
    private const string MapId = "izlude";
    private const uint VictimCharacterId = 42;
    private const uint VictimActorId = 1_000_042; // deliberately different from the monster's actor id.

    private TestCluster _cluster = null!;
    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<WorldMonsterSimulationTests.TopologyConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition() => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>("world-rest");

    private static WorldMonsterSpawnBatch Batch() =>
        new(MapId, "", [new WorldMonsterSpawnDefinition(MobId: 1002, MapId, X: 100, Y: 100, Xs: 1, Ys: 1, Count: 1, RespawnDelayMs: 5000, RespawnRandomDelayMs: 0,
            SpawnName: "Poring", WalkSpeedMs: 400, AttackRange: 1, MaxHp: 55, Mode: 0x80)]);

    private sealed record Setup(IWorldPartitionGrain Grain, WorldMonsterLifeReference Life, WorldPlayerPresence Victim, WorldMonsterFeedCursor Cursor);

    private async Task<Setup> ArrangeAsync()
    {
        var grain = Partition();
        var load = await grain.LoadMonsterSpawnsAsync(Batch());
        var bootstrap = await grain.PollMonsterFeedAsync(null, MapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        Assert.NotEqual(VictimActorId, actorId);
        var victim = new WorldPlayerPresence(Guid.NewGuid(), VictimActorId, VictimCharacterId, MapId, 101, 100);
        await grain.RegisterPresenceAsync(victim, new WorldPlayerPublicState("Victim", 0, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var life = new WorldMonsterLifeReference(MapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);
        return new Setup(grain, life, victim, new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence));
    }

    private static WorldMonsterAttackActionCommand Command(Setup s, Guid? actionId = null, Guid? presenceId = null, WorldMonsterLifeReference? life = null) =>
        new(life ?? s.Life, VictimCharacterId, presenceId ?? s.Victim.PresenceId, actionId ?? Guid.NewGuid(), Damage: 7, SourceAttackMotion: 672, TargetDamageMotion: 480, Lethal: false);

    [Fact]
    public async Task Publish_AppendsOneEntry_WhoseTargetIsTheVictimsActorId_NotTheMonsters()
    {
        var s = await ArrangeAsync();
        var actionId = Guid.NewGuid();

        var result = await s.Grain.PublishMonsterAttackActionAsync(Command(s, actionId));

        Assert.Equal(WorldMonsterAttackPublishStatus.Published, result.Status);
        var entry = Assert.Single((await s.Grain.PollMonsterFeedAsync(s.Cursor, MapId)).Entries!);
        Assert.Equal(WorldMonsterFeedEntryKind.MonsterAttackAction, entry.Kind);
        Assert.Equal(s.Life.ActorId, entry.ActorId);                       // the ATTACKER.
        Assert.Equal(VictimActorId, entry.MonsterAttack!.TargetActorId);   // the VICTIM - what a remote replica puts in 0x08C8.
        Assert.Equal(VictimCharacterId, entry.MonsterAttack.TargetCharacterId);
        Assert.Equal(s.Victim.PresenceId, entry.MonsterAttack.TargetPresenceId);
        Assert.Equal(actionId, entry.MonsterAttack.ActionId); // lets the publishing gateway recognise its own echo.
        Assert.Equal((7u, 672u, 480u, false), (entry.MonsterAttack.Damage, entry.MonsterAttack.SourceAttackMotion, entry.MonsterAttack.TargetDamageMotion, entry.MonsterAttack.Lethal));
    }

    [Fact]
    public async Task Publish_ReplayOfTheSameActionId_IsAlreadyPublished_AndAppendsNothing()
    {
        var s = await ArrangeAsync();
        var actionId = Guid.NewGuid();
        await s.Grain.PublishMonsterAttackActionAsync(Command(s, actionId));

        var replay = await s.Grain.PublishMonsterAttackActionAsync(Command(s, actionId));

        Assert.Equal(WorldMonsterAttackPublishStatus.AlreadyPublished, replay.Status);
        Assert.Single((await s.Grain.PollMonsterFeedAsync(s.Cursor, MapId)).Entries!);
        // A genuinely new attack (new ActionId) is a new entry.
        Assert.Equal(WorldMonsterAttackPublishStatus.Published, (await s.Grain.PublishMonsterAttackActionAsync(Command(s))).Status);
        Assert.Equal(2, (await s.Grain.PollMonsterFeedAsync(s.Cursor, MapId)).Entries!.Count);
    }

    [Fact]
    public async Task Publish_ForAStaleVictimPresenceOrStaleMonsterLife_IsRejected_AndAppendsNothing()
    {
        var s = await ArrangeAsync();

        Assert.Equal(WorldMonsterAttackPublishStatus.StaleTargetPresence,
            (await s.Grain.PublishMonsterAttackActionAsync(Command(s, presenceId: Guid.NewGuid()))).Status);
        Assert.Equal(WorldMonsterAttackPublishStatus.StaleLifeReference,
            (await s.Grain.PublishMonsterAttackActionAsync(Command(s, life: s.Life with { IncarnationId = s.Life.IncarnationId.Next() }))).Status);
        Assert.Equal(WorldMonsterAttackPublishStatus.StaleLifeReference,
            (await s.Grain.PublishMonsterAttackActionAsync(Command(s, life: s.Life with { SimulationEpoch = WorldSimulationEpoch.NewEpoch() }))).Status);

        Assert.Empty((await s.Grain.PollMonsterFeedAsync(s.Cursor, MapId)).Entries!);

        // A victim that already left the world cannot be the target of a late publish either.
        await s.Grain.UnregisterPresenceAsync(MapId, VictimCharacterId, s.Victim.PresenceId);
        Assert.Equal(WorldMonsterAttackPublishStatus.StaleTargetPresence, (await s.Grain.PublishMonsterAttackActionAsync(Command(s))).Status);
    }
}
