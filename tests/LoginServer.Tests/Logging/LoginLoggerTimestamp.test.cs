using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Tests.Logging;

public sealed class LoginLoggerTimestampTests
{
    [Fact]
    public void Info_PrefixesTimestamp_WhenFormatProvided()
    {
        // Arrange: a scope, not Console.SetOut/Configure: nothing global changes, so tests running in parallel can neither
        // pollute this output nor be affected by this configuration.
        var writer = new StringWriter();
        var config = new LoginConfig { TimestampFormat = "[%Y]", ConsoleSilent = 0, ConsoleMsgLog = 0, ConsoleLogFilePath = string.Empty };

        // Act
        using (LoginLogger.UseScope(writer, config))
        {
            LoginLogger.Info("hello");
        }

        // Assert
        Assert.Equal($"[{DateTime.Now:yyyy}] hello", writer.ToString().Trim());
    }

    [Fact]
    public void Scopes_AreIsolatedFromEachOther_AndFromTheConsole()
    {
        var first = new StringWriter();
        var second = new StringWriter();
        var plain = new LoginConfig { TimestampFormat = string.Empty, ConsoleSilent = 0, ConsoleMsgLog = 0, ConsoleLogFilePath = string.Empty };
        var dated = new LoginConfig { TimestampFormat = "[%Y]", ConsoleSilent = 0, ConsoleMsgLog = 0, ConsoleLogFilePath = string.Empty };

        using (LoginLogger.UseScope(first, plain))
        {
            Task.Run(() =>
            {
                using (LoginLogger.UseScope(second, dated))
                {
                    LoginLogger.Info("from the other flow");
                }
            }).GetAwaiter().GetResult();
            LoginLogger.Info("from this flow");
        }

        Assert.Equal("from this flow", first.ToString().Trim());
        Assert.EndsWith("from the other flow", second.ToString().Trim(), StringComparison.Ordinal);
        Assert.StartsWith("[", second.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleSilentMask_SuppressesTheLevel_WithinAScope()
    {
        var writer = new StringWriter();
        var config = new LoginConfig { TimestampFormat = string.Empty, ConsoleSilent = 1, ConsoleMsgLog = 0, ConsoleLogFilePath = string.Empty }; // 1 = Info

        using (LoginLogger.UseScope(writer, config))
        {
            LoginLogger.Info("quiet");
            LoginLogger.Warning("loud");
        }

        Assert.Equal("loud", writer.ToString().Trim());
    }
}
