using System.Collections.Concurrent;

namespace PicoNode.Http.Tests;

/// <summary>
/// Minimal recording <see cref="ILogger"/> for HTTP-layer tests: records every
/// call and can be configured to throw, so "a throwing user logger must not
/// destabilize the server" can be asserted.
/// </summary>
internal sealed class RecordingHttpLogger : ILogger
{
    public bool ThrowOnLog { get; init; }

    /// <summary>Optional hook for ordering assertions (invoked for every accepted call).</summary>
    public Action? OnLog { get; init; }

    public ConcurrentQueue<(
        LogLevel Level,
        EventId EventId,
        string? Message,
        Exception? Exception
    )> Calls { get; } = new();

    public LogLevel? LastLevel => Calls.TryPeek(out var c) ? c.Level : null;

    public string? LastMessage => Calls.TryPeek(out var c) ? c.Message : null;

    private void Record(LogLevel level, EventId eventId, string? message, Exception? exception)
    {
        Calls.Enqueue((level, eventId, message, exception));
        OnLog?.Invoke();
        if (ThrowOnLog)
        {
            throw new InvalidOperationException("Logger threw");
        }
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => throw new NotSupportedException();

    public void Log(LogLevel logLevel, string message, Exception? exception) =>
        Record(logLevel, default, message, exception);

    public void Log(
        LogLevel logLevel,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception
    ) => Record(logLevel, default, message, exception);

    public Task LogAsync(
        LogLevel logLevel,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        Record(logLevel, default, message, exception);
        return Task.CompletedTask;
    }

    public Task LogAsync(
        LogLevel logLevel,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        Record(logLevel, default, message, exception);
        return Task.CompletedTask;
    }

    public void Log(LogLevel logLevel, FormattableString message, Exception? exception = null) =>
        Record(logLevel, default, message.ToString(), exception);

    public void Log(
        LogLevel logLevel,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception = null
    ) => Record(logLevel, default, message.ToString(), exception);

    public Task LogAsync(
        LogLevel logLevel,
        FormattableString message,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        Record(logLevel, default, message.ToString(), exception);
        return Task.CompletedTask;
    }

    public Task LogAsync(
        LogLevel logLevel,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        Record(logLevel, default, message.ToString(), exception);
        return Task.CompletedTask;
    }

    public void Log(LogLevel logLevel, EventId eventId, string message, Exception? exception) =>
        Record(logLevel, eventId, message, exception);

    public void Log(
        LogLevel logLevel,
        EventId eventId,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception
    ) => Record(logLevel, eventId, message, exception);

    public Task LogAsync(
        LogLevel logLevel,
        EventId eventId,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        Record(logLevel, eventId, message, exception);
        return Task.CompletedTask;
    }

    public Task LogAsync(
        LogLevel logLevel,
        EventId eventId,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        Record(logLevel, eventId, message, exception);
        return Task.CompletedTask;
    }

    public void Log(
        LogLevel logLevel,
        EventId eventId,
        FormattableString message,
        Exception? exception = null
    ) => Record(logLevel, eventId, message.ToString(), exception);

    public void Log(
        LogLevel logLevel,
        EventId eventId,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception = null
    ) => Record(logLevel, eventId, message.ToString(), exception);

    public Task LogAsync(
        LogLevel logLevel,
        EventId eventId,
        FormattableString message,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        Record(logLevel, eventId, message.ToString(), exception);
        return Task.CompletedTask;
    }

    public Task LogAsync(
        LogLevel logLevel,
        EventId eventId,
        FormattableString message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        Record(logLevel, eventId, message.ToString(), exception);
        return Task.CompletedTask;
    }
}
