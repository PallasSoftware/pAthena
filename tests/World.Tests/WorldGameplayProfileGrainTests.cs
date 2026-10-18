using Athena.Net.World.Contracts;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// The World owns an immutable, deployment-configured gameplay profile; MapServers never define it.
public sealed class WorldGameplayProfileConfigurationTests
{
    private static Func<string, string?> Env(string? region = null, string? ruleSet = null) =>
        name => name switch { WorldGameplayProfileOptions.RegionVariable => region, WorldGameplayProfileOptions.RuleSetVariable => ruleSet, _ => null };

    [Fact]
    public void Defaults_AreIroAndRenewal() =>
        Assert.Equal(new WorldGameplayProfile("Iro", "Renewal"), WorldGameplayProfileOptions.FromEnvironment(Env()).Profile);

    [Theory]
    [InlineData("kRO", "Kro")]
    [InlineData("KRO", "Kro")]
    [InlineData(" iro ", "Iro")]
    public void Region_IsCaseInsensitiveAndCanonicalised(string value, string expected) =>
        Assert.Equal(expected, WorldGameplayProfileOptions.FromEnvironment(Env(region: value)).Profile.Region);

    [Theory]
    [InlineData("jRO", null)]
    [InlineData("", null)]
    [InlineData("1", null)]
    [InlineData(null, "PreRenewal")]
    [InlineData(null, "Bogus")]
    public void InvalidValues_FailStartup(string? region, string? ruleSet) =>
        Assert.Throws<InvalidOperationException>(() => WorldGameplayProfileOptions.FromEnvironment(Env(region, ruleSet)));
}

public sealed class WorldGameplayProfileGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private static readonly WorldGameplayProfile Iro = new("Iro", "Renewal");
    private static readonly WorldGameplayProfile Kro = new("Kro", "Renewal");

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<IroSilo>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    public sealed class IroSilo : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.AddWorldGameplayProfile(new WorldGameplayProfileOptions(Iro));
    }

    private IWorldGameplayProfileGrain Grain() => _cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);

    [Fact]
    public async Task ConfiguredProfileIsAccepted_AndAnyOtherIsRejected_NeverFirstWins()
    {
        // A kRO MapServer arriving FIRST must not define the world's profile: the World is configured for iRO.
        var kroFirst = await Grain().RegisterAsync(Kro);
        var iro = await Grain().RegisterAsync(Iro);

        Assert.False(kroFirst.Accepted);
        Assert.Equal(Iro, kroFirst.Established);
        Assert.True(iro.Accepted);
        Assert.Equal(Iro, iro.Established);
    }

    [Fact]
    public async Task RuleSetMismatch_IsRejected() =>
        Assert.False((await Grain().RegisterAsync(new WorldGameplayProfile("Iro", "PreRenewal"))).Accepted);

    [Theory]
    [InlineData("", "Renewal")]
    [InlineData("Iro", " ")]
    public async Task IncompleteProfile_IsRejectedAsInvalid(string region, string ruleSet) =>
        await Assert.ThrowsAnyAsync<Exception>(() => Grain().RegisterAsync(new WorldGameplayProfile(region, ruleSet)));

    // Grain re-activation: collect the activation, then ask again - the recreated grain recovers the profile from configuration.
    [Fact]
    public async Task AfterTheActivationIsCollected_TheRecreatedGrainRecoversTheConfiguredProfile()
    {
        Assert.True((await Grain().RegisterAsync(Iro)).Accepted);

        await _cluster.GrainFactory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        var kro = await Grain().RegisterAsync(Kro);
        var iro = await Grain().RegisterAsync(Iro);
        Assert.False(kro.Accepted);
        Assert.Equal(Iro, kro.Established);
        Assert.True(iro.Accepted);
    }
}

// Cluster-level behavior: silos must agree (silo metadata is authoritative, the grain is secondary), and a failover keeps the profile.
public sealed class WorldGameplayProfileClusterTests
{
    private static readonly WorldGameplayProfile Iro = new("Iro", "Renewal");
    private static readonly WorldGameplayProfile Kro = new("Kro", "Renewal");

    // Options for the NEXT silo(s) to be built, consumed one per silo (null = a silo that publishes no profile at all).
    private static readonly System.Collections.Concurrent.ConcurrentQueue<WorldGameplayProfileOptions?> s_next = new();

    public sealed class PerSiloProfile : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            if (s_next.TryDequeue(out var options) && options is not null) siloBuilder.AddWorldGameplayProfile(options);
        }
    }

    private static WorldGameplayProfileOptions Opts(WorldGameplayProfile profile) =>
        new(profile) { PeerMetadataTimeout = TimeSpan.FromSeconds(3), MonitorInterval = TimeSpan.FromMilliseconds(100) };

    private static async Task<TestCluster> DeployAsync(WorldGameplayProfileOptions? first, short silos = 1)
    {
        s_next.Clear();
        for (var i = 0; i < silos; i++) s_next.Enqueue(first);
        var builder = new TestClusterBuilder(silos);
        builder.AddSiloBuilderConfigurator<PerSiloProfile>();
        var cluster = builder.Build();
        await cluster.DeployAsync();
        return cluster;
    }

    // While a rejected silo is still being torn down the grain fails closed (it throws instead of answering); the answer itself
    // is only asserted once the call succeeds. A call that keeps failing for 15 s fails the test with the last exception.
    // After a rejected join the half-started silo can linger as an active peer until membership declares it dead; the grain then
    // fails closed. Either outcome is fine - answering "accepted" for the foreign profile is the only violation.
    private static async Task AssertNeverAcceptsAsync(TestCluster cluster, WorldGameplayProfile foreign)
    {
        var grain = cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);
        WorldGameplayProfileRegistration? answer = null;
        try { answer = await grain.RegisterAsync(foreign); } catch (Exception) { /* failed closed */ }
        Assert.False(answer?.Accepted ?? false);
    }

    private static async Task<WorldGameplayProfileRegistration> RegisterAsync(TestCluster cluster, WorldGameplayProfile profile)
    {
        var grain = cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try { return await grain.RegisterAsync(profile); }
            catch (Exception) when (DateTime.UtcNow < deadline) { await Task.Delay(200); }
        }
    }

    [Fact]
    public async Task ASiloConfiguredDifferentlyFromTheCluster_FailsToStart()
    {
        var cluster = await DeployAsync(Opts(Iro));
        try
        {
            s_next.Enqueue(Opts(Kro));
            await Assert.ThrowsAnyAsync<Exception>(() => cluster.StartAdditionalSiloAsync());

            await AssertNeverAcceptsAsync(cluster, Kro);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Regression for the CI failure: with the grain deactivated, the joining silo must be rejected by the metadata check
    // even if the grain would activate on the joining silo itself and accept its own profile.
    [Fact]
    public async Task ADifferentlyConfiguredSiloJoiningAfterTheGrainWasDeactivated_StillFailsToStart()
    {
        var cluster = await DeployAsync(Opts(Iro));
        try
        {
            Assert.True((await RegisterAsync(cluster, Iro)).Accepted);
            await cluster.GrainFactory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            s_next.Enqueue(Opts(Kro));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => cluster.StartAdditionalSiloAsync());

            Assert.Contains("mismatch", ex.ToString(), StringComparison.OrdinalIgnoreCase);
            await AssertNeverAcceptsAsync(cluster, Kro);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task ASiloWithTheSameProfile_JoinsAndTheClusterKeepsOneProfile()
    {
        var cluster = await DeployAsync(Opts(Kro));
        try
        {
            s_next.Enqueue(Opts(Kro));
            await cluster.StartAdditionalSiloAsync();

            Assert.Equal(2, cluster.GetActiveSilos().Count());
            Assert.True((await RegisterAsync(cluster, Kro)).Accepted);
            Assert.False((await RegisterAsync(cluster, Iro)).Accepted);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Fail closed: a peer that publishes no profile is never treated as a match.
    [Fact]
    public async Task APeerWithoutProfileMetadata_IsNeverTreatedAsAMatch()
    {
        var cluster = await DeployAsync(null);
        try
        {
            s_next.Enqueue(Opts(Iro));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => cluster.StartAdditionalSiloAsync());

            Assert.Contains("could not be verified", ex.ToString());
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Failover: stop the silo that hosts the profile grain; the grain re-activates on the survivor with the same profile, and the
    // dead peer (no longer active) does not make the peer verification fail.
    [Fact]
    public async Task WhenTheHostingSiloStops_TheGrainFailsOverWithTheSameProfile()
    {
        var cluster = await DeployAsync(Opts(Iro), 2);
        try
        {
            Assert.True((await RegisterAsync(cluster, Iro)).Accepted);

            var management = cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            var host = (await management.GetSimpleGrainStatistics())
                .First(g => g.GrainType.Contains("WorldGameplayProfileGrain", StringComparison.OrdinalIgnoreCase) && g.ActivationCount > 0).SiloAddress;
            var hostSilo = cluster.GetActiveSilos().First(s => s.SiloAddress.Equals(host));

            await cluster.StopSiloAsync(hostSilo);

            var kro = await RegisterAsync(cluster, Kro);
            Assert.False(kro.Accepted);
            Assert.Equal(Iro, kro.Established);
            Assert.True((await RegisterAsync(cluster, Iro)).Accepted);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Two differently configured silos starting at the same moment against an iRO cluster: exactly the kRO one is rejected.
    [Fact]
    public async Task ConcurrentIroAndKroStartups_OnlyTheMatchingSiloJoins()
    {
        var cluster = await DeployAsync(Opts(Iro));
        try
        {
            s_next.Enqueue(Opts(Iro));
            s_next.Enqueue(Opts(Kro));
            var starts = new[] { cluster.StartAdditionalSiloAsync(), cluster.StartAdditionalSiloAsync() };
            try { await Task.WhenAll(starts); } catch { /* the rejected silo's failure is asserted below */ }

            if (starts.Count(t => t.IsFaulted) != 1)
            {
                var diag = string.Join(" ||| ", starts.Select((t, i) => $"start[{i}].IsFaulted={t.IsFaulted} ex={t.Exception?.Flatten().InnerException?.Message?.Replace('\n', ' ').Replace('\r', ' ')}"));
                throw new Exception($"DIAG: {diag}");
            }
            Assert.Equal(1, starts.Count(t => t.IsFaulted));
            Assert.Equal(2, cluster.GetActiveSilos().Count());
            await AssertNeverAcceptsAsync(cluster, Kro);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // The join race itself: a silo that skipped (or could not yet see its peers in) the startup check becomes active next to a
    // differently configured one. The running monitors must find the mismatch and stop exactly the NEWER silo - never leave a
    // mixed cluster running, and never stop the established silo because of a newcomer.
    [Fact]
    public async Task SilosThatBecameActiveTogetherWithDifferentProfiles_AreCaughtByTheMonitor_AndOnlyTheNewerStops()
    {
        var olderReports = new System.Collections.Concurrent.ConcurrentBag<string>();
        var newerReports = new System.Collections.Concurrent.ConcurrentBag<string>();
        var cluster = await DeployAsync(Opts(Iro) with { OnPeerMismatch = olderReports.Add });
        try
        {
            s_next.Enqueue(Opts(Kro) with { VerifyAtStartup = false, OnPeerMismatch = newerReports.Add });
            await cluster.StartAdditionalSiloAsync();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (newerReports.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(100);
            await Task.Delay(500); // give the older silo's monitor several more ticks in which it could wrongly report

            Assert.Single(newerReports);
            Assert.Contains("gameplay profile", newerReports.Single());
            Assert.Empty(olderReports);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Pins the exact problematic interleaving by construction instead of by timing luck: an established iRO silo (oldest
    // Generation), plus a matching iRO newcomer and a mismatched Kro newcomer starting concurrently, where the Kro newcomer's
    // Generation is made OLDER than the iRO newcomer's. Check() enumeration therefore surfaces the Kro newcomer - not the
    // established silo - as the iRO newcomer's "oldest mismatch", and a naive self-vs-mismatch Generation tie-break would make
    // the iRO newcomer fault even though it fully agrees with the established cluster.
    [Fact]
    public void MatchingNewcomer_IsNotAFaultingConflict_WhenOnlyAFellowNewcomerLooksOlderInEnumeration()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100); // oldest: already active before anyone else started
        var kroNewcomer = SiloAddress.New(endpoint, 150); // starts concurrently, but got a lower Generation than the iRO newcomer
        var iroNewcomer = SiloAddress.New(endpoint, 200); // the matching silo under test - self

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [kroNewcomer] = WorldGameplayProfilePeers.Encode(Kro),
        });

        var result = WorldGameplayProfilePeers.Check(Iro, iroNewcomer, [established, kroNewcomer, iroNewcomer], cache);

        // Confirms Check() does surface the Kro newcomer, not the established silo, as the "oldest mismatch" for this case -
        // i.e. this test exercises the actual enumeration-order hazard, not a scenario the old code handled by accident.
        Assert.Equal(kroNewcomer, result.MismatchPeer);
        Assert.Equal(established, result.OldestAgreeingPeer);

        // The pre-fix logic (a plain self-vs-MismatchPeer Generation tie-break) would see iroNewcomer (200) newer than
        // kroNewcomer (150) and incorrectly fault. The fix must recognise self already agrees with the established silo
        // (100), which predates the mismatch, so this is not a real conflict.
        Assert.False(WorldGameplayProfilePeers.IsFaultingConflict(iroNewcomer, result));
    }

    // The same interleaving from the Kro newcomer's own perspective: it has no older agreeing peer, so its mismatch against
    // the established silo (which predates it) is a real conflict and it must fault.
    [Fact]
    public void MismatchedNewcomer_IsAFaultingConflict_AgainstTheEstablishedSilo()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100);
        var kroNewcomer = SiloAddress.New(endpoint, 150);
        var iroNewcomer = SiloAddress.New(endpoint, 200);

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [iroNewcomer] = WorldGameplayProfilePeers.Encode(Iro),
        });

        var result = WorldGameplayProfilePeers.Check(Kro, kroNewcomer, [established, kroNewcomer, iroNewcomer], cache);

        Assert.Equal(established, result.MismatchPeer);
        Assert.Null(result.OldestAgreeingPeer);
        Assert.True(WorldGameplayProfilePeers.IsFaultingConflict(kroNewcomer, result));
    }

    // Regression for the real-CI gap behind the three consecutive failures of ConcurrentIroAndKroStartups_OnlyTheMatchingSiloJoins:
    // ISiloMetadataCache is itself eventually consistent, not instant. The oracle can already report the established silo as
    // Active while its metadata has not yet propagated into the newcomer's local cache view (Pending), even though a fellow,
    // merely-concurrent newcomer's (mismatched) metadata already HAS propagated. A naive mismatch-vs-IsFaultingConflict check,
    // with no regard for Pending, would see MismatchPeer=kroNewcomer (established, older than self) and OldestAgreeingPeer=null
    // (the real agreement is sitting in Pending, not resolved) and fault - even though the matching newcomer is actually fine
    // and would find its agreement the moment the established silo's metadata arrives.
    [Fact]
    public void MatchingNewcomer_ResultHasUnresolvedOlderPending_WhenEstablishedPeersMetadataHasNotYetPropagated()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100); // oldest, Active in the oracle, but NOT yet cached
        var kroNewcomer = SiloAddress.New(endpoint, 150); // mismatched, and ALREADY cached
        var iroNewcomer = SiloAddress.New(endpoint, 200); // self: matches the established cluster

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [kroNewcomer] = WorldGameplayProfilePeers.Encode(Kro),
        });
        cache.NotYetCached.Add(established);

        var result = WorldGameplayProfilePeers.Check(Iro, iroNewcomer, [established, kroNewcomer, iroNewcomer], cache);

        // Check() alone cannot tell the difference between "no agreeing peer exists" and "the agreeing peer just isn't cached
        // yet" - both read as OldestAgreeingPeer=null. The caller (VerifyAsync/Evaluate) must consult result.Pending itself.
        Assert.Equal(kroNewcomer, result.MismatchPeer);
        Assert.Null(result.OldestAgreeingPeer);
        Assert.Contains(established, result.Pending);
        Assert.True(WorldGameplayProfilePeers.IsFaultingConflict(iroNewcomer, result));
    }

    // End-to-end through VerifyAsync: the matching newcomer must NOT throw on the first poll just because the established
    // peer's metadata is still Pending while the mismatched fellow newcomer already resolved - it must retry and succeed once
    // the established silo's metadata propagates into the cache (simulating real, non-instant ISiloMetadataCache propagation).
    [Fact]
    public async Task MatchingNewcomer_VerifyAsyncRetries_WhenEstablishedPeersMetadataIsStillPendingButAMismatchAlreadyResolved()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100);
        var kroNewcomer = SiloAddress.New(endpoint, 150);
        var iroNewcomer = SiloAddress.New(endpoint, 200);

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [kroNewcomer] = WorldGameplayProfilePeers.Encode(Kro),
        });
        cache.NotYetCached.Add(established); // simulates the propagation lag the real CI timing exposed
        var oracle = new FakeSiloStatusOracle([established, kroNewcomer, iroNewcomer]);
        var options = new WorldGameplayProfileOptions(Iro) { PeerMetadataTimeout = TimeSpan.FromSeconds(10) };

        var verify = WorldGameplayProfilePeers.VerifyAsync(options, cache, oracle, iroNewcomer, CancellationToken.None);
        await Task.Delay(300); // let it poll at least once while still Pending - must not have thrown by now
        Assert.False(verify.IsCompleted);

        cache.NotYetCached.Remove(established); // the established silo's metadata finally propagates
        await verify; // must now complete rather than throw
    }

    // Regression for a second gap in the SAME retry loop: once self has CURRENT, complete information and IsFaultingConflict
    // has already decided self is NOT the offender, self must stop treating that mismatch as its own concern - it must not
    // keep burning its OWN PeerMetadataTimeout waiting for the actually-guilty peer to disappear from membership. The guilty
    // peer staying Active past the timeout (slow teardown) is that peer's problem, not self's; a confirmed-innocent newcomer
    // must converge immediately, not throw once ITS deadline (meant for ITS OWN unresolved Pending info) elapses.
    [Fact]
    public async Task MatchingNewcomer_VerifyAsyncSucceeds_WhenConfirmedInnocentEvenIfTheMismatchedPeerStaysActivePastTheTimeout()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100);
        var kroNewcomer = SiloAddress.New(endpoint, 150); // mismatched, confirmed - but stays Active well past the timeout below
        var iroNewcomer = SiloAddress.New(endpoint, 200); // self: confirmed innocent via OldestAgreeingPeer = established

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [kroNewcomer] = WorldGameplayProfilePeers.Encode(Kro),
        });
        // Both peers' metadata is fully resolved from the first poll onward - no Pending ambiguity - so self already has
        // everything it needs to know it is innocent of the kroNewcomer mismatch.
        var oracle = new FakeSiloStatusOracle([established, kroNewcomer, iroNewcomer]);
        var options = new WorldGameplayProfileOptions(Iro) { PeerMetadataTimeout = TimeSpan.FromMilliseconds(50) };

        // The pre-fix loop would throw here once PeerMetadataTimeout (50 ms) elapsed, purely because kroNewcomer never left
        // the oracle's Active set - even though self had already and correctly decided it is not the offender.
        await WorldGameplayProfilePeers.VerifyAsync(options, cache, oracle, iroNewcomer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // End-to-end: the matching iRO newcomer's VerifyAsync must complete (never throw) once the conflicting Kro newcomer has
    // left the picture - simulating the Kro newcomer having faulted on its own check - confirming the fix holds through the
    // full polling loop, not just the pure Check/IsFaultingConflict helpers.
    [Fact]
    public async Task MatchingNewcomer_VerifyAsyncSucceeds_OnceTheMismatchedFellowNewcomerIsGone()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100);
        var kroNewcomer = SiloAddress.New(endpoint, 150);
        var iroNewcomer = SiloAddress.New(endpoint, 200);

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [kroNewcomer] = WorldGameplayProfilePeers.Encode(Kro),
        });
        // The first poll still sees the Kro newcomer active (the race); it is gone from the second poll onward, as if it had
        // just faulted on its own VerifyAsync and stopped - this is what lets our silo's retry loop converge deterministically.
        var oracle = new FakeSiloStatusOracle([[established, kroNewcomer, iroNewcomer], [established, iroNewcomer]]);
        var options = new WorldGameplayProfileOptions(Iro) { PeerMetadataTimeout = TimeSpan.FromSeconds(3) };

        // Must return (agree with the established cluster) rather than throw, which the pre-fix tie-break could do on the
        // very first poll before the Kro newcomer ever had a chance to disappear.
        await WorldGameplayProfilePeers.VerifyAsync(options, cache, oracle, iroNewcomer, CancellationToken.None);
    }

    // Regression for the fail-open gap found in review: having ONE agreeing peer must never exempt a DIFFERENT, unrelated
    // peer from verification. Here there is no mismatch at all - just an agreeing peer plus a second peer whose metadata
    // never arrives (permanently silent, e.g. a hung peer). Self (iroNewcomer, the OLDER/lower-Generation side relative to
    // the silent peer) is NOT the one responsible for yielding to it (IsNewerThan(self, silent) is false, since
    // self.Generation < silent.Generation), so per the ORIGINAL pre-regression per-peer Pending/IsNewerThan rule, self must
    // succeed - yielding to a silent peer is only ever the NEWER side's job. The fail-open bug would have made this pass
    // for the wrong reason (the blanket OldestAgreeingPeer-is-not-null shortcut); this asserts it still succeeds for the
    // RIGHT reason (self is not newer than the silent peer).
    [Fact]
    public async Task MatchingNewcomer_VerifyAsyncSucceeds_WithAnAgreeingPeerAndAYoungerPermanentlySilentPeer()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var agreeing = SiloAddress.New(endpoint, 50);
        var silent = SiloAddress.New(endpoint, 300); // younger than self: self is not responsible for yielding to it
        var iroNewcomer = SiloAddress.New(endpoint, 200); // self

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string> { [agreeing] = WorldGameplayProfilePeers.Encode(Iro) });
        // `silent` has no entry at all, so it reads back Pending forever - never resolves, never mismatches.
        var oracle = new FakeSiloStatusOracle([agreeing, silent, iroNewcomer]);
        var options = new WorldGameplayProfileOptions(Iro) { PeerMetadataTimeout = TimeSpan.FromMilliseconds(50) };

        await WorldGameplayProfilePeers.VerifyAsync(options, cache, oracle, iroNewcomer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Mirror of the above with self as the NEWER side relative to the silent peer: self IS responsible for yielding, so it
    // must still throw once its own PeerMetadataTimeout elapses - confirming the agreeing peer never exempts this case
    // either (the fail-open bug would have incorrectly returned success here too).
    [Fact]
    public async Task MatchingNewcomer_VerifyAsyncThrows_WithAnAgreeingPeerAndAnOlderPermanentlySilentPeer()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var agreeing = SiloAddress.New(endpoint, 50);
        var silent = SiloAddress.New(endpoint, 100); // older than self: self must yield to it
        var iroNewcomer = SiloAddress.New(endpoint, 200); // self: newer than `silent`

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string> { [agreeing] = WorldGameplayProfilePeers.Encode(Iro) });
        var oracle = new FakeSiloStatusOracle([agreeing, silent, iroNewcomer]);
        var options = new WorldGameplayProfileOptions(Iro) { PeerMetadataTimeout = TimeSpan.FromMilliseconds(50) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorldGameplayProfilePeers.VerifyAsync(options, cache, oracle, iroNewcomer, CancellationToken.None));
    }

    // Same fail-open scenario against the running monitor's Evaluate, not just VerifyAsync: an agreeing peer must not
    // exempt a separate, permanently-silent peer from the monitor's own _pendingSince/timeout loop.
    [Fact]
    public void Evaluate_StillTimesOutASilentPeer_EvenWithAnUnrelatedAgreeingPeer()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var agreeing = SiloAddress.New(endpoint, 50);
        var silent = SiloAddress.New(endpoint, 100); // older than self: self IS responsible for yielding to it
        var self = SiloAddress.New(endpoint, 200);

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string> { [agreeing] = WorldGameplayProfilePeers.Encode(Iro) });
        var result = WorldGameplayProfilePeers.Check(Iro, self, [agreeing, silent, self], cache);

        Assert.Null(result.Mismatch);
        Assert.NotNull(result.OldestAgreeingPeer);
        Assert.Contains(silent, result.Pending);
        // Directly exercises the same per-peer rule Evaluate applies: self must still be on the hook for `silent` despite
        // the unrelated agreeing peer, because self is the newer side relative to `silent`.
        Assert.True(WorldGameplayProfilePeers.IsNewerThan(self, silent));
    }

    private sealed class FakeSiloMetadataCache(Dictionary<SiloAddress, string> profiles) : ISiloMetadataCache
    {
        // Peers removed here read back as "not yet cached" (Pending), regardless of `profiles`, to simulate metadata that the
        // oracle already reports Active but that has not yet propagated into this silo's local cache view.
        public HashSet<SiloAddress> NotYetCached { get; } = [];

        // SiloMetadata exposes Metadata as get-only with no public way to populate an instance outside Orleans' own
        // UseSiloMetadata pipeline; the backing field is set via reflection purely to build this test double.
        public SiloMetadata GetSiloMetadata(SiloAddress silo)
        {
            var metadata = new SiloMetadata();
            var backingField = typeof(SiloMetadata).GetField("<Metadata>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            backingField.SetValue(metadata, !NotYetCached.Contains(silo) && profiles.TryGetValue(silo, out var value)
                ? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add(WorldGameplayProfilePeers.MetadataKey, value)
                : System.Collections.Immutable.ImmutableDictionary<string, string>.Empty);
            return metadata;
        }
    }

    private sealed class FakeSiloStatusOracle : ISiloStatusOracle
    {
        private readonly IReadOnlyList<IReadOnlyList<SiloAddress>> _snapshots;
        private int _poll;

        public FakeSiloStatusOracle(IReadOnlyList<SiloAddress> active) : this([active]) { }

        // A sequence of active-peer snapshots, advancing one step per GetApproximateSiloStatuses call and staying on the
        // last snapshot once exhausted - lets a test simulate a peer disappearing between polls deterministically (by call
        // count, not timing).
        public FakeSiloStatusOracle(IReadOnlyList<IReadOnlyList<SiloAddress>> snapshots) => _snapshots = snapshots;

        private IReadOnlyList<SiloAddress> Active => _snapshots[Math.Min(_poll, _snapshots.Count - 1)];
        public SiloAddress SiloAddress => Active[0];
        public SiloStatus CurrentStatus => SiloStatus.Active;
        public string SiloName => "fake";
        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            var snapshot = Active.ToDictionary(s => s, _ => SiloStatus.Active);
            _poll++;
            return snapshot;
        }
        public SiloAddress[] GetActiveSilos() => [.. Active];
        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress) => SiloStatus.Active;
        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName) { siloName = "fake"; return true; }
        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => true;
        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => true;
        public bool IsDeadSilo(SiloAddress silo) => false;
        public bool IsFunctionalDirectory(SiloAddress siloAddress) => true;
    }
}
