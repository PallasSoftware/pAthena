using System.Net;
using System.Net.Sockets;

namespace Athena.Shared.Net;

/// <summary>
/// The public IPv4 address handed to Ragnarok clients (server list, zone-server packet). The original client only understands a
/// 4-byte IPv4 address, so a configured hostname is resolved here. A literal IPv4 is used as is. A hostname is resolved once at
/// startup (failure is fatal: never advertise a wrong address) and re-resolved when the cached answer is older than the TTL, so a
/// changed WAN address reaches new client connections without a restart. If a later lookup fails the last good address is kept.
/// </summary>
public sealed class AdvertisedIpv4
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Func<string, IPAddress[]> _resolve;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _reportError;
    private readonly TimeSpan _ttl;
    private IPAddress _current;
    private DateTimeOffset _resolvedAt;

    public AdvertisedIpv4(
        string hostOrIp,
        Func<string, IPAddress[]>? resolve = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? ttl = null,
        Action<string>? reportError = null)
    {
        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            throw new ArgumentException("The advertised address must not be empty.", nameof(hostOrIp));
        }

        Host = hostOrIp.Trim();
        _resolve = resolve ?? Dns.GetHostAddresses;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? DefaultTtl;
        _reportError = reportError ?? (message => Console.Error.WriteLine(message));

        if (IPAddress.TryParse(Host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException($"The advertised address '{Host}' is not an IPv4 address.", nameof(hostOrIp));
            }

            _current = literal;
            IsLiteral = true;
        }
        else
        {
            if (Uri.CheckHostName(Host) != UriHostNameType.Dns)
            {
                throw new ArgumentException($"The advertised address '{Host}' is neither an IPv4 address nor a valid hostname.", nameof(hostOrIp));
            }

            _current = ResolveOrThrow();
            _resolvedAt = _clock();
        }
    }

    public string Host { get; }

    public bool IsLiteral { get; }

    /// <summary>The IPv4 address to put in a client packet right now.</summary>
    public IPAddress Current
    {
        get
        {
            if (IsLiteral)
            {
                return _current;
            }

            lock (_gate)
            {
                var now = _clock();
                if (now - _resolvedAt < _ttl)
                {
                    return _current;
                }

                try
                {
                    var resolved = ResolveOrThrow();
                    if (!resolved.Equals(_current))
                    {
                        _reportError($"Advertised address {Host} changed from {_current} to {resolved}.");
                    }

                    _current = resolved;
                }
                catch (InvalidOperationException ex)
                {
                    _reportError($"{ex.Message} Keeping the last good address {_current}.");
                }

                _resolvedAt = now;
                return _current;
            }
        }
    }

    /// <summary>
    /// ATHENA_NET_* style setting: unset or blank uses the fallback IPv4; a value is a literal IPv4 or a hostname.
    /// A value that is neither, or a hostname that does not resolve to IPv4, throws.
    /// </summary>
    public static AdvertisedIpv4 FromEnvironment(string name, string fallbackIp) =>
        FromValue(name, Environment.GetEnvironmentVariable(name), fallbackIp);

    /// <summary>The parsing of <see cref="FromEnvironment"/> for an already read value (testable without touching the process environment).</summary>
    public static AdvertisedIpv4 FromValue(string name, string? value, string fallbackIp)
    {
        try
        {
            return new AdvertisedIpv4(string.IsNullOrWhiteSpace(value) ? fallbackIp : value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{name}: {ex.Message}", ex);
        }
    }

    private IPAddress ResolveOrThrow()
    {
        IPAddress[] addresses;
        try
        {
            addresses = _resolve(Host);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new InvalidOperationException($"Cannot resolve the advertised hostname '{Host}': {ex.Message}", ex);
        }

        var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        return ipv4 ?? throw new InvalidOperationException($"The advertised hostname '{Host}' has no IPv4 (A) record.");
    }
}
