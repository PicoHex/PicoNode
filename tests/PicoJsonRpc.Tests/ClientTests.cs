using System.Diagnostics;
using System.Threading.Channels;

namespace PicoJsonRpc.Tests;

/// <summary>
/// Client failure paths (T7.2): two ways a caller with
/// <see cref="CancellationToken.None"/> used to wait forever.
/// <para>
/// Both are races between a channel's EOF and a <em>respawn</em>, so the tests
/// drive the channel source directly (<see cref="ScriptedLifecycle"/>) instead
/// of a real child: a real process dies on its own schedule, which makes the
/// interleaving "ch-1's EOF is drained while ch-2 owns the reader" schedulable
/// at best and unproducible at worst. Each scripted channel still carries a real
/// process handle (<see cref="JsonRpcChannel"/> requires one), but its PIPES
/// belong to the test — that is what lets it say exactly when a frame arrives
/// and when the child's stdout ends.
/// </para>
/// </summary>
public sealed class ClientTests
{
    /// <summary>
    /// A bounded wait, NOT a timing assertion: every failure mode below is an
    /// <em>unbounded</em> hang, so a 5 s pass proves only that nothing hung. Each
    /// step resolves in single-digit milliseconds on a healthy machine; the
    /// budget exists to turn a hang into a red test instead of a stuck run.
    /// </summary>
    private static readonly TimeSpan HangBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A call written to ch-1 and never answered must be released when ch-1
    /// dies — even though by then a respawn has made ch-2 the client's reader
    /// channel and ch-1's own reader is stale. Hanging here is the bug: the
    /// caller holds <see cref="CancellationToken.None"/> and waits forever.
    /// </summary>
    [Test]
    public async Task Pending_call_fails_when_its_own_channel_dies_after_a_respawn()
    {
        Skip.Unless(
            NodeRuntime.Present,
            "node is not installed in PATH - the scripted channels need a real process handle"
        );

        using var first = new TestChannel("ch-1");
        using var second = new TestChannel("ch-2");
        var lifecycle = new ScriptedLifecycle(first.Channel, second.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        // call #1 lands on ch-1: written, never answered
        var stranded = client.CallAsync("hold", null, CancellationToken.None);
        var strandedReq = await first.NextRequestAsync();
        await Assert.That(strandedReq.Method).IsEqualTo("hold");

        // ...then a concurrent call runs the RESPAWN: the lifecycle hands out a
        // fresh channel (what it does the moment ch-1's child has exited), and
        // the client attaches its reader there — ch-1's reader is stale from now on
        var live = client.CallAsync("ping", null, CancellationToken.None);
        var liveReq = await second.NextRequestAsync();
        await Assert.That(liveReq.Method).IsEqualTo("ping");

        // ch-1's reader finally reaches EOF, long after it stopped being current
        first.EndStdout();

        // 1) the call stranded on ch-1 is released instead of waiting on a
        //    channel nobody reads any more
        await Assert.That(async () => await stranded.WaitAsync(HangBudget)).Throws<IOException>();

        // 2) ...and ch-1's EOF does not disturb ch-2's in-flight call: an entry
        //    the failure sweep did not own must survive it, not be dropped
        second.Send(JsonRpcFrame.Response(liveReq.Id!, "\"pong\""));
        var (pong, pongError) = await OutcomeAsync(live.WaitAsync(HangBudget));
        await Assert.That(pongError).IsNull();
        await Assert.That(pong!.Result).IsEqualTo("\"pong\"");
    }

    /// <summary>
    /// A call that registers while a dying channel's EOF is being drained must
    /// not be lost. It belongs to the channel it is about to write to (ch-2), so
    /// ch-1's failure sweep may not touch it — and it must still be there when
    /// ch-2 answers. It used to be swept away with the snapshot of the pending
    /// table (failed, or silently dropped), leaving the caller waiting forever.
    /// </summary>
    [Test]
    public async Task Call_registered_during_handle_eof_is_not_lost()
    {
        Skip.Unless(
            NodeRuntime.Present,
            "node is not installed in PATH - the scripted channels need a real process handle"
        );

        using var first = new TestChannel("ch-1");
        using var second = new TestChannel("ch-2");
        var lifecycle = new ScriptedLifecycle(first.Channel, second.Channel);
        await using var client = new JsonRpcClient(lifecycle, maxInFlight: 8);

        // ch-1 owns the client's reader, with one call parked on it
        var stranded = client.CallAsync("hold", null, CancellationToken.None);
        await first.NextRequestAsync();

        // a second call is inside the channel source, about to be handed ch-2
        var (entered, release) = lifecycle.Park();
        var concurrent = client.CallAsync("concurrent", null, CancellationToken.None);
        await entered.WaitAsync(HangBudget);

        // ...while ch-1's reader hits EOF
        first.EndStdout();

        // the parked call's failure is what proves the EOF has been drained
        await Assert.That(async () => await stranded.WaitAsync(HangBudget)).Throws<IOException>();

        // now let the concurrent call through: it lands on ch-2 and must be
        // answered there, not swept away by ch-1's failure
        release.TrySetResult();
        var concurrentReq = await second.NextRequestAsync();
        second.Send(JsonRpcFrame.Response(concurrentReq.Id!, "\"ok\""));
        var (resp, respError) = await OutcomeAsync(concurrent.WaitAsync(HangBudget));
        await Assert.That(respError).IsNull();
        await Assert.That(resp!.Result).IsEqualTo("\"ok\"");
    }

    /// <summary>Await a task and return its result or the exception it faulted
    /// with, so the assertion can NAME the failure instead of the test dying
    /// with a stack trace from inside the client.</summary>
    private static async Task<(T? Value, Exception? Error)> OutcomeAsync<T>(Task<T> task)
    {
        try
        {
            return (await task, null);
        }
        catch (Exception ex)
        {
            return (default, ex);
        }
    }

    /// <summary>
    /// A channel source with a script: acquire #1 hands out ch-1, #2 ch-2, and
    /// the last one repeats (a real lifecycle keeps handing out the live
    /// channel). <see cref="Park"/> additionally holds the NEXT acquire inside
    /// the call — the seam the "registration concurrent with an EOF" test needs.
    /// </summary>
    private sealed class ScriptedLifecycle(params JsonRpcChannel[] script) : IChannelLifecycle
    {
        private readonly Queue<JsonRpcChannel> _script = new(script);
        private JsonRpcChannel? _current;
        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _release;

        /// <summary>
        /// Block the next <see cref="AcquireAsync"/>: the first task completes
        /// when the client has ENTERED it, and the acquire returns only once the
        /// test completes the second one.
        /// </summary>
        public (Task Entered, TaskCompletionSource Release) Park()
        {
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _entered = entered;
            _release = release;
            return (entered.Task, release);
        }

        public async Task<JsonRpcChannel> AcquireAsync(CancellationToken ct)
        {
            if (_release is { } release)
            {
                var entered = _entered!;
                _entered = null;
                _release = null;
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            if (_script.Count > 1)
                _current = _script.Dequeue();
            else if (_script.Count == 1)
                _current = _script.Peek();
            return _current ?? throw new InvalidOperationException("scripted lifecycle is empty");
        }

        public void Touch() { }

        /// <summary>Crash accounting is the lifecycle's own concern and is
        /// covered by <see cref="LifecycleTests"/>; these tests are about the
        /// client's registry, so the hook is deliberately inert.</summary>
        public void Churn(JsonRpcChannel ch) { }

        public void MarkHealthy() { }

        public Task RecycleAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// One scripted stdio channel: the request frames the client writes are
    /// handed to <see cref="NextRequestAsync"/>, and the test decides when a
    /// frame shows up on stdout (<see cref="Send"/>) or when the child's stdout
    /// ends (<see cref="EndStdout"/>, the EOF that makes the reader loop exit).
    /// </summary>
    private sealed class TestChannel : IDisposable
    {
        private readonly Process _process;
        private readonly RequestSink _stdin = new();
        private readonly FrameSource _stdout = new();

        public TestChannel(string name)
        {
            _process =
                Process.Start(
                    new ProcessStartInfo("node")
                    {
                        ArgumentList = { "-e", "setTimeout(() => {}, 600000)" },
                        UseShellExecute = false,
                    }
                ) ?? throw new InvalidOperationException("could not start the inert child");
            Channel = new JsonRpcChannel
            {
                Process = _process,
                Stdin = _stdin,
                Stdout = _stdout,
                Stderr = Stream.Null,
                Name = name,
            };
        }

        public JsonRpcChannel Channel { get; }

        /// <summary>The next request frame the client wrote to this channel.</summary>
        public Task<JsonRpcFrame> NextRequestAsync() =>
            _stdin.Requests.ReadAsync().AsTask().WaitAsync(HangBudget);

        /// <summary>The child answers: one response frame on this channel's stdout.</summary>
        public void Send(JsonRpcFrame frame) => _stdout.Push(NdjsonFramer.Encode(frame));

        /// <summary>The child's stdout reaches EOF (process death, closed pipe).</summary>
        public void EndStdout() => _stdout.End();

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // the inert child is disposable best-effort
            }
            _process.Dispose();
        }
    }

    /// <summary>The child's stdin, captured: every complete NDJSON line the
    /// client writes becomes a request frame the test can read.</summary>
    private sealed class RequestSink : Stream
    {
        private readonly List<byte> _line = [];
        private readonly object _gate = new();
        private readonly Channel<JsonRpcFrame> _requests = Channel.CreateUnbounded<JsonRpcFrame>();

        public ChannelReader<JsonRpcFrame> Requests => _requests.Reader;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Ingest(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Ingest(buffer.Span);
            return default;
        }

        private void Ingest(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                foreach (var b in bytes)
                {
                    if (b != (byte)'\n')
                    {
                        _line.Add(b);
                        continue;
                    }
                    if (_line.Count == 0)
                        continue;
                    var frame = JsonRpcCodec.Deserialize<JsonRpcFrame>(
                        Encoding.UTF8.GetString([.. _line])
                    );
                    if (frame is not null)
                        _requests.Writer.TryWrite(frame);
                    _line.Clear();
                }
            }
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>The client closes stdin on dispose; the sink stays usable
        /// (nothing here holds an OS handle).</summary>
        protected override void Dispose(bool disposing) { }
    }

    /// <summary>The child's stdout, scripted: frames appear only when the test
    /// pushes them, and a read returns 0 (EOF) only once the test ends it.</summary>
    private sealed class FrameSource : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public void Push(byte[] bytes) => _chunks.Writer.TryWrite(bytes);

        public void End() => _chunks.Writer.TryComplete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                    return 0; // EOF
                if (_chunks.Reader.TryRead(out _current))
                    _offset = 0;
            }
            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing) { }
    }

    /// <summary>Is there a `node` runtime to give the scripted channels a real
    /// process handle? Missing → the tests skip, they never fail.</summary>
    private static class NodeRuntime
    {
        public static readonly bool Present = Probe();

        private static bool Probe()
        {
            try
            {
                using var p = Process.Start(
                    new ProcessStartInfo("node", "--version")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                );
                if (p is null)
                    return false;
                var version = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return version.Trim().Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
