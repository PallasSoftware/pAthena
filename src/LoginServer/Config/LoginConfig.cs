using System.Net;
using Athena.Shared.Net;

namespace Athena.Net.LoginServer.Config;

public sealed class LoginConfig
{
    /// <summary>Public CharServer address for clients: ATHENA_NET_LOGIN_IRO_CHAR_IP, an IPv4 or a hostname resolved to IPv4.</summary>
    public AdvertisedIpv4 IroAdvertisedChar { get; init; } = AdvertisedIpv4.FromEnvironment(
        "ATHENA_NET_LOGIN_IRO_CHAR_IP", "128.241.92.43");
    public IPAddress IroAdvertisedCharIp => IroAdvertisedChar.Current;
    public int IroAdvertisedCharPort { get; init; } = ParseIntEnvironment(
        "ATHENA_NET_LOGIN_IRO_CHAR_PORT", 4500);
    /// <summary>
    /// ATHENA_NET_LOGIN_PROXY_PORT: a second listener whose connections must each start with a PROXY protocol v2 header (the real
    /// client address behind a TCP load balancer). 0 / unset (default) = no such listener. The port carries no authentication of
    /// the proxy: restrict who can reach it in the network (the ordinary LoginPort stays for direct connections, e.g. CharServer).
    /// </summary>
    public int ProxyProtocolPort { get; init; } = ParseProxyPort();
    public IPAddress BindIp { get; init; } = IPAddress.Any;
    public int LoginPort { get; init; } = 6900;
    public bool LogLogin { get; init; } = true;
    public string DateFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";
    public int AccountNameMinLength { get; init; } = 6;
    public int PasswordMinLength { get; init; } = 6;
    public int GroupIdToConnect { get; init; } = -1;
    public int MinGroupIdToConnect { get; init; } = -1;
    public bool UseWebAuthToken { get; init; }
    public int DisableWebTokenDelayMs { get; init; }
    public int MaxChars { get; init; } = 15;
    public int MaxCharVip { get; init; } = 6;
    public int MaxCharBilling { get; init; }
    public int CharPerAccount { get; init; } = 9;
    public int VipGroupId { get; init; } = 5;
    public int VipCharIncrease { get; init; } = 6;
    public bool IpBanEnabled { get; init; }
    public bool DynamicPassFailureBan { get; init; }
    public int DynamicPassFailureBanIntervalMinutes { get; init; } = 5;
    public int DynamicPassFailureBanLimit { get; init; } = 7;
    public int DynamicPassFailureBanDurationMinutes { get; init; } = 5;
    public bool UseDnsbl { get; init; }
    public string DnsblServers { get; init; } = string.Empty;
    public int IpBanCleanupIntervalSeconds { get; init; } = 60;
    public bool ConsoleEnabled { get; init; }
    public int ConsoleMsgLog { get; init; }
    public int ConsoleSilent { get; init; }
    public string ConsoleLogFilePath { get; init; } = string.Empty;
    public string TimestampFormat { get; init; } = string.Empty;
    public int IpSyncIntervalMinutes { get; init; }
    public bool UsercountDisable { get; init; }
    public int UsercountLow { get; init; } = 200;
    public int UsercountMedium { get; init; } = 500;
    public int UsercountHigh { get; init; } = 1000;

    private static int ParseProxyPort() => ParseProxyPort(Environment.GetEnvironmentVariable("ATHENA_NET_LOGIN_PROXY_PORT"));

    /// <summary>The parsing of ATHENA_NET_LOGIN_PROXY_PORT: unset/blank = 0 (no proxy listener), otherwise a port or a startup error.</summary>
    public static int ParseProxyPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        return int.TryParse(value, out var port) && port is > 0 and <= ushort.MaxValue
            ? port
            : throw new InvalidOperationException($"ATHENA_NET_LOGIN_PROXY_PORT must be a port number (1-65535), got '{value}'.");
    }

    private static int ParseIntEnvironment(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value is > 0 and <= ushort.MaxValue
            ? value
            : fallback;
    }
}
