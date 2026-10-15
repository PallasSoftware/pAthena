using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Athena.Shared.Net;

/// <summary>Thrown when a connection from a trusted proxy does not start with a valid PROXY protocol v2 header.</summary>
public sealed class ProxyProtocolException(string message) : IOException(message);

/// <summary>
/// The addresses that may send a PROXY protocol v2 header (a load balancer such as Traefik): a comma separated list of IP
/// addresses and CIDR ranges. Empty means PROXY protocol is off and every connection is a direct one.
/// </summary>
public sealed class TrustedProxies
{
    private readonly (IPAddress Network, int Bits)[] _ranges;

    public static TrustedProxies None { get; } = new([]);

    private TrustedProxies((IPAddress, int)[] ranges) => _ranges = ranges;

    public bool Enabled => _ranges.Length > 0;

    public static TrustedProxies Parse(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return None;
        }

        var ranges = new List<(IPAddress, int)>();
        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var network))
            {
                throw new FormatException($"'{item}' is not an IP address or CIDR range.");
            }

            network = Normalize(network);
            var max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var bits = max;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > max))
            {
                throw new FormatException($"'{item}' has an invalid prefix length.");
            }

            ranges.Add((network, bits));
        }

        return new TrustedProxies([.. ranges]);
    }

    public bool Contains(IPAddress address)
    {
        address = Normalize(address);
        foreach (var (network, bits) in _ranges)
        {
            if (network.AddressFamily == address.AddressFamily && SamePrefix(network.GetAddressBytes(), address.GetAddressBytes(), bits))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool SamePrefix(byte[] a, byte[] b, int bits)
    {
        var whole = bits / 8;
        for (var i = 0; i < whole; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        var rest = bits % 8;
        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (a[whole] & mask) == (b[whole] & mask);
    }
}

/// <summary>
/// PROXY protocol v2 (binary) for raw TCP: the one real-client-IP mechanism a TCP load balancer offers. A header is trusted only
/// when the TCP peer is a configured trusted proxy; any other peer is a direct connection and nothing is read from it, so
/// a client can never claim another address. From a trusted proxy the header is mandatory: a missing or malformed one fails
/// the connection (the proxy is misconfigured; guessing would attribute the proxy's address to a player).
/// </summary>
public static class ProxyProtocolV2
{
    private static readonly byte[] Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];
    private const int FixedLength = 16;
    private const int MaxPayload = 536; // addresses plus TLVs; far more than any proxy sends for TCP

    /// <summary>
    /// The client address of a new connection. For an untrusted peer: the peer itself, nothing is consumed. For a trusted proxy:
    /// consumes exactly one header from <paramref name="stream"/> and returns the address it carries (the peer itself for LOCAL or
    /// an unspecified family, as sent by proxy health checks).
    /// </summary>
    public static async Task<IPEndPoint> ResolveClientAsync(
        Stream stream, IPEndPoint peer, TrustedProxies trusted, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!trusted.Enabled || !trusted.Contains(peer.Address))
        {
            return peer;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var header = new byte[FixedLength];
            await stream.ReadExactlyAsync(header, cts.Token);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14, 2));
            var payload = new byte[length <= MaxPayload ? length : 0];
            if (length > MaxPayload)
            {
                throw new ProxyProtocolException($"PROXY header from {peer.Address} is too long ({length} bytes).");
            }

            await stream.ReadExactlyAsync(payload, cts.Token);
            return Interpret(header, payload, peer);
        }
        catch (EndOfStreamException)
        {
            throw new ProxyProtocolException($"Connection from trusted proxy {peer.Address} closed before a complete PROXY header.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProxyProtocolException($"Trusted proxy {peer.Address} sent no PROXY header in time.");
        }
    }

    /// <summary>Interprets a fixed 16 byte header and its payload. Public for tests and for callers that already hold the bytes.</summary>
    public static IPEndPoint Interpret(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, IPEndPoint peer)
    {
        if (header.Length != FixedLength || !header[..12].SequenceEqual(Signature))
        {
            throw new ProxyProtocolException($"Connection from trusted proxy {peer.Address} did not start with a PROXY protocol v2 header.");
        }

        var version = header[12] >> 4;
        var command = header[12] & 0x0F;
        if (version != 2 || command > 1)
        {
            throw new ProxyProtocolException($"Unsupported PROXY protocol version/command 0x{header[12]:X2} from {peer.Address}.");
        }

        if (command == 0)
        {
            return peer; // LOCAL: the proxy's own connection (health check)
        }

        var family = header[13] >> 4;
        var transport = header[13] & 0x0F;
        if (family == 0 || transport != 1)
        {
            return peer; // UNSPEC or not TCP: nothing usable, keep the peer
        }

        if (family == 1)
        {
            if (payload.Length < 12) throw new ProxyProtocolException("PROXY header: truncated IPv4 address block.");
            return new IPEndPoint(new IPAddress(payload.Slice(0, 4)), BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2)));
        }

        if (family == 2)
        {
            if (payload.Length < 36) throw new ProxyProtocolException("PROXY header: truncated IPv6 address block.");
            return new IPEndPoint(new IPAddress(payload.Slice(0, 16)), BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(32, 2)));
        }

        throw new ProxyProtocolException($"PROXY header: unsupported address family {family}.");
    }
}
