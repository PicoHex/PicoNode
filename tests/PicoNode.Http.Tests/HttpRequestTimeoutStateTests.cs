namespace PicoNode.Http.Tests;

/// <summary>
/// Unit coverage for the wall-clock request deadline state. The deadline
/// callback runs on a timer thread, so nothing it invokes may escape: an
/// unhandled exception on that thread would crash the process, and a throwing
/// user <c>Close()</c>/logger is outside our control. The callback must also
/// prove it belongs to the deadline that is currently armed — a late callback
/// from a completed request must never close the next request on the same
/// keep-alive connection.
/// </summary>
public sealed class HttpRequestTimeoutStateTests
{
    [Test]
    public async Task Deadline_close_swallows_connection_close_failures()
    {
        var state = new Http1ConnectionState();
        var connection = new ThrowingCloseContext();
        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger: null);

        await Assert
            .That(() => state.OnDeadlineElapsed(state.ArmedDeadline!))
            .ThrowsNothing()
            .Because(
                "the deadline callback runs on a timer thread; a throwing Close() must not crash the process"
            );

        state.DisarmRequestTimeout();
    }

    [Test]
    public async Task Deadline_close_logs_at_debug_and_closes_the_connection()
    {
        var state = new Http1ConnectionState();
        var connection = new RecordingCloseContext();
        var logger = new RecordingHttpLogger();
        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger);

        state.OnDeadlineElapsed(state.ArmedDeadline!);

        await Assert.That(connection.CloseCount).IsEqualTo(1);
        await Assert.That(logger.Calls.Count).IsEqualTo(1);
        await Assert.That(logger.LastLevel).IsEqualTo(LogLevel.Debug);
        await Assert.That(logger.LastMessage).Contains("deadline");

        state.DisarmRequestTimeout();
    }

    [Test]
    public async Task Deadline_close_swallows_throwing_loggers()
    {
        var state = new Http1ConnectionState();
        var connection = new RecordingCloseContext();
        var logger = new RecordingHttpLogger { ThrowOnLog = true };
        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger);

        await Assert
            .That(() => state.OnDeadlineElapsed(state.ArmedDeadline!))
            .ThrowsNothing()
            .Because("a throwing user logger must not crash the timer thread");

        await Assert.That(connection.CloseCount).IsEqualTo(1);

        state.DisarmRequestTimeout();
    }

    [Test]
    public async Task Deadline_after_disarm_is_a_no_op()
    {
        var state = new Http1ConnectionState();
        var connection = new RecordingCloseContext();
        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger: null);
        var disarmed = state.ArmedDeadline!;
        state.DisarmRequestTimeout();

        state.OnDeadlineElapsed(disarmed);

        await Assert
            .That(connection.CloseCount)
            .IsEqualTo(0)
            .Because("a disarmed deadline must never close the connection");
    }

    [Test]
    public async Task Stale_deadline_cannot_close_the_next_request_on_the_same_connection()
    {
        // Regression: the callback only checked "is any deadline armed", not
        // "is it MY deadline". A late callback from request 1 (already answered)
        // could therefore kill request 2 after it armed its own deadline — the
        // classic keep-alive/pipelining failure mode under scheduling delays.
        var state = new Http1ConnectionState();
        var connection = new RecordingCloseContext();

        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger: null);
        var stale = state.ArmedDeadline!;
        state.DisarmRequestTimeout(); // request 1 completed normally
        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger: null); // request 2 in flight

        state.OnDeadlineElapsed(stale); // request 1's callback arrives late

        await Assert
            .That(connection.CloseCount)
            .IsEqualTo(0)
            .Because(
                "a stale deadline must not close the connection while a newer request is in flight"
            );

        // The current deadline must still work (the fix must not disable closing).
        state.OnDeadlineElapsed(state.ArmedDeadline!);
        await Assert.That(connection.CloseCount).IsEqualTo(1);
    }

    [Test]
    public async Task Null_state_is_ignored_defensively()
    {
        // Documentation guard rather than a discriminator: by construction the
        // connection/logger fields are null whenever the CTS is null, so this test
        // cannot fail for a behavioral reason — it pins the explicit guard.
        var state = new Http1ConnectionState();
        var connection = new RecordingCloseContext();
        var logger = new RecordingHttpLogger();

        state.OnDeadlineElapsed(null);

        await Assert.That(connection.CloseCount).IsEqualTo(0);
        await Assert.That(logger.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Deadline_closes_before_it_logs()
    {
        // The close must not wait behind user logging: a slow logger would delay
        // the only action the deadline exists for.
        var events = new List<string>();
        var state = new Http1ConnectionState();
        var connection = new RecordingCloseContext { OnClose = () => events.Add("close") };
        var logger = new RecordingHttpLogger { OnLog = () => events.Add("log") };
        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger);

        state.OnDeadlineElapsed(state.ArmedDeadline!);

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0]).IsEqualTo("close");
        await Assert.That(events[1]).IsEqualTo("log");
    }

    [Test]
    public async Task Repeated_arm_keeps_the_original_deadline()
    {
        // A slow/incomplete request re-arms on every parse; the original deadline
        // must stand (otherwise drip-feeding would extend it forever).
        var state = new Http1ConnectionState();
        var connection = new RecordingCloseContext();
        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger: null);
        var first = state.ArmedDeadline;

        state.ArmRequestTimeout(connection, TimeSpan.FromSeconds(30), logger: null);

        await Assert.That(state.ArmedDeadline).IsSameReferenceAs(first);
        state.DisarmRequestTimeout();
    }

    private class CountedContext : ITcpConnectionContext
    {
        public int CloseCount { get; protected set; }

        public Action? OnClose { get; init; }

        public long ConnectionId => 1;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 12345);
        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UnixEpoch;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.UnixEpoch;
        public object? UserState { get; set; }
        public CancellationToken RemoteCloseToken => CancellationToken.None;
        public string? NegotiatedProtocol => null;

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public virtual void Close()
        {
            CloseCount++;
            OnClose?.Invoke();
        }
    }

    private sealed class RecordingCloseContext : CountedContext;

    private sealed class ThrowingCloseContext : CountedContext
    {
        public override void Close() => throw new InvalidOperationException("close-boom");
    }
}
