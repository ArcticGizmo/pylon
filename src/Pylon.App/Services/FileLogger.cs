using Microsoft.Extensions.Logging;

namespace Pylon.App.Services;

/// <summary>
/// Plain daily log files under {data}\logs, as Postern does: a tray app has no console, and when it misbehaves the
/// first question is "what did it say?".
/// </summary>
internal sealed class FileLoggerProvider(string directory, bool echoToConsole) : ILoggerProvider
{
    readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:HH:mm:ss.fff} {Short(level)} {category.Split('.').Last()}: {message}{(exception is null ? "" : Environment.NewLine + exception)}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"pylon-{now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            if (echoToConsole)
                Console.WriteLine(line);
        }
    }

    static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => "    ",
    };

    sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Kestrel/hosting chatter stays at warning; Pylon's own categories at information.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Pylon", StringComparison.Ordinal) ? LogLevel.Information : LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
