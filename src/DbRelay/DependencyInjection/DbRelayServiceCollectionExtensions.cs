using DbRelay;
using DbRelay.Configuration;
using DbRelay.Diagnostics;
using DbRelay.Dispatching;
using DbRelay.Maintenance;
using DbRelay.Retry;
using DbRelay.Serialization;
using DbRelay.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers DbRelay services.</summary>
public static class DbRelayServiceCollectionExtensions
{
    /// <summary>
    /// Adds DbRelay: <see cref="IOutbox"/>, <see cref="IOutboxAdmin"/>, the background dispatcher and maintenance services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures DbRelay. It must select a storage provider (for example <c>relay.UsePostgreSql(...)</c>) and,
    /// unless <see cref="DbRelayBuilder.DisableDispatcher"/> is called, a delivery handler.</param>
    /// <returns>The service collection.</returns>
    /// <exception cref="InvalidOperationException">No storage provider or delivery handler was configured, or DbRelay was already added.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddDbRelay(relay =>
    /// {
    ///     relay.UsePostgreSql(connectionString);
    ///     relay.UseDeliveryHandler&lt;MyBrokerPublisher&gt;();
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddDbRelay(this IServiceCollection services, Action<DbRelayBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(d => d.ServiceType == typeof(DbRelayMarker)))
        {
            throw new InvalidOperationException("AddDbRelay has already been called for this service collection.");
        }

        services.AddSingleton<DbRelayMarker>();

        services.AddOptions<OutboxOptions>().ValidateOnStart();
        services.AddOptions<DispatcherOptions>().ValidateOnStart();
        services.AddOptions<RetryOptions>().ValidateOnStart();
        services.AddOptions<MaintenanceOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OutboxOptions>, OutboxOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DispatcherOptions>, DispatcherOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RetryOptions>, RetryOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MaintenanceOptions>, MaintenanceOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DbRelayMetrics>();
        services.TryAddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();

        var builder = new DbRelayBuilder(services);
        configure(builder);

        if (!builder.HasStorage)
        {
            throw new InvalidOperationException(
                "No DbRelay storage provider is configured. Call relay.UsePostgreSql(...) (DbRelay.PostgreSql package) or relay.UseSqlServer(...) (DbRelay.SqlServer package) inside AddDbRelay.");
        }

        if (builder.DispatcherEnabled && !builder.HasDeliveryHandler)
        {
            throw new InvalidOperationException(
                "No delivery handler is configured. Call relay.UseDeliveryHandler<THandler>() or relay.UseDeliveryHandler(delegate), " +
                "or call relay.DisableDispatcher() if this application only enqueues messages.");
        }

        var jsonOptions = builder.JsonOptions;
        services.TryAddSingleton<IMessageSerializer>(_ => new SystemTextJsonMessageSerializer(jsonOptions));
        var typeNames = new Dictionary<Type, string>(builder.TypeNames);
        services.AddSingleton(new MessageTypeRegistry(typeNames));

        services.AddSingleton<IOutbox, Outbox>();
        services.AddSingleton<IOutboxAdmin, OutboxAdmin>();

        services.AddSingleton(new DispatcherState { Registered = builder.DispatcherEnabled });
        services.AddHostedService<OutboxMaintenanceService>();
        if (builder.DispatcherEnabled)
        {
            services.AddHostedService<OutboxDispatcher>();
        }

        return services;
    }

    private sealed class DbRelayMarker;
}
