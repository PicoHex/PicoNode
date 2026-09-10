using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PicoJsonRpc;

/// <summary>JSON-RPC error surfaced from a child error frame (fail-loud).</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Multiplexed JSON-RPC client over a single stdio channel (spec §9.1):
/// id-routed responses, in-flight cap (default 64, callers queue via the
/// gate), cancellation via a `cancel` notification for streams, streaming via
/// method-tagged notifications consumed as <see cref="IAsyncEnumerable{T}"/>.
/// <para>
/// A background reader per spawned channel dispatches frames by id; on child
/// EOF every pending call and stream is failed with <see cref="IOException"/>,
/// and — unless the teardown was deliberate (<see cref="JsonRpcChannel.RecycleRequested"/>)
/// — the crash is counted on the lifecycle (backoff). The first successful
/// response resets the crash counter (<see cref="ProcessLifecycle.MarkHealthy"/>).
/// </para>
/// </summary>
public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly ProcessLifecycle _lifecycle;
    private readonly SemaphoreSlim _queueGate;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcFrame>> _pending =
        new();
    private readonly ConcurrentDictionary<string, Channel<JsonRpcFrame>> _streams = new();
    private readonly object _readerGate = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private JsonRpcChannel? _readerChannel;
    private Task? _readerTask;

    public JsonRpcClient(ProcessLifecycle lifecycle, int maxInFlight = 64)
    {
        _lifecycle = lifecycle;
        _queueGate = new SemaphoreSlim(maxInFlight, maxInFlight);
    }

    public async Task<JsonRpcFrame> CallAsync(
        string method,
        string? paramsJson,
        CancellationToken ct,
        bool throwOnError = true
    )
    {
        await _queueGate.WaitAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var tcs = new TaskCompletionSource<JsonRpcFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _pending[id] = tcs;
            await WriteAsync(JsonRpcFrame.Request(id, method, paramsJson), ct);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var resp = await tcs.Task.WaitAsync(ct);
            if (throwOnError && resp.Error is { } err)
                throw new JsonRpcException(err.Code, err.Message);
            return resp;
        }
        finally
        {
            _queueGate.Release();
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Stream a method: yields method-tagged notification frames until the
    /// child's final response for the request id arrives (graceful completion)
    /// or it crashes (IOException). Cancellation sends a `cancel` notification
    /// (params = raw JSON text {"id":"&lt;request id&gt;"}) and ends the
    /// enumeration without waiting for the abandoned id.
    /// <para>
    /// Lifecycle note (review #6): `await foreach` disposes the enumerator on
    /// break/abandonment, releasing the in-flight slot and cancel-acknowledging
    /// the child. A caller that obtains an enumerator via GetAsyncEnumerator
    /// and never disposes it leaks one in-flight slot — dispose the enumerator.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<JsonRpcFrame> StreamAsync(
        string method,
        string? paramsJson,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await _queueGate.WaitAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        var queue = Channel.CreateUnbounded<JsonRpcFrame>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
        );
        _streams[id] = queue;
        try
        {
            // Consumer cancellation completes the channel GRACEFULLY (no OCE
            // through the enumerator) — the loop below just drains until the
            // channel ends; the cancel notification is sent in finally.
            using var reg = ct.Register(() => queue.Writer.TryComplete());
            await WriteAsync(JsonRpcFrame.Request(id, method, paramsJson), ct);
            while (await queue.Reader.WaitToReadAsync(CancellationToken.None))
            {
                while (queue.Reader.TryRead(out var frame))
                    yield return frame;
            }
            // channel ended — surface a terminal failure (child crash or an
            // error frame in the final response) loudly to the consumer
            // instead of silently ending the enumeration.
            await queue.Reader.Completion;
        }
        finally
        {
            _streams.TryRemove(id, out _);
            _queueGate.Release();
            var completion = queue.Reader.Completion;
            var completedNormally = completion.IsCompleted && !ct.IsCancellationRequested;
            if (!completion.IsFaulted && !completedNormally)
            {
                // cancelled/abandoned stream → tell the child (best-effort;
                // the child may already be gone after a crash).
                try
                {
                    await WriteAsync(
                        JsonRpcFrame.Notification("cancel", $"{{\"id\":\"{id}\"}}"),
                        CancellationToken.None
                    );
                }
                catch
                {
                    // best-effort cancel
                }
            }
        }
    }

    /// <summary>
    /// Write a frame to the current channel (re-acquires per write so a
    /// respawned process is always targeted) and ensure a reader is attached.
    /// </summary>
    private async Task WriteAsync(JsonRpcFrame frame, CancellationToken ct)
    {
        var ch = await _lifecycle.AcquireAsync(ct);
        EnsureReader(ch);
        var bytes = NdjsonFramer.Encode(frame);
        await ch.Stdin.WriteAsync(bytes, ct);
        await ch.Stdin.FlushAsync(ct);
        _lifecycle.Touch();
    }

    private void EnsureReader(JsonRpcChannel ch)
    {
        lock (_readerGate)
        {
            if (ReferenceEquals(_readerChannel, ch))
                return;
            _readerChannel = ch;
            _readerTask = Task.Run(() => ReadLoopAsync(ch), CancellationToken.None);
        }
    }

    private async Task ReadLoopAsync(JsonRpcChannel ch)
    {
        try
        {
            await foreach (var frame in NdjsonFramer.ReadAsync(ch.Stdout, _disposeCts.Token))
            {
                // review fix #2: DOWNLINK frames are activity — refresh the
                // idle timer or a concurrent call after IdleTimeout of no
                // writes would reclaim the channel of an active stream.
                _lifecycle.Touch();
                if (frame.Method is { } method)
                {
                    // notification — fan out to every active stream subscription
                    foreach (var kvp in _streams)
                        kvp.Value.Writer.TryWrite(frame);
                    continue;
                }
                var id = frame.Id;
                if (id is null)
                    continue; // unroutable (both-null) frame — ignore
                if (_pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(frame);
                    _lifecycle.MarkHealthy();
                    continue;
                }
                if (_streams.TryRemove(id, out var stream))
                {
                    // final response for a stream — graceful completion on
                    // success; an ERROR frame faults the stream so the
                    // consumer (adapter) rethrows JsonRpcException (C3 retry
                    // semantics depend on it).
                    var fault = frame.Error is { } err
                        ? new JsonRpcException(err.Code, err.Message)
                        : null;
                    stream.Writer.TryComplete(fault);
                    _lifecycle.MarkHealthy();
                    continue;
                }
                // response with no pending (cancelled/abandoned) — ignore
            }
            HandleEof(ch);
        }
        catch (OperationCanceledException)
        {
            // client disposed — no crash accounting
        }
        catch (Exception)
        {
            // malformed frame / framing failure — treat as a channel failure
            HandleEof(ch);
        }
    }

    private void HandleEof(JsonRpcChannel ch)
    {
        lock (_readerGate)
        {
            if (!ReferenceEquals(_readerChannel, ch))
                return; // stale reader — a newer channel owns the client
            _readerChannel = null;
            _readerTask = null;
        }
        var ex = new IOException("extension process closed the stream");
        foreach (var kvp in _pending)
            kvp.Value.TrySetException(ex);
        foreach (var kvp in _streams)
            kvp.Value.Writer.TryComplete(ex);
        _pending.Clear();
        _streams.Clear();
        if (!ch.RecycleRequested)
            _lifecycle.Churn(ch);
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return; // idempotent — `await using` + explicit dispose both call in
        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already cancelled
        }
        var ex = new IOException("jsonrpc client disposed");
        foreach (var kvp in _pending)
            kvp.Value.TrySetException(ex);
        foreach (var kvp in _streams)
            kvp.Value.Writer.TryComplete(ex);
        _pending.Clear();
        _streams.Clear();
        JsonRpcChannel? ch;
        lock (_readerGate)
        {
            ch = _readerChannel;
            _readerChannel = null;
        }
        try
        {
            // close stdin → the child sees EOF and exits on its own
            ch?.Stdin.Close();
        }
        catch
        {
            // already closed/rotated
        }
        await _lifecycle.RecycleAsync();
        try
        {
            _disposeCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // idempotent
        }
    }
}
