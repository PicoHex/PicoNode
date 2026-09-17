namespace PicoNode.Web.Tests;

/// <summary>
/// Deterministic clock for keep-alive tests: timers fire only when
/// <see cref="Advance"/> is called, so "a ping happens after the interval" and
/// "a busy stream never pings" become facts about the clock rather than about
/// machine load. Real-time keep-alive assertions were load-sensitive: under
/// thread-pool starvation a 5 ms write cadence could slip past a 200 ms interval.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    /// <summary>Moves the clock forward and fires every timer that became due.</summary>
    public void Advance(TimeSpan delta)
    {
        List<Action>? due = null;
        lock (_gate)
        {
            _ticks += delta.Ticks;
            foreach (var timer in _timers.ToArray())
            {
                if (timer.IsDue(_ticks))
                {
                    (due ??= []).Add(timer.Fire);
                }
            }
        }

        if (due is null)
        {
            return;
        }

        foreach (var fire in due)
        {
            fire();
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long _dueTicks;
        private long _periodTicks;
        private bool _disposed;

        public ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            SetDue(dueTime);
            _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : Math.Max(0, period.Ticks);
        }

        public bool IsDue(long nowTicks) => !_disposed && _dueTicks <= nowTicks;

        public void Fire()
        {
            if (_disposed)
            {
                return;
            }

            // One-shot timers park themselves; periodic timers re-arm.
            _dueTicks = _periodTicks > 0 ? _dueTicks + _periodTicks : long.MaxValue;
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SetDue(dueTime);
            _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : Math.Max(0, period.Ticks);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void SetDue(TimeSpan dueTime) =>
            _dueTicks =
                dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : _owner.GetTimestamp() + Math.Max(0, dueTime.Ticks);
    }
}
