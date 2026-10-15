using System.Net;
using Athena.Shared.Net;

namespace Athena.Net.LoginServer.Tests.Net;

public sealed class ProxyProtocolV2Tests
{
    private static readonly byte[] Sig = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];
    private static readonly IPEndPoint Traefik = new(IPAddress.Parse("10.42.0.7"), 40000);
    private static readonly byte[] LoginPacket = [0x64, 0x00, 1, 2, 3];

    private static byte[] Header(byte verCmd, byte famProto, byte[] payload) =>
        [.. Sig, verCmd, famProto, (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];

    private static byte[] V4(string src, ushort srcPort) =>
        [.. IPAddress.Parse(src).GetAddressBytes(), .. IPAddress.Parse("203.0.113.5").GetAddressBytes(),
         (byte)(srcPort >> 8), (byte)srcPort, 0x1A, 0xE4];

    private static async Task<(IPEndPoint Client, MemoryStream Stream)> Read(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        var client = await ProxyProtocolV2.ReadClientAsync(stream, Traefik, TimeSpan.FromSeconds(1), CancellationToken.None);
        return (client, stream);
    }

    [Fact]
    public async Task ValidIpv4Header_GivesTheRealClient_AndConsumesOnlyTheHeader()
    {
        var bytes = Header(0x21, 0x11, V4("203.0.113.77", 55123)).Concat(LoginPacket).ToArray();
        var (client, stream) = await Read(bytes);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.77"), 55123), client);
        Assert.Equal(bytes.Length - LoginPacket.Length, stream.Position);
        Assert.Equal(LoginPacket, bytes[(int)stream.Position..]);
    }

    [Fact]
    public async Task ValidIpv6Header_GivesTheRealClient()
    {
        var src = IPAddress.Parse("2001:db8::10");
        byte[] payload = [.. src.GetAddressBytes(), .. IPAddress.Parse("2001:db8::1").GetAddressBytes(), 0x01, 0xBB, 0x1A, 0xE4];

        Assert.Equal(new IPEndPoint(src, 0x01BB), (await Read(Header(0x21, 0x21, payload))).Client);
    }

    [Fact]
    public async Task LocalCommand_AndUnspecifiedFamily_KeepThePeer_ForProxyHealthChecks()
    {
        Assert.Equal(Traefik, (await Read(Header(0x20, 0x00, []))).Client);
        Assert.Equal(Traefik, (await Read(Header(0x21, 0x00, []))).Client);
    }

    [Fact]
    public async Task ConnectionWithoutAHeader_IsRejected_NotAttributedToTheProxy()
    {
        var ex = await Assert.ThrowsAsync<ProxyProtocolException>(() => Read([.. LoginPacket, .. new byte[20]]));
        Assert.Contains("10.42.0.7", ex.Message);
    }

    [Fact]
    public async Task BadVersion_Truncated_Oversized_AndUnknownFamilyHeaders_AreRejected()
    {
        byte[] oversized = [.. Sig, 0x21, 0x11, 0xFF, 0xFF];
        foreach (var bytes in new[]
        {
            Header(0x11, 0x11, V4("203.0.113.77", 1)),
            Header(0x21, 0x11, V4("203.0.113.77", 1))[..20],
            Header(0x21, 0x11, [1, 2, 3, 4]),
            oversized,
            Header(0x21, 0x31, new byte[216]),
            Array.Empty<byte>(),
        })
        {
            await Assert.ThrowsAsync<ProxyProtocolException>(() => Read(bytes));
        }
    }

    [Fact]
    public async Task ASilentPeer_TimesOut()
    {
        using var never = new NeverEndingStream();
        await Assert.ThrowsAsync<ProxyProtocolException>(() =>
            ProxyProtocolV2.ReadClientAsync(never, Traefik, TimeSpan.FromMilliseconds(50), CancellationToken.None));
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

public sealed class ProxyListenerConfigTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("6901", 6901)]
    public void ProxyPort_IsOffByDefault_AndParsed(string? value, int expected)
    {
        Assert.Equal(expected, Athena.Net.LoginServer.Config.LoginConfig.ParseProxyPort(value));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    public void ProxyPort_InvalidValue_StopsStartup(string value)
    {
        Assert.Throws<InvalidOperationException>(() => Athena.Net.LoginServer.Config.LoginConfig.ParseProxyPort(value));
    }
}
