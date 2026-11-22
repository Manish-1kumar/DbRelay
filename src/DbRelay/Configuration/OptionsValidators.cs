using DbRelay.Storage;
using Microsoft.Extensions.Options;

namespace DbRelay.Configuration;

internal sealed class OutboxOptionsValidator : IValidateOptions<OutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxOptions options)
    {
        var errors = new List<string>();
        if (options.MaxPayloadBytes is < 1 or > 256 * 1024 * 1024)
        {
            errors.Add("DbRelay: OutboxOptions.MaxPayloadBytes must be between 1 and 268435456 (256 MiB).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class DispatcherOptionsValidator : IValidateOptions<DispatcherOptions>
{
    public ValidateOptionsResult Validate(string? name, DispatcherOptions options)
    {
        var errors = new List<string>();
        if (options.BatchSize < 1 || options.BatchSize > OutboxLimits.MaxClaimBatchSize)
        {
            errors.Add($"DbRelay: DispatcherOptions.BatchSize must be between 1 and {OutboxLimits.MaxClaimBatchSize}.");
        }

        if (options.MaxConcurrency is < 1 or > 256)
        {
            errors.Add("DbRelay: DispatcherOptions.MaxConcurrency must be between 1 and 256.");
        }

        if (options.PollingInterval < TimeSpan.FromMilliseconds(10) || options.PollingInterval > TimeSpan.FromHours(1))
        {
            errors.Add("DbRelay: DispatcherOptions.PollingInterval must be between 10 milliseconds and 1 hour.");
        }

        if (options.LeaseDuration < TimeSpan.FromSeconds(1) || options.LeaseDuration > TimeSpan.FromHours(1))
        {
            errors.Add("DbRelay: DispatcherOptions.LeaseDuration must be between 1 second and 1 hour.");
        }

        if (options.DeliveryTimeout <= TimeSpan.Zero || options.DeliveryTimeout > TimeSpan.FromMinutes(30))
        {
            errors.Add("DbRelay: DispatcherOptions.DeliveryTimeout must be greater than zero and at most 30 minutes.");
        }
        else if (options.LeaseDuration < options.DeliveryTimeout * 2)
        {
            errors.Add(
                $"DbRelay: DispatcherOptions.LeaseDuration ({options.LeaseDuration}) must be at least twice DeliveryTimeout ({options.DeliveryTimeout}), " +
                "so a lease cannot expire while a delivery is still allowed to run.");
        }

        if (options.MaxErrorBackoff < TimeSpan.FromSeconds(1) || options.MaxErrorBackoff > TimeSpan.FromMinutes(10))
        {
            errors.Add("DbRelay: DispatcherOptions.MaxErrorBackoff must be between 1 second and 10 minutes.");
        }

        if (options.WorkerId is { } workerId && (workerId.Length == 0 || workerId.Length > OutboxLimits.MaxWorkerIdLength))
        {
            errors.Add($"DbRelay: DispatcherOptions.WorkerId must be 1 to {OutboxLimits.MaxWorkerIdLength} characters.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class RetryOptionsValidator : IValidateOptions<RetryOptions>
{
    public ValidateOptionsResult Validate(string? name, RetryOptions options)
    {
        var errors = new List<string>();
        if (options.MaxAttempts is < 1 or > 1000)
        {
            errors.Add("DbRelay: RetryOptions.MaxAttempts must be between 1 and 1000.");
        }

        if (options.InitialDelay <= TimeSpan.Zero || options.InitialDelay > TimeSpan.FromDays(1))
        {
            errors.Add("DbRelay: RetryOptions.InitialDelay must be greater than zero and at most 1 day.");
        }

        if (options.MaxDelay < options.InitialDelay || options.MaxDelay > TimeSpan.FromDays(7))
        {
            errors.Add("DbRelay: RetryOptions.MaxDelay must be at least InitialDelay and at most 7 days.");
        }

        if (double.IsNaN(options.BackoffMultiplier) || options.BackoffMultiplier < 1.0 || options.BackoffMultiplier > 10.0)
        {
            errors.Add("DbRelay: RetryOptions.BackoffMultiplier must be between 1 and 10.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class MaintenanceOptionsValidator : IValidateOptions<MaintenanceOptions>
{
    public ValidateOptionsResult Validate(string? name, MaintenanceOptions options)
    {
        var errors = new List<string>();
        if (options.DeliveredRetention < TimeSpan.Zero || options.DeliveredRetention > TimeSpan.FromDays(3650))
        {
            errors.Add("DbRelay: MaintenanceOptions.DeliveredRetention must be between 0 and 3650 days.");
        }

        if (options.CleanupInterval < TimeSpan.FromSeconds(1) || options.CleanupInterval > TimeSpan.FromDays(1))
        {
            errors.Add("DbRelay: MaintenanceOptions.CleanupInterval must be between 1 second and 1 day.");
        }

        if (options.CleanupBatchSize is < 1 or > 10000)
        {
            errors.Add("DbRelay: MaintenanceOptions.CleanupBatchSize must be between 1 and 10000.");
        }

        if (options.StatisticsInterval is { } interval && (interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromDays(1)))
        {
            errors.Add("DbRelay: MaintenanceOptions.StatisticsInterval must be between 1 second and 1 day, or null to disable.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
