using DbRelay.Configuration;
using DbRelay.Diagnostics;
using DbRelay.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbRelay.Maintenance;

/// <summary>
/// Periodically deletes old delivered messages and refreshes the statistics behind the gauges. Safe to run on every instance:
/// providers implement deletes with row-skipping semantics, and statistics are read-only.
/// </summary>
internal sealed class OutboxMaintenanceService : BackgroundService
{
    private const int MaxCleanupBatchesPerRun = 100;

    /// <summary>When a run hits the per-run cap, the next run starts after this short pause instead of a full interval (CR-04).</summary>
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(1);

    private readonly IOutboxStore _store;
    private readonly DbRelayMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxMaintenanceService> _logger;
    private readonly MaintenanceOptions _options;
    private readonly bool _dispatcherRegistered;

    public OutboxMaintenanceService(
        IOutboxStore store,
        DbRelayMetrics metrics,
        TimeProvider time,
        IOptions<MaintenanceOptions> options,
        Dispatching.DispatcherState dispatcherState,
        ILogger<OutboxMaintenanceService> logger)
    {
        _store = store;
        _metrics = metrics;
        _time = time;
        _logger = logger;
        _options = options.Value;
        _dispatcherRegistered = dispatcherState.Registered;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleanupInterval = _options.CleanupEnabled ? _options.CleanupInterval : (TimeSpan?)null;

        // Gauges are only refreshed where messages are delivered. Enqueue-only nodes (DisableDispatcher) skip the statistics
        // query, so N web nodes don't multiply its cost (CR-03).
        var statisticsInterval = _dispatcherRegistered ? _options.StatisticsInterval : null;
        if (cleanupInterval is null && statisticsInterval is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var nextCleanup = now;
        var nextStatistics = now;

        while (!stoppingToken.IsCancellationRequested)
        {
            now = _time.GetUtcNow();

            if (cleanupInterval is { } ci && now >= nextCleanup)
            {
                var behind = await RunCleanupAsync(stoppingToken).ConfigureAwait(false);
                nextCleanup = _time.GetUtcNow() + (behind ? CatchUpDelay : ci);
            }

            if (statisticsInterval is { } si && now >= nextStatistics)
            {
                await RefreshStatisticsAsync(stoppingToken).ConfigureAwait(false);
                nextStatistics = _time.GetUtcNow() + si;
            }

            var next = cleanupInterval is null ? nextStatistics
                : statisticsInterval is null ? nextCleanup
                : nextCleanup < nextStatistics ? nextCleanup : nextStatistics;
            var wait = next - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <returns><see langword="true"/> if the run stopped at the per-run cap, meaning more rows are probably eligible.</returns>
    private async Task<bool> RunCleanupAsync(CancellationToken stoppingToken)
    {
        try
        {
            var total = 0;
            var lastBatchFull = false;
            for (var i = 0; i < MaxCleanupBatchesPerRun && !stoppingToken.IsCancellationRequested; i++)
            {
                var deleted = await _store.DeleteDeliveredAsync(_options.DeliveredRetention, _options.CleanupBatchSize, stoppingToken)
                    .ConfigureAwait(false);
                total += deleted;
                lastBatchFull = deleted >= _options.CleanupBatchSize;
                if (!lastBatchFull)
                {
                    break;
                }
            }

            if (total > 0)
            {
                Log.CleanupDeleted(_logger, total, _options.DeliveredRetention);
            }

            if (lastBatchFull)
            {
                Log.CleanupBehind(_logger, total, CatchUpDelay);
            }

            return lastBatchFull;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            Log.MaintenanceFailed(_logger, exception, "cleanup");
            return false;
        }
    }

    private async Task RefreshStatisticsAsync(CancellationToken stoppingToken)
    {
        try
        {
            _metrics.UpdateStatistics(await _store.GetStatisticsAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.MaintenanceFailed(_logger, exception, "statistics");
        }
    }
}
