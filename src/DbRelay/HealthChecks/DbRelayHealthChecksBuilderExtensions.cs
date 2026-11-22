using DbRelay.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the DbRelay health check.</summary>
public static class DbRelayHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a health check that reports Unhealthy (or <paramref name="failureStatus"/>) when the database is unreachable, and Degraded
    /// when the dispatcher is stalled or an optional backlog threshold is exceeded.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The registration name.</param>
    /// <param name="configure">Optional threshold configuration.</param>
    /// <param name="failureStatus">The status reported when the database check fails. Defaults to Unhealthy.</param>
    /// <param name="tags">Optional tags.</param>
    /// <returns>The builder.</returns>
    public static IHealthChecksBuilder AddDbRelay(
        this IHealthChecksBuilder builder,
        string name = "dbrelay",
        Action<DbRelayHealthCheckOptions>? configure = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (configure is not null)
        {
            builder.Services.Configure(name, configure);
        }

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => ActivatorUtilities.CreateInstance<DbRelayHealthCheck>(sp),
            failureStatus,
            tags));
    }
}
