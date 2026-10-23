using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PicoJsonRpc;

/// <summary>JSON-RPC error surfaced from a child error frame (fail-loud).</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// A call waiting for its response: the completer plus the channel the request
/// was (or is about to be) written to — the OWNER. The owner is what makes a
/// channel failure attributable: when <c>ch</c> dies, exactly the calls owned by
/// <c>ch</c> can no longer be answered, and no other call may be touched.
/// </summary>
internal readonly record struct PendingCall(
    JsonRpcChannel Owner,
    TaskCompletionSource<JsonRpcFrame> Tcs
);

/// <summary>A stream subscription: the same ownership rule as
/// <see cref="PendingCall"/>, with the notification queue as its completer.</summary>
internal readonly record struct StreamCall(JsonRpcChannel Owner, Channel<JsonRpcFrame> Queue);

/// <summary>
/// Multiplexed JSON-RPC client over a single stdio channel (spec §9.1):
/// id-routed responses, in-flight cap (default 64, callers queue via the
/// gate), cancellation via a `cancel` notification for streams, streaming via
/// method-tagged notifications consumed as <see cref="IAsyncEnumerable{T}"/>.
/// <para>
/// A background reader per spawned channel dispatches frames by id; on child
/// EOF every pending call and stream OWNED BY THAT CHANNEL is failed with
/// <see cref="IOException"/>, and — unless the teardown was deliberate
/// (<see cref="JsonRpcChannel.RecycleRequested"/>) — the crash is counted on the
/// lifecycle (backoff). The first successful response resets the crash counter
/// (<see cref="ProcessLifecycle.MarkHealthy"/>).
/// </para>
/// <para>
/// Ownership, not currency, decides who gets failed (T7.2). A respawn can
/// replace the reader before the old channel's EOF is drained — the old calls
/// still belong to the old channel and must be released, while the calls that
/// already moved to the new channel must not be. Subscriptions and their reader
/// are published in one critical section (<see cref="_stateGate"/>), so a
/// registration can never fall between "what is in the table" and "what gets
/// failed": it is either in the detached table (drained, owner-checked) or in
/// its replacement.
/// </para>
/// </summary>
public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly IChannelLifecycle _lifecycle;
    private readonly SemaphoreSlim _queueGate;

    /// <summary>Guards the reader bookkeeping AND the two registries: registering
    /// a call and draining the calls of a dead channel are mutually exclusive.</summary>
    private readonly object _stateGate = new();

    /// <summary>id → its owner channel + completer. Replaced wholesale, never
    /// cleared in place (a `Clear` would race a registration into oblivion).</summary>
    private ConcurrentDictionary<string, PendingCall> _pending = new();
    private ConcurrentDictionary<string, StreamCall> _streams = new();
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>1 once <see cref="DisposeAsync"/> has started. Registration checks
    /// it under <see cref="_stateGate"/> — see <see cref="TrackPending"/>.</summary>
    private int _disposed;

    private JsonRpcChannel? _readerChannel;
    private Task? _readerTask;

    public JsonRpcClient(ProcessLifecycle lifecycle, int maxInFlight = 64)
        : this(new LifecycleChannelSource(lifecycle), maxInFlight) { }

    /// <summary>Test seam — see <see cref="IChannelLifecycle"/>: drives the exact
    /// channel/EOF interleaving the ownership rule is about.</summary>
    internal JsonRpcClient(IChannelLifecycle lifecycle, int maxInFlight = 64)
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
            // Order matters: channel first, then the registration (which carries
            // the owner), then the write. The reader is attached in the same
            // critical section as the registration, so the reader either sees
            // this call (it is already in the table) or has not started for this
            // channel yet — it can never observe the EOF of a channel this call
            // is registered on without also seeing the call.
            var ch = await _lifecycle.AcquireAsync(ct);
            TrackPending(id, ch, tcs);
            await WriteToAsync(ch, JsonRpcFrame.Request(id, method, paramsJson), ct);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var resp = await tcs.Task.WaitAsync(ct);
            if (throwOnError && resp.Error is { } err)
                throw new JsonRpcException(err.Code, err.Message);
            return resp;
        }
        finally
        {
            _queueGate.Release();
            UntrackPending(id);
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
        try
        {
            var ch = await _lifecycle.AcquireAsync(ct);
            TrackStream(id, ch, queue);
            // Consumer cancellation completes the channel GRACEFULLY (no OCE
            // through the enumerator) — the loop below just drains until the
            // channel ends; the cancel notification is sent in finally.
            using var reg = ct.Register(() => queue.Writer.TryComplete());
            await WriteToAsync(ch, JsonRpcFrame.Request(id, method, paramsJson), ct);
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
            UntrackStream(id);
            _queueGate.Release();
            var completion = queue.Reader.Completion;
            var completedNormally = completion.IsCompleted && !ct.IsCancellationRequested;
            if (
                !completion.IsFaulted
                && !completedNormally
                // ...but never on a disposed client: WriteAsync re-acquires a
                // channel, and by now DisposeAsync's RecycleAsync has dropped the
                // old one — so the acquire would SPAWN a fresh child (plus a
                // reader) whose reader is cancelled by _disposeCts and therefore
                // never reaches HandleEof to recycle it. Fail-fast must not
                // respawn; the abandoned child gets no cancel notice.
                && Volatile.Read(ref _disposed) == 0
            )
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
    /// Write a frame to the CURRENT channel (re-acquires per write so a
    /// respawned process is always targeted) and ensure a reader is attached.
    /// Used for the cancel notification, where no response is expected.
    /// </summary>
    private async Task WriteAsync(JsonRpcFrame frame, CancellationToken ct)
    {
        var ch = await _lifecycle.AcquireAsync(ct);
        AttachReader(ch);
        await WriteToAsync(ch, frame, ct);
    }

    private async Task WriteToAsync(JsonRpcChannel ch, JsonRpcFrame frame, CancellationToken ct)
    {
        var bytes = NdjsonFramer.Encode(frame);
        await ch.Stdin.WriteAsync(bytes, ct);
        await ch.Stdin.FlushAsync(ct);
        _lifecycle.Touch();
    }

    /// <summary>Publish the call — owner included — and attach the owner's reader
    /// in the same critical section as
    /// <see cref="HandleEof(JsonRpcChannel)"/>'s drain.
    /// <para>
    /// T7.3 fold-in (T7.2 carries #2, same hang class): a call that registers
    /// AFTER <see cref="DisposeAsync"/> drained the tables would be inserted into
    /// the replacement — which nobody will ever drain again, because the reader
    /// is cancelled on dispose and a cancelled reader deliberately skips
    /// <see cref="HandleEof(JsonRpcChannel)"/>. Nothing then completes that TCS and
    /// the caller waits on <see cref="CancellationToken.None"/> forever. So a
    /// registration past dispose fails immediately instead of entering the table.
    /// The check is INSIDE the gate: dispose raises <c>_disposed</c> before it
    /// takes the gate, so an insert that passes it is guaranteed to be seen by the
    /// drain.
    /// </para>
    /// </summary>
    private void TrackPending(
        string id,
        JsonRpcChannel owner,
        TaskCompletionSource<JsonRpcFrame> tcs
    )
    {
        lock (_stateGate)
        {
            ThrowIfDisposed();
            _pending[id] = new PendingCall(owner, tcs);
            EnsureReaderLocked(owner);
        }
    }

    /// <summary>Stream counterpart of <see cref="TrackPending"/>: a subscription
    /// registered after dispose is refused the same way, or its queue could never
    /// be completed and the enumerator would wait forever.</summary>
    private void TrackStream(string id, JsonRpcChannel owner, Channel<JsonRpcFrame> queue)
    {
        lock (_stateGate)
        {
            ThrowIfDisposed();
            _streams[id] = new StreamCall(owner, queue);
            EnsureReaderLocked(owner);
        }
    }

    /// <summary>Under the gate: refuse to register on a disposed client — the
    /// caller's task is abandoned, so it must fail here rather than hang.</summary>
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(JsonRpcClient));
    }

    /// <summary>Under the gate: a drain cannot swap the registry out from under
    /// this removal (which would leave a completed entry behind forever).</summary>
    private void UntrackPending(string id)
    {
        lock (_stateGate)
            _pending.TryRemove(id, out _);
    }

    private void UntrackStream(string id)
    {
        lock (_stateGate)
            _streams.TryRemove(id, out _);
    }

    private void AttachReader(JsonRpcChannel ch)
    {
        lock (_stateGate)
            EnsureReaderLocked(ch);
    }

    private void EnsureReaderLocked(JsonRpcChannel ch)
    {
        if (ReferenceEquals(_readerChannel, ch))
            return;
        _readerChannel = ch;
        _readerTask = Task.Run(() => ReadLoopAsync(ch), CancellationToken.None);
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
                        kvp.Value.Queue.Writer.TryWrite(frame);
                    continue;
                }
                var id = frame.Id;
                if (id is null)
                {
                    // Unroutable: no id means nothing to answer (a response ids
                    // itself, a notification is fire-and-forget) — so the frame
                    // is DROPPED.
                    //
                    // The one frame this loses is the framer's answer to a peer
                    // BATCH line ("-32600 batch unsupported", null id): it is
                    // produced and then dropped here, never written back. That
                    // is the honest state of the behaviour (README + T7.4 report
                    // say so) — a peer that offers a batch gets no reply. Routing
                    // it to a protocol-error sink is a public-API decision, not a
                    // silent patch.
                    continue;
                }
                if (_pending.TryRemove(id, out var call))
                {
                    call.Tcs.TrySetResult(frame);
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
                    stream.Queue.Writer.TryComplete(fault);
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

    /// <summary>
    /// The channel's reader ended: fail exactly the calls and streams OWNED by
    /// <paramref name="ch"/> and drain them out of the registries.
    /// <para>
    /// Being the current reader is a lifecycle question only — it decides
    /// whether the reader bookkeeping is cleared and whether the crash is counted
    /// (<see cref="IChannelLifecycle.Churn"/>). It must NOT decide whether calls
    /// fail: a respawn attaches a new reader to a new channel, and the moment it
    /// does, this channel's EOF looks "stale" — while the calls written to it are
    /// still unanswered and would wait forever without this drain.
    /// </para>
    /// </summary>
    private void HandleEof(JsonRpcChannel ch)
    {
        List<PendingCall> calls = [];
        List<StreamCall> streams = [];
        bool current;
        lock (_stateGate)
        {
            current = ReferenceEquals(_readerChannel, ch);
            if (current)
            {
                _readerChannel = null;
                _readerTask = null;
            }
            // Atomic swap under the registration lock: the detached tables hold
            // exactly the entries that existed before this drain, and every one of
            // them ends up either failed (its owner is this channel) or carried
            // into the replacement (its owner is still alive).
            _pending = Detach(_pending, ch, static call => call.Owner, calls);
            _streams = Detach(_streams, ch, static stream => stream.Owner, streams);
        }
        var ex = new IOException("extension process closed the stream" + DescribeStderr(ch));
        foreach (var call in calls)
            call.Tcs.TrySetException(ex);
        foreach (var stream in streams)
            stream.Queue.Writer.TryComplete(ex);
        if (current && !ch.RecycleRequested)
            _lifecycle.Churn(ch);
    }

    /// <summary>
    /// Publish a replacement registry without the entries owned by
    /// <paramref name="ch"/>, collecting those into <paramref name="victims"/>.
    /// Entries owned by anyone else are transferred (removed from the detached
    /// table, re-published in the replacement) — a snapshot-plus-`Clear` used to
    /// drop them, and a call dropped from the table can never be completed: its
    /// response is unroutable and its caller waits on
    /// <see cref="CancellationToken.None"/> forever.
    /// <para>
    /// Every entry is moved with <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>,
    /// so the dispatch path (which removes without the gate) and this drain can
    /// never both own the same entry.
    /// </para>
    /// </summary>
    private static ConcurrentDictionary<string, T> Detach<T>(
        ConcurrentDictionary<string, T> live,
        JsonRpcChannel ch,
        Func<T, JsonRpcChannel> owner,
        List<T> victims
    )
    {
        var survivors = new ConcurrentDictionary<string, T>();
        foreach (var kvp in live)
        {
            if (ReferenceEquals(owner(kvp.Value), ch))
            {
                if (live.TryRemove(kvp.Key, out var victim))
                    victims.Add(victim);
            }
            else if (live.TryRemove(kvp.Key, out var survivor))
            {
                survivors[kvp.Key] = survivor;
            }
        }
        return survivors;
    }

    /// <summary>
    /// Attach the child's own last words to a channel failure (T7.1): a stdio
    /// extension that dies of a stack trace, an OOM or a bad config usually says
    /// so on stderr, and an `IOException` with no cause is undebuggable. Bounded
    /// to 2 KB in the message — the full 8 KB tail stays on
    /// <see cref="JsonRpcChannel.StderrTail"/> for a caller that wants it.
    /// </summary>
    private static string DescribeStderr(JsonRpcChannel ch)
    {
        var tail = ch.CaptureStderrTail(TimeSpan.FromMilliseconds(250)).TrimEnd();
        if (tail.Length == 0)
            return string.Empty;
        const int maxInMessage = 2048;
        var body = tail.Length <= maxInMessage ? tail : "…" + tail[^maxInMessage..];
        return "; child stderr tail:\n" + body;
    }

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
        List<PendingCall> calls = [];
        List<StreamCall> streams = [];
        JsonRpcChannel? ch;
        lock (_stateGate)
        {
            var pending = _pending;
            var subscriptions = _streams;
            foreach (var kvp in pending)
                if (pending.TryRemove(kvp.Key, out var call))
                    calls.Add(call);
            foreach (var kvp in subscriptions)
                if (subscriptions.TryRemove(kvp.Key, out var stream))
                    streams.Add(stream);
            ch = _readerChannel;
            _readerChannel = null;
            _readerTask = null;
        }
        var ex = new IOException("jsonrpc client disposed");
        foreach (var call in calls)
            call.Tcs.TrySetException(ex);
        foreach (var stream in streams)
            stream.Queue.Writer.TryComplete(ex);
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
