using System.Globalization;
using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Logging;

public enum LogLevel
{
    Info,
    Status,
    Notice,
    Warning,
    Error,
    Debug
}

public static class LoginLogger
{
    private static readonly object LockObj = new();
    private static int _consoleSilentMask;
    private static int _fileMask;
    private static string _filePath = string.Empty;
    private static string _timestampFormat = string.Empty;
    private static bool _configured;
    private static Action<LogLevel>? _telemetrySink;

    // A scope (see UseScope) redirects the output and the settings of the logging done within one async flow, so that tests can
    // observe their own log lines without touching the process-wide Console.Out or the global configuration, which other
    // tests running in parallel use too.
    private sealed record Scope(TextWriter Output, int ConsoleSilentMask, int FileMask, string FilePath, string TimestampFormat);
    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    /// <summary>
    /// Sends everything logged within the current async flow (and the tasks it starts) to <paramref name="output"/> using the
    /// settings of <paramref name="config"/>, and nothing else. Other flows keep using the console and the global configuration.
    /// Dispose to leave the scope.
    /// </summary>
    public static IDisposable UseScope(TextWriter output, LoginConfig config)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new Scope(output, config.ConsoleSilent, config.ConsoleMsgLog, config.ConsoleLogFilePath ?? string.Empty, config.TimestampFormat ?? string.Empty);
        return new ScopeExit(previous);
    }

    private sealed class ScopeExit(Scope? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }

    public static void SetTelemetrySink(Action<LogLevel>? sink)
    {
        _telemetrySink = sink;
    }

    public static void Configure(LoginConfig config)
    {
        _consoleSilentMask = config.ConsoleSilent;
        _fileMask = config.ConsoleMsgLog;
        _filePath = config.ConsoleLogFilePath ?? string.Empty;
        _timestampFormat = config.TimestampFormat ?? string.Empty;
        _configured = true;
    }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Status(string message) => Write(LogLevel.Status, message);
    public static void Notice(string message) => Write(LogLevel.Notice, message);
    public static void Warning(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    private static void Write(LogLevel level, string message)
    {
        _telemetrySink?.Invoke(level);

        var scope = CurrentScope.Value;
        var configured = scope is not null || _configured;
        var silent = scope?.ConsoleSilentMask ?? _consoleSilentMask;
        var fileMask = scope?.FileMask ?? _fileMask;
        var filePath = scope?.FilePath ?? _filePath;

        var mask = MaskFor(level);
        var line = message;
        var prefix = FormatTimestampPrefix(scope?.TimestampFormat ?? _timestampFormat);
        if (!string.IsNullOrEmpty(prefix))
        {
            line = $"{prefix} {message}";
        }

        if (!configured || (silent & mask) == 0)
        {
            (scope?.Output ?? Console.Out).WriteLine(line);
        }

        if (configured && (fileMask & mask) != 0 && !string.IsNullOrWhiteSpace(filePath))
        {
            AppendToFile(filePath, line);
        }
    }

    private static int MaskFor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Info => 1,
            LogLevel.Status => 2,
            LogLevel.Notice => 4,
            LogLevel.Warning => 8,
            LogLevel.Error => 16,
            LogLevel.Debug => 32,
            _ => 1,
        };
    }

    private static void AppendToFile(string filePath, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (LockObj)
            {
                File.AppendAllText(filePath, message + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Ignore file logging failures.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore file logging failures.
        }
    }

    private static string FormatTimestampPrefix(string timestampFormat)
    {
        if (string.IsNullOrWhiteSpace(timestampFormat))
        {
            return string.Empty;
        }

        var format = ConvertTimestampFormat(timestampFormat);
        try
        {
            return DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string ConvertTimestampFormat(string format)
    {
        return format
            .Replace("%Y", "yyyy", StringComparison.Ordinal)
            .Replace("%m", "MM", StringComparison.Ordinal)
            .Replace("%d", "dd", StringComparison.Ordinal)
            .Replace("%H", "HH", StringComparison.Ordinal)
            .Replace("%I", "hh", StringComparison.Ordinal)
            .Replace("%M", "mm", StringComparison.Ordinal)
            .Replace("%S", "ss", StringComparison.Ordinal)
            .Replace("%b", "MMM", StringComparison.Ordinal)
            .Replace("%p", "tt", StringComparison.Ordinal);
    }
}
