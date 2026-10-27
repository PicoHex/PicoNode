namespace PicoNode.PerfHarness;

/// <summary>
/// Measures how fast <b>this machine</b> is at the moment a scenario is measured, so the scenario's
/// floor can be scaled by it instead of being trusted as an absolute number of operations per second.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves is measured, not hypothetical: <c>Tcp_churn_succeeds</c> runs at
/// ~1 100 connections/s when the harness is alone and at ~120 when <c>dotnet test PicoNode.slnx</c> is
/// compiling and serving the other nine suites, so an absolute floor either sits above the contended
/// number (a flaky test) or below it with so much headroom that it no longer catches a real regression.
/// Scaling the floor by the machine's own speed at measurement time removes the machine from the
/// comparison, leaving the code under test as the only variable.
/// </para>
/// <para>
/// Two probes, because the scenarios are not all bound by the same resource:
/// <list type="bullet">
///   <item><see cref="SampleCpuThroughput"/> — a deterministic integer/array mix over a cache-resident
///   working set. Its cost per unit of work is fixed, so the measured rate is the machine's CPU
///   throughput: the canonical "how fast is this box right now" number.</item>
///   <item><see cref="SampleHandoffLatency"/> — a two-thread ping-pong over semaphores. It does almost
///   no work per unit, so its rate is a property of the <b>scheduler</b>: how long a released thread
///   waits before it runs. The guarded scenarios are latency-bound round trips (write, wake the node,
///   wake the client), which is the resource this probe measures.</item>
/// </list>
/// Both are reported. The report for this change records which of the two actually tracked the
/// cross-process contention, and the floors are scaled by the one that did.
/// </para>
/// <para>
/// Sampling is <b>bracketed around</b> the scenario: a before-set and an after-set, each of
/// <see cref="SamplesPerBracket"/> short samples aggregated by their median. The median discards a
/// single descheduled sample, and taking the <b>slower</b> of the two bracket medians is the
/// conservative choice — it lowers the floor only when the machine was actually observed to be slower
/// across the window, never on a hunch.
/// </para>
/// </remarks>
internal static class MachineCalibration
{
    /// <summary>Short samples per bracket; the median of five discards one descheduled sample.</summary>
    internal const int SamplesPerBracket = 5;

    /// <summary>
    /// Element updates per CPU sample, chosen so one sample is a few milliseconds on a current desktop
    /// core: long enough that the stopwatch's ~100 ns resolution is noise, short enough that five of
    /// them do not change the cost of a scenario that already takes tens of milliseconds.
    /// </summary>
    private const int CpuUpdatesPerSample = 1 << 20;

    /// <summary>Thread handoffs per latency sample, also a few milliseconds on a current desktop core.</summary>
    private const int HandoffsPerSample = 1_000;

    /// <summary>
    /// The working set of the CPU probe: 4 096 ints = 16 KiB, i.e. cache resident (L1D on a current
    /// core is 32–48 KiB), so the probe measures execution throughput rather than memory bandwidth and
    /// does not itself become a memory-bound workload whose rate depends on what other processes are
    /// doing to the shared last-level cache.
    /// </summary>
    private const int WorkingSetElements = 4_096;

    /// <summary>Consumes the probe's result so no runtime can treat the loop as dead code.</summary>
    private static long _sink;

    /// <summary>
    /// Opens the measurement window of one scenario. Call <see cref="Window.Worst"/> once the scenario's
    /// timed region is over to close it.
    /// </summary>
    internal static Window Start() => new(Bracket());

    /// <summary>
    /// The scale factor for a floor: how fast the machine is now against how fast it was when the floor
    /// constants were derived. 1.0 on the reference machine, 0.5 on a machine running at half speed.
    /// </summary>
    /// <remarks>
    /// Clamped to <see cref="MinScale"/>–<see cref="MaxScale"/>. The lower clamp keeps a pathological
    /// reading (a machine an order of magnitude slower) from scaling a floor into meaninglessness; the
    /// upper one keeps a machine much faster than the reference from scaling a floor above what the code
    /// under test can do on it, which would be a flake in the opposite direction. Both bounds are wider
    /// than anything measured on this repository's machines (see the report).
    /// </remarks>
    internal static double Scale(double actual, double baseline) =>
        Math.Clamp(actual / baseline, MinScale, MaxScale);

    /// <summary>
    /// The CPU and handoff rates the floor constants in <see cref="PerfHarnessTests"/> were derived on.
    /// </summary>
    /// <remarks>
    /// Both were measured on this repository's reference machine with the harness running alone; the
    /// report records the sample spread they came from. They are the denominator of <see cref="Scale"/>:
    /// a machine (or a moment) measuring half of these gets a floor at half the constant, which is what
    /// removes the machine from the comparison without removing the bound.
    /// </remarks>
    internal const double CpuBaseline = 614_820_287d;
    internal const double HandoffBaseline = 775_795d;

    /// <summary>One CPU bracket: the median rate of <see cref="SamplesPerBracket"/> fixed-work samples.</summary>
    private static double SampleCpuThroughput()
    {
        // Allocated and seeded outside the timed region: the probe must measure execution throughput,
        // not the cost of a 16 KiB allocation.
        var buffer = new int[WorkingSetElements];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = unchecked((int)(i * 2_654_435_761 + 1));
        }

        var rates = new double[SamplesPerBracket];
        for (var sample = 0; sample < rates.Length; sample++)
        {
            var stopwatch = Stopwatch.StartNew();
            RunCpuWork(buffer, CpuUpdatesPerSample);
            stopwatch.Stop();
            rates[sample] = CpuUpdatesPerSample / Math.Max(stopwatch.Elapsed.TotalSeconds, 1e-9);
        }

        return Median(rates);
    }

    /// <summary>
    /// One handoff bracket: the median rate of <see cref="SamplesPerBracket"/> ping-pongs of
    /// <see cref="HandoffsPerSample"/> handoffs, in handoffs per second.
    /// </summary>
    private static double SampleHandoffLatency()
    {
        var rates = new double[SamplesPerBracket];
        for (var sample = 0; sample < rates.Length; sample++)
        {
            var stopwatch = Stopwatch.StartNew();
            RunHandoffWork(HandoffsPerSample);
            stopwatch.Stop();
            rates[sample] = HandoffsPerSample / Math.Max(stopwatch.Elapsed.TotalSeconds, 1e-9);
        }

        return Median(rates);
    }

    private static CalibrationBracket Bracket() => new(SampleCpuThroughput(), SampleHandoffLatency());

    private const double MinScale = 0.05;
    private const double MaxScale = 4.0;

    /// <summary>
    /// Fixed amount of integer work over a cache-resident array. Every update feeds the next, so the
    /// dependency chain cannot be reordered or vectorised into something much cheaper by a different JIT —
    /// a probe whose cost changes with the code generator is not a unit of machine work.
    /// </summary>
    private static void RunCpuWork(int[] buffer, long elementUpdates)
    {
        var state = unchecked((int)_sink) | 1;
        var remaining = elementUpdates;
        while (remaining > 0)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                state = unchecked(state * 31 + buffer[i]);
                buffer[i] = state & 0xFFFF;
            }

            remaining -= buffer.Length;
        }

        _sink = state;
    }

    /// <summary>
    /// Two threads passing a token to each other. Each turn is a blocking wait plus a wake-up, so the
    /// rate is dominated by how promptly the scheduler runs a released thread — the same latency a
    /// loopback round trip pays twice per message.
    /// </summary>
    private static void RunHandoffWork(int handoffs)
    {
        using var toServer = new SemaphoreSlim(0, 1);
        using var toClient = new SemaphoreSlim(0, 1);
        using var start = new ManualResetEventSlim(false);

        var server = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < handoffs; i++)
            {
                toServer.Wait();
                toClient.Release();
            }
        })
        {
            IsBackground = true,
            Name = "calibration-handoff",
        };
        server.Start();
        start.Set();

        for (var i = 0; i < handoffs; i++)
        {
            toServer.Release();
            toClient.Wait();
        }

        server.Join();
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }

    /// <summary>The calibration of one scenario, bracketed around its timed region.</summary>
    internal sealed class Window
    {
        private readonly CalibrationBracket _before;

        internal Window(CalibrationBracket before) => _before = before;

        /// <summary>
        /// Closes the window and returns the slower bracket per probe: the machine capability the
        /// scenario is held to is the worst one actually observed across its own window.
        /// </summary>
        internal CalibrationBracket Worst()
        {
            var after = Bracket();
            return new CalibrationBracket(
                Math.Min(_before.CpuUpdatesPerSecond, after.CpuUpdatesPerSecond),
                Math.Min(_before.HandoffsPerSecond, after.HandoffsPerSecond)
            );
        }
    }
}

/// <summary>
/// What the machine measured, in each probe's own unit, across a single scenario's window.
/// </summary>
internal readonly record struct CalibrationBracket(double CpuUpdatesPerSecond, double HandoffsPerSecond);
