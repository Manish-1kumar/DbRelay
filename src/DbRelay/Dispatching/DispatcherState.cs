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

    /// <summary>The number of abandoned handlers (CR-05) that are still running. Read by the health check and a gauge.</summary>
    public int AbandonedHandlers => Volatile.Read(ref _abandonedHandlers);

    public void SetAbandonedHandlers(int count) => Volatile.Write(ref _abandonedHandlers, count);

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
