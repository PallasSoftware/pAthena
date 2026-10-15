using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Tests.Logging;

public sealed class LoginLoggerTests
{
    [Fact]
    public void Configure_AppliesTimestampFormat_AndFileLogging()
    {
        // The scope applies the same settings Configure would, without changing the process-wide logger.
        var writer = new StringWriter();
        var file = Path.Combine(Path.GetTempPath(), $"login-logger-{Guid.NewGuid():N}.log");
        var config = new LoginConfig
        {
            TimestampFormat = "[%Y-%m-%d]",
            ConsoleSilent = 0,
            ConsoleMsgLog = 1, // Info goes to the file too
            ConsoleLogFilePath = file,
        };

        try
        {
            using (LoginLogger.UseScope(writer, config))
            {
                LoginLogger.Info("hello");
            }

            var expected = $"[{DateTime.Now:yyyy-MM-dd}] hello";
            Assert.Equal(expected, writer.ToString().Trim());
            Assert.Equal(expected, File.ReadAllText(file).Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }
}
