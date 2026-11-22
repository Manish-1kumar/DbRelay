using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DbRelay.Diagnostics;

/// <summary>
/// DbRelay's metric instruments. One instance per service provider; disposed with it.
/// </summary>
internal sealed class DbRelayMetrics : IDisposable
{
    private const string MessageTypeTag = "dbrelay.message.type";
    private const string OutcomeTag = "dbrelay.outcome";

    private readonly Meter _meter;
    private readonly Counter<long> _enqueued;
    private readonly Counter<long> _delivered;
    private readonly Counter<long> _retried;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _released;
    private readonly Counter<long> _leaseLost;
    private readonly Counter<long> _pollFailures;
    private readonly Counter<long> _abandoned;
    private readonly Histogram<double> _deliveryDuration;
    private readonly Histogram<double> _claimDuration;
    private readonly Histogram<int> _claimBatchSize;
    private volatile OutboxStatistics? _statistics;

    public DbRelayMetrics(Dispatching.DispatcherState dispatcherState)
    {
        ArgumentNullException.ThrowIfNull(dispatcherState);
        _meter = new Meter(DbRelayDiagnostics.MeterName, DbRelayDiagnostics.Version);
        _enqueued = _meter.CreateCounter<long>("dbrelay.messages.enqueued", "{message}", "Messages written to the outbox (counted before the caller's transaction commits).");
        _delivered = _meter.CreateCounter<long>("dbrelay.messages.delivered", "{message}", "Messages delivered successfully.");
        _retried = _meter.CreateCounter<long>("dbrelay.messages.retried", "{message}", "Delivery attempts that failed transiently and were scheduled for retry.");
        _deadLettered = _meter.CreateCounter<long>("dbrelay.messages.dead_lettered", "{message}", "Messages moved to the dead-letter state.");
        _released = _meter.CreateCounter<long>("dbrelay.messages.released", "{message}", "Claimed messages returned to the queue without an attempt (shutdown or insufficient lease time).");
        _leaseLost = _meter.CreateCounter<long>("dbrelay.lease.lost", "{message}", "Outcome updates rejected because another worker had reclaimed the message.");
        _pollFailures = _meter.CreateCounter<long>("dbrelay.dispatcher.poll_failures", "{failure}", "Failed claim queries.");
        _abandoned = _meter.CreateCounter<long>("dbrelay.delivery.abandoned", "{delivery}", "Handler invocations abandoned because they ignored cancellation after the delivery timeout.");
        _deliveryDuration = _meter.CreateHistogram<double>("dbrelay.delivery.duration", "s", "Duration of delivery handler invocations.");
        _claimDuration = _meter.CreateHistogram<double>("dbrelay.claim.duration", "s", "Duration of claim queries.");
        _claimBatchSize = _meter.CreateHistogram<int>("dbrelay.claim.batch_size", "{message}", "Number of messages returned per claim.");

        _meter.CreateObservableGauge("dbrelay.messages.pending", () => Observe(s => s.Pending), "{message}", "Pending messages (requires MaintenanceOptions.StatisticsInterval).");
        _meter.CreateObservableGauge("dbrelay.messages.processing", () => Observe(s => s.Processing), "{message}", "Messages currently claimed.");
        _meter.CreateObservableGauge("dbrelay.messages.dead_lettered.current", () => Observe(s => s.DeadLettered), "{message}", "Messages currently in the dead-letter state.");
        _meter.CreateObservableGauge("dbrelay.messages.oldest_due_age", ObserveOldestDueAge, "s", "Age of the oldest due message since it became due.");
        _meter.CreateObservableGauge(
            "dbrelay.delivery.abandoned.active",
            () => dispatcherState.AbandonedHandlers,
            "{handler}",
            "Abandoned delivery handlers (they ignored cancellation) that are still running. At MaxConcurrency the dispatcher stops claiming.");
    }

    public void Enqueued(string messageType, int count) => _enqueued.Add(count, new KeyValuePair<string, object?>(MessageTypeTag, messageType));

    public void Delivered(string messageType) => _delivered.Add(1, new KeyValuePair<string, object?>(MessageTypeTag, messageType));

    public void Retried(string messageType) => _retried.Add(1, new KeyValuePair<string, object?>(MessageTypeTag, messageType));

    public void DeadLettered(string messageType) => _deadLettered.Add(1, new KeyValuePair<string, object?>(MessageTypeTag, messageType));

    public void Released(int count) => _released.Add(count);

    public void LeaseLost() => _leaseLost.Add(1);

    public void PollFailed() => _pollFailures.Add(1);

    public void HandlerAbandoned() => _abandoned.Add(1);

    public void RecordDelivery(string messageType, DeliveryOutcome outcome, TimeSpan duration) =>
        _deliveryDuration.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>(MessageTypeTag, messageType),
            new KeyValuePair<string, object?>(OutcomeTag, outcome.ToString()));

    public void RecordClaim(int count, TimeSpan duration)
    {
        _claimDuration.Record(duration.TotalSeconds);
        _claimBatchSize.Record(count);
    }

    public void UpdateStatistics(OutboxStatistics statistics) => _statistics = statistics;

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> Observe(Func<OutboxStatistics, long> selector)
    {
        var stats = _statistics;
        return stats is null ? [] : [new Measurement<long>(selector(stats))];
    }

    private IEnumerable<Measurement<double>> ObserveOldestDueAge()
    {
        var stats = _statistics;
        return stats is null ? [] : [new Measurement<double>(stats.OldestDueAge?.TotalSeconds ?? 0)];
    }
}

internal static class ActivityExtensions
{
    public static void SetError(this Activity? activity, string description)
    {
        activity?.SetStatus(ActivityStatusCode.Error, description);
    }
}
