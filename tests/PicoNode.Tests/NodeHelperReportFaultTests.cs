namespace PicoNode.Tests;

public sealed class NodeHelperReportFaultTests
{
    [Test]
    public async Task ReportFault_WithNullLogger_DoesNotThrow()
    {
        await Assert.That(() =>
            NodeHelper.ReportFault(null, NodeFaultCode.StartFailed, "test.operation")
        ).ThrowsNothing();
    }

    [Test]
    public async Task ReportFault_WithThrowingLogger_SuppressesException()
    {
        var logger = new SpyLogger { ThrowOnLog = true };


        await Assert.That(() =>
            NodeHelper.ReportFault(logger, NodeFaultCode.StartFailed, "test.operation")
        ).ThrowsNothing();
    }

    [Test]
    public async Task ReportFault_LogsCorrectLevel_ForError()
    {
        var logger = new SpyLogger();

        NodeHelper.ReportFault(logger, NodeFaultCode.StartFailed, "tcp.start");

        await Assert.That(logger.LastLevel).IsEqualTo(LogLevel.Error);
    }

    [Test]
    public async Task ReportFault_LogsCorrectLevel_ForWarning()
    {
        var logger = new SpyLogger();

        NodeHelper.ReportFault(logger, NodeFaultCode.SessionRejected, "tcp.session");


        await Assert.That(logger.LastLevel).IsEqualTo(LogLevel.Warning);
    }

    [Test]
    public async Task ReportFault_PassesExceptionToLogger()
    {
        var logger = new SpyLogger();
        var exception = new InvalidOperationException("boom");

        NodeHelper.ReportFault(logger, NodeFaultCode.ReceiveFailed, "tcp.receive", exception);

        await Assert.That(logger.LastException).IsSameReferenceAs(exception);
    }
}

internal sealed record LogCall(LogLevel Level, EventId EventId, string? Message, Exception? Exception);

internal sealed class SpyLogger : ILogger
{
    public bool ThrowOnLog { get; init; }
    public ConcurrentQueue<LogCall> Calls { get; } = new();
    public LogLevel? LastLevel => Calls.TryPeek(out var c) ? c.Level : null;
    public Exception? LastException => Calls.TryPeek(out var c) ? c.Exception : null;
    public string? LastMessage => Calls.TryPeek(out var c) ? c.Message : null;

    private void Record(LogLevel level, EventId eventId, string? message, Exception? exception)
    {
        Calls.Enqueue(new LogCall(level, eventId, message, exception));
        if (ThrowOnLog) throw new InvalidOperationException("Logger threw");
    }

    public IDisposable BeginScope<TState>(TState state) => throw new NotSupportedException();

    public void Log(LogLevel logLevel, string message, Exception? exception) => Record(logLevel, default, message, exception);
    public void Log(LogLevel logLevel, string message, IReadOnlyList<KeyValuePair<string, object?>>? properties, Exception? exception) => Record(logLevel, default, message, exception);
    public Task LogAsync(LogLevel logLevel, string message, Exception? exception = null, CancellationToken cancellationToken = default) { Record(logLevel, default, message, exception); return Task.CompletedTask; }
    public Task LogAsync(LogLevel logLevel, string message, IReadOnlyList<KeyValuePair<string, object?>>? properties, Exception? exception = null, CancellationToken cancellationToken = default) { Record(logLevel, default, message, exception); return Task.CompletedTask; }
    public void Log(LogLevel logLevel, FormattableString message, Exception? exception = null) => Record(logLevel, default, message.ToString(), exception);
    public void Log(LogLevel logLevel, FormattableString message, IReadOnlyList<KeyValuePair<string, object?>>? properties, Exception? exception = null) => Record(logLevel, default, message.ToString(), exception);
    public Task LogAsync(LogLevel logLevel, FormattableString message, Exception? exception = null, CancellationToken cancellationToken = default) { Record(logLevel, default, message.ToString(), exception); return Task.CompletedTask; }
    public Task LogAsync(LogLevel logLevel, FormattableString message, IReadOnlyList<KeyValuePair<string, object?>>? properties, Exception? exception = null, CancellationToken cancellationToken = default) { Record(logLevel, default, message.ToString(), exception); return Task.CompletedTask; }
    public void Log(LogLevel logLevel, EventId eventId, string message, Exception? exception) => Record(logLevel, eventId, message, exception);
    public void Log(LogLevel logLevel, EventId eventId, string message, IReadOnlyList<KeyValuePair<string, object?>>? properties, Exception? exception) => Record(logLevel, eventId, message, exception);
    public Task LogAsync(LogLevel logLevel, EventId eventId, string message, Exception? exception = null, CancellationToken cancellationToken = default) { Record(logLevel, eventId, message, exception); return Task.CompletedTask; }
    public Task LogAsync(LogLevel logLevel, EventId eventId, string message, IReadOnlyList<KeyValuePair<string, object?>>? properties, Exception? exception = null, CancellationToken cancellationToken = default) { Record(logLevel, eventId, message, exception); return Task.CompletedTask; }
    public void Log(LogLevel logLevel, EventId eventId, FormattableString message, Exception? exception = null) => Record(logLevel, eventId, message.ToString(), exception);
    public void Log(LogLevel logLevel, EventId eventId, FormattableString message, IReadOnlyList<KeyValuePair<string, object?>>? properties, Exception? exception = null) => Record(logLevel, eventId, message.ToString(), exception);
    public Task LogAsync(LogLevel logLevel, EventId eventId, FormattableString message, Exception? exception = null, CancellationToken cancellationToken = default) { Record(logLevel, eventId, message.ToString(), exception); return Task.CompletedTask; }
    public Task LogAsync(LogLevel logLevel, EventId eventId, FormattableString message, IReadOnlyList<KeyValuePair<string, object?>>? properties, Exception? exception = null, CancellationToken cancellationToken = default) { Record(logLevel, eventId, message.ToString(), exception); return Task.CompletedTask; }
}
