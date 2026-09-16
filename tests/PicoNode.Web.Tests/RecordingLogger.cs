namespace PicoNode.Web.Tests;

/// <summary>Records the last log entry and counts warnings/errors.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public int WarningCount => Entries.Count(e => e.Level == LogLevel.Warning);

    public Exception? LastException => Entries.Count > 0 ? Entries[^1].Exception : null;

    public void Log(
        LogLevel level,
        EventId eventId,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? args,
        Exception? exception
    ) => Entries.Add((level, message, exception));

    public void Log(
        LogLevel level,
        EventId eventId,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? args,
        Exception? exception
    ) => Log(level, eventId, message.Format, args, exception);

    public void Log(
        LogLevel level,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? args,
        Exception? exception
    ) => Log(level, new EventId(0), message, args, exception);

    public void Log(
        LogLevel level,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? args,
        Exception? exception
    ) => Log(level, new EventId(0), message.Format, args, exception);

    public Task LogAsync(
        LogLevel level,
        EventId eventId,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? args,
        Exception? exception,
        CancellationToken cancellationToken
    )
    {
        Log(level, eventId, message, args, exception);
        return Task.CompletedTask;
    }

    public Task LogAsync(
        LogLevel level,
        EventId eventId,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? args,
        Exception? exception,
        CancellationToken cancellationToken
    )
    {
        Log(level, eventId, message.Format, args, exception);
        return Task.CompletedTask;
    }

    public Task LogAsync(
        LogLevel level,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? args,
        Exception? exception,
        CancellationToken cancellationToken
    )
    {
        Log(level, new EventId(0), message, args, exception);
        return Task.CompletedTask;
    }

    public Task LogAsync(
        LogLevel level,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? args,
        Exception? exception,
        CancellationToken cancellationToken
    )
    {
        Log(level, new EventId(0), message.Format, args, exception);
        return Task.CompletedTask;
    }

    public bool IsEnabled(LogLevel level) => true;

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose() { }
    }
}
