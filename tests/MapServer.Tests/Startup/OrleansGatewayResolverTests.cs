using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Startup;
using Microsoft.Extensions.Configuration;

namespace Athena.Net.MapServer.Tests.Startup;

// Orleans' static gateway list only accepts IP literals. MapServer resolves Service names (Kubernetes) to an IPv4 address at
// startup and must leave every existing literal/URI configuration untouched.
public sealed class OrleansGatewayResolverTests
{
    private static IConfiguration Configuration(params string[] gateways) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(gateways.Select((value, index) =>
                new KeyValuePair<string, string?>($"Orleans:Clustering:Gateways:{index}", value)))
            .Build();

    private static readonly TimeSpan Instant = TimeSpan.Zero;

    private static IReadOnlyDictionary<string, string?> Resolve(
        IConfiguration configuration,
        Func<string, IReadOnlyList<IPAddress>> lookup,
        TimeSpan? timeout = null,
        Action<string>? log = null) =>
        OrleansGatewayResolver.Resolve(configuration, lookup, timeout ?? TimeSpan.FromSeconds(5), Instant, log);

    [Fact]
    public void NoGateways_NothingToResolve()
    {
        var overrides = Resolve(Configuration(), _ => throw new InvalidOperationException("must not look anything up"));

        Assert.Empty(overrides);
    }

    [Theory]
    [InlineData("127.0.0.1:30000")]
    [InlineData("10.43.12.7:30000")]
    [InlineData("[::1]:30000")]
    [InlineData("gwy.tcp://worldserver:30000/0")]
    public void LiteralsAndOrleansUris_AreLeftAlone(string value)
    {
        var overrides = Resolve(Configuration(value), _ => throw new InvalidOperationException("must not look anything up"));

        Assert.Empty(overrides);
    }

    [Fact]
    public void ServiceName_IsReplacedByItsIPv4Address_KeepingThePort()
    {
        var logged = new List<string>();

        var overrides = Resolve(
            Configuration("worldserver:30000"),
            host => host == "worldserver" ? [IPAddress.Parse("10.43.12.7")] : throw new SocketException(),
            log: logged.Add);

        Assert.Equal("10.43.12.7:30000", overrides["Orleans:Clustering:Gateways:0"]);
        Assert.Contains(logged, line => line.Contains("worldserver -> 10.43.12.7:30000"));
    }

    [Fact]
    public void SeveralGateways_OnlyNamesAreResolved()
    {
        var overrides = Resolve(
            Configuration("127.0.0.1:30000", "worldserver:30001"),
            _ => [IPAddress.Parse("10.43.12.7")]);

        Assert.Single(overrides);
        Assert.Equal("10.43.12.7:30001", overrides["Orleans:Clustering:Gateways:1"]);
    }

    [Fact]
    public void IPv6Answers_AreIgnored_AndTheLowestIPv4IsChosenDeterministically()
    {
        var overrides = Resolve(
            Configuration("worldserver:30000"),
            _ => [IPAddress.IPv6Loopback, IPAddress.Parse("10.43.12.9"), IPAddress.Parse("10.43.12.7")]);

        Assert.Equal("10.43.12.7:30000", overrides["Orleans:Clustering:Gateways:0"]);
    }

    [Fact]
    public void ANameThatResolvesLater_IsRetriedUntilItDoes()
    {
        var attempts = 0;

        var overrides = Resolve(
            Configuration("worldserver:30000"),
            _ => ++attempts < 3 ? throw new SocketException() : [IPAddress.Parse("10.43.12.7")]);

        Assert.Equal(3, attempts);
        Assert.Equal("10.43.12.7:30000", overrides["Orleans:Clustering:Gateways:0"]);
    }

    [Fact]
    public void ANameThatNeverResolves_FailsClosed_NamingTheSetting()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(
            Configuration("nowhere.invalid:30000"),
            _ => throw new SocketException(),
            timeout: TimeSpan.FromMilliseconds(50)));

        Assert.Contains("Orleans:Clustering:Gateways:0", error.Message);
        Assert.Contains("nowhere.invalid", error.Message);
    }

    [Fact]
    public void AnAnswerWithOnlyIPv6_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(
            Configuration("worldserver:30000"),
            _ => [IPAddress.IPv6Loopback],
            timeout: TimeSpan.FromMilliseconds(50)));
    }

    [Theory]
    [InlineData("worldserver")]
    [InlineData("worldserver:")]
    [InlineData(":30000")]
    [InlineData("worldserver:0")]
    [InlineData("worldserver:70000")]
    [InlineData("worldserver:http")]
    public void AMalformedEntry_IsRejected(string value)
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(
            Configuration(value),
            _ => [IPAddress.Parse("10.43.12.7")]));
    }

    [Fact]
    public void TheOverride_ReplacesTheNameInTheEffectiveConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Orleans:Clustering:Gateways:0"] = "worldserver:30000" });
        var original = builder.Build();

        var overrides = Resolve(original, _ => [IPAddress.Parse("10.43.12.7")]);
        var effective = builder.AddInMemoryCollection(overrides).Build();

        Assert.Equal("10.43.12.7:30000", effective["Orleans:Clustering:Gateways:0"]);
    }
}
