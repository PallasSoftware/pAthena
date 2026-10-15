using System.Net;
using System.Net.Sockets;
using Athena.Shared.Net;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class AdvertisedIpv4Tests
{
    private sealed class FakeDns
    {
        public IPAddress[] Answer { get; set; } = [IPAddress.Parse("203.0.113.10")];
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public IPAddress[] Resolve(string host)
        {
            Calls++;
            return Fail ? throw new SocketException((int)SocketError.HostNotFound) : Answer;
        }
    }

    [Fact]
    public void Literal_Ipv4_IsUsedWithoutResolution()
    {
        var dns = new FakeDns();
        var advertised = new AdvertisedIpv4("62.194.17.160", dns.Resolve);

        Assert.Equal(IPAddress.Parse("62.194.17.160"), advertised.Current);
        Assert.Equal(0, dns.Calls);
    }

    [Fact]
    public void Hostname_ResolvesToIpv4_AndSkipsIpv6Answers()
    {
        var dns = new FakeDns { Answer = [IPAddress.IPv6Loopback, IPAddress.Parse("203.0.113.10")] };

        var advertised = new AdvertisedIpv4("ragnarok.pathena.eu", dns.Resolve);

        Assert.Equal(AddressFamily.InterNetwork, advertised.Current.AddressFamily);
        Assert.Equal(new byte[] { 203, 0, 113, 10 }, advertised.Current.GetAddressBytes());
    }

    [Fact]
    public void Hostname_IsCachedForTheTtl_ThenResolvedAgain()
    {
        var dns = new FakeDns();
        var now = DateTimeOffset.UnixEpoch;
        var advertised = new AdvertisedIpv4("ragnarok.pathena.eu", dns.Resolve, () => now, TimeSpan.FromSeconds(60), _ => { });

        now = now.AddSeconds(59);
        dns.Answer = [IPAddress.Parse("203.0.113.99")];
        Assert.Equal(IPAddress.Parse("203.0.113.10"), advertised.Current);
        Assert.Equal(1, dns.Calls);

        now = now.AddSeconds(2);
        Assert.Equal(IPAddress.Parse("203.0.113.99"), advertised.Current);
        Assert.Equal(2, dns.Calls);
    }

    [Fact]
    public void Startup_FailsClearly_WhenTheHostnameDoesNotResolve()
    {
        var dns = new FakeDns { Fail = true };

        var ex = Assert.Throws<InvalidOperationException>(() => new AdvertisedIpv4("ragnarok.pathena.eu", dns.Resolve));

        Assert.Contains("ragnarok.pathena.eu", ex.Message);
    }

    [Fact]
    public void Startup_FailsClearly_WhenTheHostnameHasNoIpv4Record()
    {
        var dns = new FakeDns { Answer = [IPAddress.IPv6Loopback] };

        Assert.Throws<InvalidOperationException>(() => new AdvertisedIpv4("ragnarok.pathena.eu", dns.Resolve));
    }

    [Fact]
    public void LaterLookupFailure_KeepsTheLastGoodAddress_AndReportsIt()
    {
        var dns = new FakeDns();
        var now = DateTimeOffset.UnixEpoch;
        var errors = new List<string>();
        var advertised = new AdvertisedIpv4("ragnarok.pathena.eu", dns.Resolve, () => now, TimeSpan.FromSeconds(60), errors.Add);

        dns.Fail = true;
        now = now.AddSeconds(61);

        Assert.Equal(IPAddress.Parse("203.0.113.10"), advertised.Current);
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("not a host")]
    [InlineData("")]
    public void InvalidValues_AreRejected(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AdvertisedIpv4(value, new FakeDns().Resolve));
    }

    [Fact]
    public void FromEnvironment_UsesTheFallback_WhenUnset_AndFailsOnGarbage()
    {
        const string name = "ATHENA_TEST_ADVERTISED_IPV4";
        try
        {
            Environment.SetEnvironmentVariable(name, null);
            Assert.Equal(IPAddress.Parse("128.241.92.43"), AdvertisedIpv4.FromEnvironment(name, "128.241.92.43").Current);

            Environment.SetEnvironmentVariable(name, "::1");
            var ex = Assert.Throws<InvalidOperationException>(() => AdvertisedIpv4.FromEnvironment(name, "128.241.92.43"));
            Assert.Contains(name, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
