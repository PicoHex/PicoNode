using System.Diagnostics;

namespace PicoJsonRpc;

/// <summary>
/// The slice of the process lifecycle <see cref="JsonRpcClient"/> depends on:
/// acquire the live channel, report activity, and attribute a channel failure.
/// <para>
/// Internal on purpose. Production always passes a <see cref="ProcessLifecycle"/>;
/// the seam exists because the client's rule "only the calls OWNED by the dead
/// channel fail" is a rule about a race (a respawn replacing the reader before
/// the old channel's EOF is drained), and a real child dies on its own
/// schedule — no external test can produce that interleaving against a real
/// process without racing the scheduler. A scripted channel source makes the
/// interleaving explicit instead of probabilistic.
/// </para>
/// </summary>
internal interface IChannelLifecycle
{
    Task<JsonRpcChannel> AcquireAsync(CancellationToken ct);

    void Touch();

    void Churn(JsonRpcChannel ch);

    void MarkHealthy();

    Task RecycleAsync();
}

/// <summary>
/// Adapts the real lifecycle to the seam above. Kept as a wrapper rather than
/// `ProcessLifecycle : IChannelLifecycle` on purpose: an implemented interface
/// lands in the public surface dump (base list + "override" markers), which would
/// report an API change for a purely internal seam — and the released baseline
/// would then have to be refreshed for nothing.
/// </summary>
internal sealed class LifecycleChannelSource(ProcessLifecycle lifecycle) : IChannelLifecycle
{
    public Task<JsonRpcChannel> AcquireAsync(CancellationToken ct) => lifecycle.AcquireAsync(ct);

    public void Touch() => lifecycle.Touch();

    public void Churn(JsonRpcChannel ch) => lifecycle.Churn(ch);

    public void MarkHealthy() => lifecycle.MarkHealthy();

    public Task RecycleAsync() => lifecycle.RecycleAsync();
}

/// <summary>
/// A live stdio channel to one child process: process handle + stdin/stdout/stderr
/// streams. <see cref="RecycleRequested"/> distinguishes an INTENTIONAL recycle
/// (idle/RecycleAsync — not a crash) from an EOF the client observes spuriously.
/// <para>
/// <see cref="ConsecutiveCrashes"/> / <see cref="NextRetryAtUtc"/> are a READ-ONLY
/// VIEW of the lifecycle's crash streak (T7.3), not its storage: they are
/// re-stamped on every spawn and on every <see cref="ProcessLifecycle.Churn"/>,
/// so a caller holding this generation's channel reads the streak that includes
/// its predecessors. They are mirrored — not owned — precisely because a channel
/// is destroyed by the crash it reports: a counter kept on it would die with the
/// generation it measured. The properties stay public (released surface).
/// </para>
/// </summary>
public sealed class JsonRpcChannel : IDisposable
{
    /// <summary>How much of the child's stderr tail is retained (per channel).</summary>
    internal const int StderrTailLimit = 8 * 1024;

    private readonly byte[] _stderrWindow = new byte[StderrTailLimit];
    private readonly object _stderrGate = new();
    private int _stderrLen;

    public required Process Process { get; init; }
    public required Stream Stdin { get; init; }
    public required Stream Stdout { get; init; }

    /// <summary>The child's stderr stream. Redirected pipes have a finite OS
    /// buffer (4-64 KB), so <see cref="ProcessLifecycle"/> drains this from
    /// spawn to dispose — an unread pipe deadlocks a chatty child inside
    /// write(2), with no error, no timeout and no diagnostic.</summary>
    public required Stream Stderr { get; init; }
    public required string Name { get; init; }

    /// <summary>How many crashes in a row the LIFECYCLE has counted, including
    /// generations before this channel. Stamped by <see cref="ProcessLifecycle"/>.</summary>
    public int ConsecutiveCrashes { get; set; }

    /// <summary>When the lifecycle will next try to spawn; <c>default</c> while no
    /// backoff is armed. Stamped by <see cref="ProcessLifecycle"/>.</summary>
    public DateTime NextRetryAtUtc { get; set; }

    /// <summary>
    /// The last <see cref="StderrTailLimit"/> bytes the child wrote to stderr,
    /// decoded as UTF-8 (empty when it wrote nothing). Live view — readable at
    /// any time, so a channel failure can carry the child's own account of why
    /// it died. Starts at a byte boundary, not a code-point one: a truncated
    /// multi-byte sequence shows up as U+FFFD.
    /// </summary>
    internal string StderrTail
    {
        get
        {
            lock (_stderrGate)
                return _stderrLen == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(_stderrWindow, 0, _stderrLen);
        }
    }

    /// <summary>The background stderr drain (owned by <see cref="ProcessLifecycle"/>).</summary>
    internal Task StderrDrain { get; set; } = Task.CompletedTask;

    /// <summary>
    /// The stderr tail for a FAILURE report, after giving the drain a short
    /// bounded moment to catch up (the child may have closed stdout a beat
    /// before it finished writing its dying words). Never blocks a caller for
    /// longer than <paramref name="maxWait"/>.
    /// </summary>
    internal string CaptureStderrTail(TimeSpan maxWait)
    {
        try
        {
            StderrDrain.Wait(maxWait);
        }
        catch
        {
            // a faulted/aborted drain has nothing more to contribute
        }
        return StderrTail;
    }

    /// <summary>Keep only the newest bytes: shift out as much of the old window
    /// as the incoming chunk needs. Bounded memory regardless of how much the
    /// child logs.</summary>
    internal void AppendStderr(ReadOnlySpan<byte> bytes)
    {
        lock (_stderrGate)
        {
            if (bytes.Length >= _stderrWindow.Length)
            {
                bytes[^StderrTailLimit..].CopyTo(_stderrWindow);
                _stderrLen = StderrTailLimit;
                return;
            }
            var overflow = _stderrLen + bytes.Length - _stderrWindow.Length;
            if (overflow > 0)
            {
                Buffer.BlockCopy(_stderrWindow, overflow, _stderrWindow, 0, _stderrLen - overflow);
                _stderrLen -= overflow;
            }
            bytes.CopyTo(_stderrWindow.AsSpan(_stderrLen));
            _stderrLen += bytes.Length;
        }
    }

    /// <summary>Intentional recycle/idle reclaim flag — the client read loop
    /// uses it to distinguish a crash (churn) from a deliberate teardown.</summary>
    public volatile bool RecycleRequested;

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
            Process.WaitForExit(2000); // bounded settle so HasExited is truthful
        }
        catch
        {
            // kill races are best-effort; the process is already gone or will be
        }
        try
        {
            // the stderr pipe reaches EOF once the child is gone, so the drain
            // is settling — bound the wait, never block the caller on it
            StderrDrain.Wait(1000);
        }
        catch
        {
            // a faulted/aborted drain has nothing more to contribute
        }
        try
        {
            Process.Dispose();
        }
        catch
        {
            // dispose is idempotent-until-association; never tear down the caller
        }
    }
}

/// <summary>
/// stdio child-process lifecycle (spec §9.2): spawn-on-demand single-instance
/// reuse, idle reclamation (default 300s, descriptor-overridable), crash
/// backoff — 3 consecutive crashes arm exponential backoff from 2s up to 60s,
/// reset on the first successful response (MarkHealthy). The streak and its
/// deadline belong to the LIFECYCLE (T7.3), never to the channel: a crash
/// destroys the channel it happened on, so a channel-owned counter would restart
/// at 0 on every respawn, the threshold would be unreachable in the real flow,
/// and a child dying on start-up would be respawned on every single request with
/// no backoff and no cap. PATH resolution is
/// self-contained (equivalent to the Tools CommandResolver, deliberately not
/// referenced to avoid a Tools↔Binding cycle). Batch runtimes (.cmd/.bat) are
/// rejected defensively (audit M1 discipline).
/// </summary>
public sealed class ProcessLifecycle
{
    public const int BackoffCrashThreshold = 3;
    public static readonly TimeSpan BackoffStart = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(60);

    private readonly string _name;
    private readonly IReadOnlyList<string> _command;
    private readonly IReadOnlyDictionary<string, string?>? _env;
    private JsonRpcChannel? _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    // T7.3: crash/backoff state — LIFECYCLE scope, so it survives respawns.
    // Written by the reader thread (Churn) and read by the gate holder
    // (AcquireAsync): `Volatile` on both, and the deadline is kept as UTC ticks
    // so the cross-thread read/write is a single word on every platform.
    private int _consecutiveCrashes;
    private long _nextRetryAtUtcTicks; // 0 = no backoff armed

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(300);

    public ProcessLifecycle(
        string name,
        IReadOnlyList<string> command,
        TimeSpan idleTimeout = default,
        IReadOnlyDictionary<string, string?>? env = null
    )
    {
        _name = name;
        _command = command;
        _env = env;
        if (idleTimeout != default)
            IdleTimeout = idleTimeout;
    }

    /// <summary>
    /// Return the live channel (reusing the single instance), recycling it
    /// first when idle, or spawn a fresh one. While in crash backoff the call
    /// throws <see cref="TimeoutException"/> until the lifecycle's
    /// <c>_nextRetryAtUtc</c> — checked BEFORE anything is detached, because the
    /// refusal itself must not destroy the state it was decided from.
    /// </summary>
    public async Task<JsonRpcChannel> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // T7.3 step 1: the backoff deadline lives on the lifecycle, so it is
            // still readable after the channel that caused it has been disposed.
            // (It used to be read off the channel this method had just nulled —
            // the throw then became the reason the next acquire resumed.)
            var retryTicks = Volatile.Read(ref _nextRetryAtUtcTicks);
            if (retryTicks != 0)
            {
                var retry = new DateTime(retryTicks, DateTimeKind.Utc);
                if (DateTime.UtcNow < retry)
                    throw new TimeoutException(
                        $"extension '{_name}' in crash backoff until {retry:O}"
                    );
            }

            var live = _channel;
            if (live is not null && !live.Process.HasExited)
            {
                if (DateTime.UtcNow - _lastActivityUtc > IdleTimeout)
                {
                    // idle reclaim (spec §9.2): mark, kill, respawn on next use
                    live.RecycleRequested = true;
                    live.Dispose();
                    _channel = null;
                }
                else
                {
                    _lastActivityUtc = DateTime.UtcNow;
                    return live;
                }
            }
            // A dead-but-retained channel is dropped — the OBJECT, never the
            // crash history: that lives on the lifecycle now, which is why no
            // path here may reset it.
            if (_channel is { } dead)
            {
                _channel = null;
                dead.Dispose();
            }
            var fresh = await SpawnAsync(ct);
            // stamp the current streak onto the new generation so the channel a
            // caller holds reports the truth (diagnostics + released surface)
            MirrorCrashState(fresh);
            _channel = fresh;
            _lastActivityUtc = DateTime.UtcNow;
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Refresh the idle timer on every request write (B3 client calls).</summary>
    public void Touch() => _lastActivityUtc = DateTime.UtcNow;

    /// <summary>Reset crash/backoff state after the first successful response
    /// (B3). The ONLY place the streak is cleared: an idle reclaim or a recycle is
    /// a deliberate teardown and must not launder a crash loop into a fresh
    /// budget.</summary>
    internal void MarkHealthy()
    {
        Volatile.Write(ref _consecutiveCrashes, 0);
        Volatile.Write(ref _nextRetryAtUtcTicks, 0L);
        if (_channel is { } ch)
            MirrorCrashState(ch);
    }

    /// <summary>Bump the crash counter and arm/refresh backoff (B3 read-loop
    /// EOF). Defensive guard: only the CURRENT channel's crash is attributed —
    /// a stale reader EOF (channel already replaced by a newer spawn) must not
    /// corrupt the live channel's state or arm a ghost backoff.
    /// <para>
    /// The counter and the deadline are the LIFECYCLE's (T7.3), so the streak
    /// continues across the respawn the crash forces; the channel only receives
    /// a mirror of them.
    /// </para></summary>
    internal void Churn(JsonRpcChannel ch)
    {
        if (!ReferenceEquals(ch, _channel))
            return; // stale channel — the lifecycle already moved on
        var crashes = Interlocked.Increment(ref _consecutiveCrashes);
        if (crashes >= BackoffCrashThreshold)
        {
            var pow = Math.Min(crashes - BackoffCrashThreshold + 1, 6);
            var delay = Math.Min(
                BackoffStart.TotalSeconds * Math.Pow(2, pow - 1),
                BackoffMax.TotalSeconds
            );
            Volatile.Write(ref _nextRetryAtUtcTicks, DateTime.UtcNow.AddSeconds(delay).Ticks);
        }
        else
        {
            Volatile.Write(ref _nextRetryAtUtcTicks, 0L);
        }
        MirrorCrashState(ch);
    }

    /// <summary>Publish the lifecycle's streak onto <paramref name="ch"/> — the
    /// channel-side view (see <see cref="JsonRpcChannel.ConsecutiveCrashes"/>).</summary>
    private void MirrorCrashState(JsonRpcChannel ch)
    {
        ch.ConsecutiveCrashes = Volatile.Read(ref _consecutiveCrashes);
        var ticks = Volatile.Read(ref _nextRetryAtUtcTicks);
        ch.NextRetryAtUtc = ticks == 0 ? default : new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>Kill the tree and drop the channel (marks <see cref="JsonRpcChannel.RecycleRequested"/>).</summary>
    public async Task RecycleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_channel is { } ch)
            {
                ch.RecycleRequested = true;
                ch.Dispose();
            }
            _channel = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonRpcChannel> SpawnAsync(CancellationToken ct)
    {
        var exe = _command[0];
        if (
            exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidOperationException($"batch runtime '{exe}' is not supported");
        var resolved =
            ResolveExe(exe)
            ?? throw new InvalidOperationException($"runtime '{exe}' not found in PATH");
        var psi = new ProcessStartInfo(resolved)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (_env is not null)
        {
            foreach (var (k, v) in _env)
                psi.Environment[k] = v;
        }
        foreach (var a in _command.Skip(1))
            psi.ArgumentList.Add(a);
        var p =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {resolved}");
        var ch = new JsonRpcChannel
        {
            Process = p,
            Stdin = p.StandardInput.BaseStream,
            Stdout = p.StandardOutput.BaseStream,
            Stderr = p.StandardError.BaseStream,
            Name = _name,
        };
        // T7.1: start draining immediately — the child owns a finite stderr
        // pipe and blocks inside write(2) the moment it fills, so there is no
        // safe window to defer this to the first read.
        ch.StderrDrain = DrainStderrAsync(ch);
        return ch;
    }

    /// <summary>
    /// Read the child's stderr until EOF (process exit / pipe closed), keeping
    /// only the newest <see cref="JsonRpcChannel.StderrTailLimit"/> bytes in the
    /// channel's ring. Reads in fixed chunks rather than `ReadToEndAsync` on
    /// purpose: the point is a BOUNDED sink, and an unbounded string would just
    /// trade a deadlock for a memory leak on a chatty extension. Failures are
    /// swallowed — a broken pipe means the child is gone anyway, and the tail
    /// already holds whatever it managed to say.
    /// </summary>
    private static async Task DrainStderrAsync(JsonRpcChannel ch)
    {
        var chunk = new byte[4096];
        try
        {
            while (true)
            {
                var n = await ch.Stderr.ReadAsync(chunk).ConfigureAwait(false);
                if (n == 0)
                    break; // EOF — the child closed stderr or exited
                ch.AppendStderr(chunk.AsSpan(0, n));
            }
        }
        catch (ObjectDisposedException)
        {
            // Dispose closed the pipe under us — nothing left to drain
        }
        catch (IOException)
        {
            // broken pipe: the child died mid-write, keep the tail we have
        }
        catch (OperationCanceledException)
        {
            // async read aborted by teardown
        }
    }

    /// <summary>
    /// PATH lookup + absolute path passthrough. Note: the Tools CommandResolver
    /// has equivalent logic; it is deliberately NOT referenced here (keeps the
    /// generic PicoJsonRpc component free of Tools, which consumes Binding).
    /// </summary>
    private static string? ResolveExe(string name)
    {
        if (
            name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
        )
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (
            var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        )
        {
            var candidate = Path.Combine(dir, name);
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(candidate + ".exe"))
                    return candidate + ".exe";
                if (File.Exists(candidate + ".cmd") || File.Exists(candidate + ".bat"))
                    throw new InvalidOperationException($"batch runtime '{name}' is not supported");
            }
            else if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
