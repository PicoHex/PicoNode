using PicoLog.Abs;

namespace PicoWeb.Samples;

/// <summary>
/// Minimal console logger that writes to <see cref="Console.Error"/>.
/// Designed for sample/diagnostic use — not for production.
/// </summary>
internal sealed class ConsoleLogger : ILogger
{
    private static readonly Dictionary<LogLevel, string> Labels = new()
    {
        [LogLevel.Trace] = "trce",
        [LogLevel.Debug] = "dbug",
        [LogLevel.Info] = "info",
        [LogLevel.Warning] = "warn",
        [LogLevel.Error] = "fail",
        [LogLevel.Critical] = "crit",
    };

    public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

    public void Log(
        LogLevel logLevel,
        EventId eventId,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception
    )
    {
        if (logLevel < LogLevel.Warning)
            return;

        var label = Labels.GetValueOrDefault(logLevel, "????");
        Console.Error.WriteLine($"[{label}] [{eventId.Id}] {message}");
        if (exception is not null)
            Console.Error.WriteLine($"  {exception}");
    }

    public Task LogAsync(
        LogLevel logLevel,
        EventId eventId,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception,
        CancellationToken cancellationToken = default
    )
    {
        Log(logLevel, eventId, message, properties, exception);
        return Task.CompletedTask;
    }

    public void Log(
        LogLevel logLevel,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception
    ) => Log(logLevel, default, message, properties, exception);

    public Task LogAsync(
        LogLevel logLevel,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception,
        CancellationToken cancellationToken = default
    )
    {
        Log(logLevel, default, message, properties, exception);
        return Task.CompletedTask;
    }

    public void Log(
        LogLevel logLevel,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception
    ) => Log(logLevel, default, message.ToString(), properties, exception);

    public Task LogAsync(
        LogLevel logLevel,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception,
        CancellationToken cancellationToken = default
    )
    {
        Log(logLevel, default, message.ToString(), properties, exception);
        return Task.CompletedTask;
    }

    public void Log(
        LogLevel logLevel,
        EventId eventId,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception
    ) => Log(logLevel, eventId, message.ToString(), properties, exception);

    public Task LogAsync(
        LogLevel logLevel,
        EventId eventId,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception,
        CancellationToken cancellationToken = default
    )
    {
        Log(logLevel, eventId, message.ToString(), properties, exception);
        return Task.CompletedTask;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    }
}
