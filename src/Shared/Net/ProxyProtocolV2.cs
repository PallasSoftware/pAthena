using System.Buffers.Binary;
using System.Net;

namespace Athena.Shared.Net;

/// <summary>Thrown when a connection from a trusted proxy does not start with a valid PROXY protocol v2 header.</summary>
public sealed class ProxyProtocolException(string message) : IOException(message);

/// <summary>
/// PROXY protocol v2 (binary) for raw TCP: how a TCP load balancer such as Traefik reports the real client address. It is meant
/// for a dedicated listener that only the proxy can reach (restricted by the network, for example a Kubernetes NetworkPolicy):
/// every connection to it must start with a valid header, and a missing or malformed one fails the connection rather than
/// attributing the proxy's address to a player. Connections on the ordinary listener never carry or parse a header, so a
/// client can not claim another address there.
/// </summary>
public static class ProxyProtocolV2
{
    private static readonly byte[] Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];
    private const int FixedLength = 16;
    private const int MaxPayload = 536; // addresses plus TLVs; far more than any proxy sends for TCP

    /// <summary>
    /// Consumes exactly one PROXY protocol v2 header from <paramref name="stream"/> (within <paramref name="timeout"/>) and returns
    /// the client address it carries; the proxy's own <paramref name="peer"/> for a LOCAL command (proxy health check) or an
    /// unspecified/non-TCP family. Throws <see cref="ProxyProtocolException"/> for anything else.
    /// </summary>
    public static async Task<IPEndPoint> ReadClientAsync(
        Stream stream, IPEndPoint peer, TimeSpan timeout, CancellationToken cancellationToken)
    {
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
            throw new ProxyProtocolException($"Connection from {peer.Address} closed before a complete PROXY header.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProxyProtocolException($"Connection from {peer.Address} sent no PROXY header in time.");
        }
    }

    /// <summary>Interprets a fixed 16 byte header and its payload. Public for tests and for callers that already hold the bytes.</summary>
    public static IPEndPoint Interpret(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, IPEndPoint peer)
    {
        if (header.Length != FixedLength || !header[..12].SequenceEqual(Signature))
        {
            throw new ProxyProtocolException($"Connection from {peer.Address} did not start with a PROXY protocol v2 header.");
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
