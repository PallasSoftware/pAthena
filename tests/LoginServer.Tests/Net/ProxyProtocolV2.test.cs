using System.Net;
using Athena.Shared.Net;

namespace Athena.Net.LoginServer.Tests.Net;

public sealed class ProxyProtocolV2Tests
{
    private static readonly byte[] Sig = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];
    private static readonly IPEndPoint Traefik = new(IPAddress.Parse("10.42.0.7"), 40000);
    private static readonly IPEndPoint Stranger = new(IPAddress.Parse("198.51.100.9"), 51000);
    private static readonly TrustedProxies Trusted = TrustedProxies.Parse("10.42.0.0/16, 192.0.2.1");

    private static byte[] Header(byte verCmd, byte famProto, byte[] payload) =>
        [.. Sig, verCmd, famProto, (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];

    private static byte[] V4(string src, ushort srcPort) =>
        [.. IPAddress.Parse(src).GetAddressBytes(), .. IPAddress.Parse("203.0.113.5").GetAddressBytes(),
         (byte)(srcPort >> 8), (byte)srcPort, 0x1A, 0xE4];

    private static Task<IPEndPoint> Resolve(byte[] bytes, IPEndPoint peer, TrustedProxies? trusted, out MemoryStream stream)
    {
        stream = new MemoryStream(bytes);
        return ProxyProtocolV2.ResolveClientAsync(stream, peer, trusted ?? Trusted, TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    private static readonly byte[] LoginPacket = [0x64, 0x00, 1, 2, 3];

    [Fact]
    public async Task Disabled_ReadsNothing_AndKeepsThePeer()
    {
        var bytes = Header(0x21, 0x11, V4("203.0.113.77", 1234));
        var endpoint = await Resolve(bytes, Traefik, TrustedProxies.None, out var stream);

        Assert.Equal(Traefik, endpoint);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DirectConnection_FromAnUntrustedSource_IsNotParsed_EvenIfItSendsAHeader()
    {
        // A client (or an internal CharServer) must not be able to claim another address.
        var bytes = Header(0x21, 0x11, V4("203.0.113.77", 1234));
        var endpoint = await Resolve(bytes, Stranger, null, out var stream);

        Assert.Equal(Stranger, endpoint);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task TrustedProxy_ValidIpv4Header_GivesTheRealClient_AndConsumesOnlyTheHeader()
    {
        var bytes = Header(0x21, 0x11, V4("203.0.113.77", 55123)).Concat(LoginPacket).ToArray();
        var endpoint = await Resolve(bytes, Traefik, null, out var stream);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.77"), 55123), endpoint);
        Assert.Equal(bytes.Length - LoginPacket.Length, stream.Position);
        Assert.Equal(LoginPacket, bytes[(int)stream.Position..]);
    }

    [Fact]
    public async Task TrustedProxy_ValidIpv6Header_GivesTheRealClient()
    {
        var src = IPAddress.Parse("2001:db8::10");
        byte[] payload = [.. src.GetAddressBytes(), .. IPAddress.Parse("2001:db8::1").GetAddressBytes(), 0x01, 0xBB, 0x1A, 0xE4];
        var endpoint = await Resolve(Header(0x21, 0x21, payload), Traefik, null, out _);

        Assert.Equal(new IPEndPoint(src, 0x01BB), endpoint);
    }

    [Fact]
    public async Task TrustedProxy_LocalCommand_KeepsThePeer_ForProxyHealthChecks()
    {
        var endpoint = await Resolve(Header(0x20, 0x00, []), Traefik, null, out _);

        Assert.Equal(Traefik, endpoint);
    }

    [Fact]
    public async Task TrustedProxy_UnspecifiedFamily_KeepsThePeer()
    {
        var endpoint = await Resolve(Header(0x21, 0x00, []), Traefik, null, out _);

        Assert.Equal(Traefik, endpoint);
    }

    [Fact]
    public async Task TrustedProxy_WithoutAHeader_IsRejected()
    {
        // The proxy is misconfigured: a plain login packet from a trusted source must not be attributed to the proxy's address.
        var ex = await Assert.ThrowsAsync<ProxyProtocolException>(() => Resolve([.. LoginPacket, .. new byte[20]], Traefik, null, out _));
        Assert.Contains("10.42.0.7", ex.Message);
    }

    [Fact]
    public async Task TrustedProxy_BadSignature_BadVersion_Truncated_AndOversizedHeaders_AreRejected()
    {
        var badVersion = Header(0x11, 0x11, V4("203.0.113.77", 1));
        var truncatedHeader = Header(0x21, 0x11, V4("203.0.113.77", 1))[..20];
        var truncatedAddresses = Header(0x21, 0x11, [1, 2, 3, 4]);
        byte[] oversized = [.. Sig, 0x21, 0x11, 0xFF, 0xFF];
        var unknownFamily = Header(0x21, 0x31, new byte[216]);
        foreach (var bytes in new[] { badVersion, truncatedHeader, truncatedAddresses, oversized, unknownFamily, Array.Empty<byte>() })
        {
            await Assert.ThrowsAsync<ProxyProtocolException>(() => Resolve(bytes, Traefik, null, out _));
        }
    }

    [Fact]
    public async Task TrustedProxy_ThatSendsNothing_TimesOut()
    {
        using var never = new NeverEndingStream();
        await Assert.ThrowsAsync<ProxyProtocolException>(() =>
            ProxyProtocolV2.ResolveClientAsync(never, Traefik, Trusted, TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Theory]
    [InlineData("10.42.9.9", true)]
    [InlineData("10.43.0.1", false)]
    [InlineData("192.0.2.1", true)]
    [InlineData("192.0.2.2", false)]
    [InlineData("::ffff:10.42.0.7", true)]
    public void TrustedProxies_MatchesAddressesAndCidrs(string address, bool expected)
    {
        Assert.Equal(expected, Trusted.Contains(IPAddress.Parse(address)));
    }

    [Fact]
    public void TrustedProxies_EmptyIsDisabled_AndInvalidEntriesAreRejected()
    {
        Assert.False(TrustedProxies.Parse(null).Enabled);
        Assert.False(TrustedProxies.Parse("  ").Enabled);
        Assert.False(TrustedProxies.None.Contains(IPAddress.Loopback));
        foreach (var bad in new[] { "10.0.0.0/33", "banana", "10.0.0.0/8/8", "10.0.0.1/x" })
        {
            Assert.Throws<FormatException>(() => TrustedProxies.Parse(bad));
        }
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
