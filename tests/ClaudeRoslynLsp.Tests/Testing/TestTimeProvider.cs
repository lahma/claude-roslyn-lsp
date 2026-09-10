namespace ClaudeRoslynLsp.Tests.Testing;

/// <summary>
/// A clock the test moves by hand, with timers that fire when it passes their due time.
/// </summary>
/// <remarks>
/// Hand-rolled because the package budget is a hard rule and
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> is a package. It implements exactly what this
/// repository's <see cref="TimeProvider"/> callers use — <see cref="GetTimestamp"/>,
/// <see cref="GetUtcNow"/> and <see cref="CreateTimer"/> — and nothing else; a test that reaches for
/// something missing should add it here rather than fall back to real time, because a test that
/// waits two minutes for a readiness timeout is a test nobody runs.
/// </remarks>
internal sealed class TestTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<TestTimer> _timers = [];

    private DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    /// <summary>Ticks, so <see cref="TimeProvider.GetElapsedTime(long)"/> is exact rather than scaled.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new TestTimer(this, callback, state);
        timer.Change(dueTime, period);

        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that comes due on the way.</summary>
    /// <remarks>
    /// One timer at a time, in due order, with the clock set to each timer's own due moment before it
    /// runs — so a callback that reads the clock sees the time it was scheduled for rather than the
    /// end of the jump.
    /// </remarks>
    /// <param name="by">How far forward.</param>
    internal void Advance(TimeSpan by)
    {
        DateTimeOffset target;

        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            TestTimer? next = null;

            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.NextDue is { } due && due <= target && (next?.NextDue is not { } best || due < best))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.NextDue!.Value;
            }

            next.Fire();
        }
    }

    /// <summary>Forgets a disposed timer.</summary>
    private void Remove(TestTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>A timer whose only clock is <see cref="TestTimeProvider"/>.</summary>
    private sealed class TestTimer : ITimer
    {
        private readonly TestTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        internal TestTimer(TestTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
        }

        /// <summary>When this timer next fires, or null when it is not scheduled.</summary>
        internal DateTimeOffset? NextDue { get; private set; }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            NextDue = dueTime == Timeout.InfiniteTimeSpan ? null : _provider.GetUtcNow() + dueTime;
            return true;
        }

        /// <summary>Runs the callback and schedules the next repeat, if there is one.</summary>
        internal void Fire()
        {
            NextDue = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero
                ? null
                : _provider.GetUtcNow() + _period;

            _callback(_state);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            NextDue = null;
            _provider.Remove(this);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
