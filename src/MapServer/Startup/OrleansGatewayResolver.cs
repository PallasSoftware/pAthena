using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace Athena.Net.MapServer.Startup;

/// <summary>
/// Orleans' static client gateway list (<c>Orleans:Clustering:Gateways:N</c>, environment variable
/// <c>Orleans__Clustering__Gateways__N</c>) only accepts IP literals: with a host name the client finds no gateway and
/// MapServer never connects to the World. Kubernetes (and Docker networks) only offer a stable Service <em>name</em>, so this
/// resolves every non-literal <c>host:port</c> entry to its IPv4 address once, at startup, and returns the replacement
/// values for the configuration (the caller adds them as an in-memory override before building the Orleans client).
/// <para>
/// Fail closed: a name that cannot be resolved within <see cref="DefaultTimeout"/> throws, so MapServer does not run
/// without a world instead of retrying a wrong address forever. The address is resolved once; a Service name
/// that maps to a stable ClusterIP (the World's Service) keeps working across World restarts, while a headless name
/// would not - point the gateway at a ClusterIP Service.
/// </para>
/// Entries that are already IP literals (<c>127.0.0.1:30000</c>, <c>[::1]:30000</c>) or Orleans URIs
/// (<c>gwy.tcp://host:port/0</c>) are left exactly as they are, so existing local and Aspire configuration is unchanged.
/// </summary>
public static class OrleansGatewayResolver
{
    public const string GatewaysSection = "Orleans:Clustering:Gateways";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static IReadOnlyDictionary<string, string?> Resolve(
        IConfiguration configuration,
        Func<string, IReadOnlyList<IPAddress>>? lookup = null,
        TimeSpan? timeout = null,
        TimeSpan? retryDelay = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lookup ??= static host => Dns.GetHostAddresses(host);
        var limit = timeout ?? DefaultTimeout;
        var delay = retryDelay ?? TimeSpan.FromSeconds(1);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.GetSection(GatewaysSection).GetChildren())
        {
            var value = entry.Value?.Trim();
            if (string.IsNullOrEmpty(value) || value.Contains("://", StringComparison.Ordinal))
            {
                continue;
            }

            var (host, port) = Split(value, entry.Path);
            if (IPAddress.TryParse(host, out _))
            {
                continue;
            }

            var address = ResolveIPv4(host, entry.Path, lookup, limit, delay);
            overrides[entry.Path] = $"{address}:{port}";
            log?.Invoke($"Orleans gateway {entry.Key}: {host} -> {address}:{port}");
        }

        return overrides;
    }

    private static (string Host, int Port) Split(string value, string path)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new InvalidOperationException($"{path} must be <host>:<port>, got '{value}'.");
        }

        var host = value[..separator].Trim('[', ']');
        if (!int.TryParse(value[(separator + 1)..], out var port) || port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException($"{path} has no valid port: '{value}'.");
        }

        return (host, port);
    }

    private static IPAddress ResolveIPv4(
        string host,
        string path,
        Func<string, IReadOnlyList<IPAddress>> lookup,
        TimeSpan timeout,
        TimeSpan delay)
    {
        var started = DateTime.UtcNow;
        Exception? lastError = null;
        while (true)
        {
            try
            {
                var candidates = lookup(host)
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .OrderBy(address => address.ToString(), StringComparer.Ordinal)
                    .ToArray();
                if (candidates.Length > 0)
                {
                    return candidates[0];
                }
            }
            catch (SocketException ex)
            {
                lastError = ex;
            }

            if (DateTime.UtcNow - started >= timeout)
            {
                throw new InvalidOperationException(
                    $"{path}: '{host}' did not resolve to an IPv4 address within {timeout.TotalSeconds:0}s" +
                    (lastError is null ? "." : $" ({lastError.Message})."),
                    lastError);
            }

            Thread.Sleep(delay);
        }
    }
}
