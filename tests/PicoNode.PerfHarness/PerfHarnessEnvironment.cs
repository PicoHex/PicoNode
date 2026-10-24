namespace PicoNode.PerfHarness;

/// <summary>
/// Process-wide settings the throughput harness needs before it can measure the hot path instead of the
/// runtime around it.
/// </summary>
internal static class PerfHarnessEnvironment
{
    /// <summary>
    /// Raises the thread-pool minimum threads for the duration of the process.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed: the same echo code, the same payload, the same 1-CPU affinity mask, five
    /// consecutive runs printed 333, 601, 9050, 5741 and 840 round trips per second. The stalls were
    /// ~500 ms wide — one thread-pool injection interval each — because every pool thread was occupied
    /// and the pool injects replacements at roughly one per second. On a masked core that lands inside a
    /// 20–600 ms measurement window and dominates it.
    ///
    /// <para>
    /// Raising the minimum does not weaken a single assertion: it removes a source of latency that is a
    /// property of the CLR's scheduling heuristic, not of the code under test. The floor is asserted
    /// against the best of several rounds either way.
    /// </para>
    /// </remarks>
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        const int Target = 16;
        if (worker < Target)
        {
            ThreadPool.SetMinThreads(Target, Math.Max(io, Target));
        }
    }
}
