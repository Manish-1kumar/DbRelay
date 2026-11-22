using DbRelay.Configuration;
using Microsoft.Extensions.Options;

namespace DbRelay.Retry;

/// <summary>Computes the delay before the next delivery attempt.</summary>
public interface IRetryPolicy
{
    /// <summary>Gets the delay to wait after attempt <paramref name="failedAttempt"/> failed.</summary>
    /// <param name="failedAttempt">The 1-based number of the attempt that failed.</param>
    /// <returns>A non-negative delay. DbRelay caps it at <see cref="RetryOptions.MaxDelay"/>.</returns>
    TimeSpan GetDelay(int failedAttempt);
}

/// <summary>
/// Exponential backoff with "equal jitter": the capped exponential delay <c>d</c> is turned into a random value in
/// <c>[d/2, d]</c>. The floor of <c>d/2</c> avoids retry storms of near-instant retries. The random half spreads out retries that
/// were scheduled together (for example after a broker outage) instead of synchronizing them.
/// </summary>
public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private readonly RetryOptions _options;
    private readonly Func<double> _random;

    /// <summary>Creates the policy from configured <see cref="RetryOptions"/>.</summary>
    /// <param name="options">The retry options.</param>
    public ExponentialBackoffRetryPolicy(IOptions<RetryOptions> options)
#pragma warning disable CA5394 // Jitter does not need cryptographic randomness.
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), Random.Shared.NextDouble)
#pragma warning restore CA5394
    {
    }

    internal ExponentialBackoffRetryPolicy(RetryOptions options, Func<double> random)
    {
        _options = options;
        _random = random;
    }

    /// <inheritdoc />
    public TimeSpan GetDelay(int failedAttempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);

        var maxMs = _options.MaxDelay.TotalMilliseconds;
        var exponential = _options.InitialDelay.TotalMilliseconds * Math.Pow(_options.BackoffMultiplier, failedAttempt - 1);
        var capped = double.IsFinite(exponential) ? Math.Min(exponential, maxMs) : maxMs;
        var jittered = (capped / 2) + (_random() * capped / 2);
        return TimeSpan.FromMilliseconds(jittered);
    }
}
