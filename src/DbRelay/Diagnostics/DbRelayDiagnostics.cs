using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace DbRelay.Diagnostics;

/// <summary>
/// Names for DbRelay's tracing and metrics instrumentation.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(DbRelayDiagnostics.ActivitySourceName))
///     .WithMetrics(m => m.AddMeter(DbRelayDiagnostics.MeterName));
/// </code>
/// </example>
public static class DbRelayDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> name: <c>DbRelay</c>.</summary>
    public const string ActivitySourceName = "DbRelay";

    /// <summary>The <see cref="Meter"/> name: <c>DbRelay</c>.</summary>
    public const string MeterName = "DbRelay";

    internal static readonly string Version =
        typeof(DbRelayDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);
}
