namespace PicoNode;

/// <summary>
/// The one place a configured drain budget becomes a value the timer APIs accept.
/// </summary>
/// <remarks>
/// <see cref="TcpNodeOptions.DrainTimeout"/> and <see cref="UdpNodeOptions.DrainTimeout"/> are plain
/// mutable properties: the constructor rejects only a negative value, the hot-reload path takes
/// whatever <see cref="TimeSpan.TryParse(string, out TimeSpan)"/> accepts — up to
/// <see cref="TimeSpan.MaxValue"/> — and a host can assign anything at all directly. But all three
/// timer-backed waits a stop path uses (<c>CancellationTokenSource.CancelAfter</c>,
/// <c>Task.WaitAsync(TimeSpan)</c> and <c>Task.Delay</c>) share one validation and throw
/// <see cref="ArgumentOutOfRangeException"/> above <see cref="MaxTimerDuration"/> (0xFFFFFFFE ms,
/// i.e. ~49.7 days). A budget above that did not shorten the drain, it removed it: the throw came
/// from <c>CancelAfter</c>, which sits before the drain's own <c>try</c>, so no connection was asked
/// to close, the node stayed in <see cref="NodeState.Stopping"/>, and a later
/// <c>DisposeAsync</c> rethrew after the teardown had already run.
/// </remarks>
internal static class DrainBudget
{
    /// <summary>
    /// The longest timeout the timer-backed waits accept: <c>Task.WaitAsync(TimeSpan)</c>,
    /// <c>Task.Delay</c> and <c>CancellationTokenSource.CancelAfter</c> share one validation and
    /// throw <see cref="ArgumentOutOfRangeException"/> above it — 0xFFFFFFFE ms, i.e. ~49.7 days.
    /// </summary>
    internal static readonly TimeSpan MaxTimerDuration = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    /// <summary>
    /// The budget a stop path hands to its deadline timer, for a given configured
    /// <c>DrainTimeout</c>: <see cref="TimeSpan.Zero"/> ("no deadline at all") is preserved, every
    /// positive value is capped at <see cref="MaxTimerDuration"/>, and anything in between is used
    /// unchanged.
    /// </summary>
    internal static TimeSpan Effective(TimeSpan configured)
    {
        if (configured <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return configured < MaxTimerDuration ? configured : MaxTimerDuration;
    }
}
