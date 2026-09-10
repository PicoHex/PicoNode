using System.Diagnostics;

namespace PicoJsonRpc;

/// <summary>
/// A live stdio channel to one child process: process handle + stdin/stdout
/// streams. Carries the crash/backoff state the lifecycle manages on behalf
/// of the client read loop (B3): <see cref="ConsecutiveCrashes"/> /
/// <see cref="NextRetryAtUtc"/> are bumped via <see cref="ProcessLifecycle.Churn"/>
/// and reset via <see cref="ProcessLifecycle.MarkHealthy"/>.
/// <see cref="RecycleRequested"/> distinguishes an INTENTIONAL recycle
/// (idle/RecycleAsync — not a crash) from an EOF the client observes spuriously.
/// </summary>
public sealed class JsonRpcChannel : IDisposable
{
    public required Process Process { get; init; }
    public required Stream Stdin { get; init; }
    public required Stream Stdout { get; init; }
    public required string Name { get; init; }
    public int ConsecutiveCrashes { get; set; }
    public DateTime NextRetryAtUtc { get; set; }

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
/// reset on the first successful response (MarkHealthy). PATH resolution is
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
    /// throws <see cref="TimeoutException"/> until <see cref="NextRetryAtUtc"/>.
    /// </summary>
    public async Task<JsonRpcChannel> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
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
            // dead-but-retained channel carries the crash/backoff state — read
            // it BEFORE dispose, then drop the reference so no later path
            // touches the disposed Process object.
            var crashed = _channel;
            _channel = null;
            if (crashed is not null)
            {
                var retry = crashed.NextRetryAtUtc;
                crashed.Dispose();
                if (retry != default && DateTime.UtcNow < retry)
                    throw new TimeoutException(
                        $"extension '{_name}' in crash backoff until {retry:O}"
                    );
            }
            var fresh = await SpawnAsync(ct);
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

    /// <summary>Reset crash/backoff state after the first successful response (B3).</summary>
    internal void MarkHealthy()
    {
        if (_channel is { } ch)
        {
            ch.ConsecutiveCrashes = 0;
            ch.NextRetryAtUtc = default;
        }
    }

    /// <summary>Bump the crash counter and arm/refresh backoff (B3 read-loop
    /// EOF). Defensive guard: only the CURRENT channel's crash is attributed —
    /// a stale reader EOF (channel already replaced by a newer spawn) must not
    /// corrupt the live channel's state or arm a ghost backoff.</summary>
    internal void Churn(JsonRpcChannel ch)
    {
        if (!ReferenceEquals(ch, _channel))
            return; // stale channel — the lifecycle already moved on
        ch.ConsecutiveCrashes++;
        if (ch.ConsecutiveCrashes >= BackoffCrashThreshold)
        {
            var pow = Math.Min(ch.ConsecutiveCrashes - BackoffCrashThreshold + 1, 6);
            var delay = Math.Min(
                BackoffStart.TotalSeconds * Math.Pow(2, pow - 1),
                BackoffMax.TotalSeconds
            );
            ch.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(delay);
        }
        else
        {
            ch.NextRetryAtUtc = default;
        }
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
            Name = _name,
        };
        return ch;
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
