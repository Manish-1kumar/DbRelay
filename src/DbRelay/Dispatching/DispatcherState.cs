namespace DbRelay.Dispatching;

/// <summary>Thread-safe liveness state of the dispatcher, read by the health check.</summary>
internal sealed class DispatcherState
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastSuccessfulPoll;
    private DateTimeOffset? _lastActivity;
    private int _consecutiveFailures;
    private int _abandonedHandlers;
    private string? _lastError;
    private bool _running;

    public bool Registered { get; set; }

    public void MarkRunning(bool running)
    {
        lock (_gate)
        {
            _running = running;
        }
    }

    public void PollSucceeded(DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastSuccessfulPoll = now;
            _consecutiveFailures = 0;
            _lastError = null;
        }
    }

    /// <summary>
    /// Records progress other than a poll (a finished delivery). While every slot is busy the dispatcher deliberately does not
    /// poll, so a busy dispatcher must not look stalled.
    /// </summary>
    public void RecordActivity(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastSuccessfulPoll is null || now > _lastSuccessfulPoll)
            {
                _lastActivity = now;
            }
        }
    }

    /// <summary>
    /// The number of abandoned handlers (CR-05) that are still running. This is the single source of truth, read by the dispatcher's
    /// FU-02 limit, the health check and a gauge. It is updated only with atomic increment/decrement: copying a separately
    /// maintained counter in here would let a stale last write win, leaving the count, and Degraded health, stuck.
    /// </summary>
    public int AbandonedHandlers => Volatile.Read(ref _abandonedHandlers);

    public void AbandonedHandlerStarted() => Interlocked.Increment(ref _abandonedHandlers);

    public void AbandonedHandlerFinished() => Interlocked.Decrement(ref _abandonedHandlers);

    public int PollFailed(Exception exception)
    {
        lock (_gate)
        {
            _lastError = exception.GetType().Name;
            return ++_consecutiveFailures;
        }
    }

    public Snapshot GetSnapshot()
    {
        lock (_gate)
        {
            var lastProgress = _lastActivity > _lastSuccessfulPoll ? _lastActivity : _lastSuccessfulPoll;
            return new Snapshot(_running, _lastSuccessfulPoll, lastProgress, _consecutiveFailures, _lastError);
        }
    }

    public readonly record struct Snapshot(
        bool Running,
        DateTimeOffset? LastSuccessfulPoll,
        DateTimeOffset? LastProgress,
        int ConsecutiveFailures,
        string? LastError);
}
