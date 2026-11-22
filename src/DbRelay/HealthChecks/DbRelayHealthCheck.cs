using DbRelay.Configuration;
using DbRelay.Dispatching;
using DbRelay.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DbRelay.HealthChecks;

/// <summary>Options for the DbRelay health check.</summary>
public sealed class DbRelayHealthCheckOptions
{
    /// <summary>
    /// Report <see cref="HealthStatus.Degraded"/> when the oldest due message has waited longer than this. <see langword="null"/>
    /// (the default) disables the check. Enabling any threshold adds a statistics query to each health probe.
    /// </summary>
    public TimeSpan? MaxOldestDueAge { get; set; }

    /// <summary>Report <see cref="HealthStatus.Degraded"/> when more than this many messages are dead-lettered. <see langword="null"/> (the default) disables the check.</summary>
    public long? MaxDeadLettered { get; set; }
}

/// <summary>
/// Checks database connectivity, dispatcher liveness and optional backlog thresholds.
/// </summary>
internal sealed class DbRelayHealthCheck : IHealthCheck
{
    private readonly IOutboxStore _store;
    private readonly DispatcherState _state;
    private readonly DispatcherOptions _dispatcher;
    private readonly IOptionsMonitor<DbRelayHealthCheckOptions> _options;
    private readonly TimeProvider _time;

    public DbRelayHealthCheck(
        IOutboxStore store,
        DispatcherState state,
        IOptions<DispatcherOptions> dispatcher,
        IOptionsMonitor<DbRelayHealthCheckOptions> options,
        TimeProvider time)
    {
        _store = store;
        _state = state;
        _dispatcher = dispatcher.Value;
        _options = options;
        _time = time;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        try
        {
            await _store.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only the exception type is exposed. Health endpoints are often public, and provider exceptions can contain host names,
            // database names or SQL (FU-08). Diagnose with the provider's own logging or the dispatcher logs.
            data["error"] = exception.GetType().Name;
            return new HealthCheckResult(context.Registration.FailureStatus, "The outbox database is unreachable or the outbox table is missing.", exception: null, data);
        }

        var degraded = new List<string>();

        if (_state.Registered)
        {
            var snapshot = _state.GetSnapshot();
            data["dispatcher.running"] = snapshot.Running;
            data["dispatcher.consecutive_poll_failures"] = snapshot.ConsecutiveFailures;
            if (snapshot.LastSuccessfulPoll is { } last)
            {
                data["dispatcher.last_successful_poll"] = last;
            }

            var abandoned = _state.AbandonedHandlers;
            data["dispatcher.abandoned_handlers_running"] = abandoned;
            if (abandoned > 0)
            {
                // FU-02: handlers that ignored cancellation are still running; at MaxConcurrency the dispatcher pauses claiming.
                degraded.Add($"{abandoned} abandoned delivery handler(s) that ignored cancellation are still running");
            }

            if (!snapshot.Running)
            {
                degraded.Add("the dispatcher is not running");
            }
            else if (snapshot.ConsecutiveFailures > 0)
            {
                degraded.Add($"the dispatcher's last {snapshot.ConsecutiveFailures} poll(s) failed ({snapshot.LastError})");
            }
            else if (snapshot.LastProgress is { } lastProgress && _time.GetUtcNow() - lastProgress > StaleThreshold())
            {
                degraded.Add($"the dispatcher has made no progress since {lastProgress:O}");
            }
        }

        var options = _options.Get(context.Registration.Name);
        if (options.MaxOldestDueAge is not null || options.MaxDeadLettered is not null)
        {
            OutboxStatistics stats;
            try
            {
                stats = await _store.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The database answered the connectivity check, so a failed or slow statistics query is a degraded backlog signal,
                // not an outage. Reporting Unhealthy here could get pods restarted under load (CR-19).
                degraded.Add("the backlog statistics query failed");
                data["error"] = exception.GetType().Name;
                return HealthCheckResult.Degraded("DbRelay is degraded: " + string.Join("; ", degraded) + ".", exception: null, data);
            }

            data["messages.pending"] = stats.Pending;
            data["messages.processing"] = stats.Processing;
            data["messages.dead_lettered"] = stats.DeadLettered;
            if (stats.OldestDueAge is { } age)
            {
                data["messages.oldest_due_age_seconds"] = age.TotalSeconds;
            }

            if (options.MaxOldestDueAge is { } maxAge && stats.OldestDueAge > maxAge)
            {
                degraded.Add($"the oldest due message has waited {stats.OldestDueAge} (threshold {maxAge})");
            }

            if (options.MaxDeadLettered is { } maxDead && stats.DeadLettered > maxDead)
            {
                degraded.Add($"{stats.DeadLettered} messages are dead-lettered (threshold {maxDead})");
            }
        }

        return degraded.Count == 0
            ? HealthCheckResult.Healthy("DbRelay is healthy.", data)
            : HealthCheckResult.Degraded("DbRelay is degraded: " + string.Join("; ", degraded) + ".", data: data);
    }

    private TimeSpan StaleThreshold()
    {
        var threshold = TimeSpan.FromMinutes(1);
        var polling = _dispatcher.PollingInterval * 10;
        var delivery = _dispatcher.DeliveryTimeout * 2;
        if (polling > threshold)
        {
            threshold = polling;
        }

        return delivery > threshold ? delivery : threshold;
    }
}
