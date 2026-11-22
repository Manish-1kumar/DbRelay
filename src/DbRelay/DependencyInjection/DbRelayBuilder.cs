using System.Text.Json;
using DbRelay.Configuration;
using DbRelay.Dispatching;
using DbRelay.Retry;
using DbRelay.Serialization;
using DbRelay.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbRelay;

/// <summary>
/// Configures DbRelay. Obtained from <see cref="DbRelayServiceCollectionExtensions.AddDbRelay"/>.
/// </summary>
public sealed class DbRelayBuilder
{
    private readonly Dictionary<Type, string> _typeNames = [];
    private readonly Dictionary<string, Type> _namesInUse = new(StringComparer.Ordinal);

    internal DbRelayBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The service collection. Storage providers use it to register their services.</summary>
    public IServiceCollection Services { get; }

    internal bool HasStorage { get; private set; }

    internal bool HasDeliveryHandler { get; private set; }

    internal bool DispatcherEnabled { get; private set; } = true;

    internal IReadOnlyDictionary<Type, string> TypeNames => _typeNames;

    internal JsonSerializerOptions? JsonOptions { get; private set; }

    /// <summary>
    /// Registers the storage provider. Called by provider packages (for example <c>UsePostgreSql</c>); application code normally does
    /// not call this directly. Only one storage provider can be registered.
    /// </summary>
    /// <param name="factory">Creates the store. It is resolved once (singleton).</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder UseStorage(Func<IServiceProvider, IOutboxStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (HasStorage)
        {
            throw new InvalidOperationException("A DbRelay storage provider has already been configured. Call only one Use{Provider} method.");
        }

        Services.AddSingleton(factory);
        HasStorage = true;
        return this;
    }

    /// <summary>
    /// Registers the delivery handler type. A new instance is resolved from a fresh DI scope for every delivery.
    /// </summary>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <returns>This builder.</returns>
    public DbRelayBuilder UseDeliveryHandler<THandler>()
        where THandler : class, IMessageDeliveryHandler
    {
        EnsureNoHandler();
        Services.AddScoped<IMessageDeliveryHandler, THandler>();
        HasDeliveryHandler = true;
        return this;
    }

    /// <summary>Registers a delegate as the delivery handler.</summary>
    /// <param name="handler">The delivery function.</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder UseDeliveryHandler(Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureNoHandler();
        Services.AddSingleton<IMessageDeliveryHandler>(new DelegateDeliveryHandler(handler));
        HasDeliveryHandler = true;
        return this;
    }

    /// <summary>
    /// Sets the logical type name stored for messages of type <typeparamref name="TMessage"/>. This takes precedence over
    /// <see cref="OutboxMessageTypeAttribute"/>. Names must be unique.
    /// </summary>
    /// <typeparam name="TMessage">The message CLR type.</typeparam>
    /// <param name="name">The logical name (1 to 256 characters).</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder MapMessageType<TMessage>(string name)
    {
        MessageTypeRegistry.ValidateName(name, nameof(name));
        if (_namesInUse.TryGetValue(name, out var existing) && existing != typeof(TMessage))
        {
            throw new ArgumentException($"The message type name '{name}' is already mapped to '{existing.FullName}'.", nameof(name));
        }

        if (_typeNames.TryGetValue(typeof(TMessage), out var previous))
        {
            _namesInUse.Remove(previous);
        }

        _typeNames[typeof(TMessage)] = name;
        _namesInUse[name] = typeof(TMessage);
        return this;
    }

    /// <summary>Uses <paramref name="options"/> for the default System.Text.Json serializer.</summary>
    /// <param name="options">The serializer options.</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder UseJsonSerializerOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        JsonOptions = options;
        return this;
    }

    /// <summary>Replaces the default serializer.</summary>
    /// <param name="serializer">The serializer.</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder UseSerializer(IMessageSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        Services.Replace(ServiceDescriptor.Singleton(serializer));
        return this;
    }

    /// <summary>Replaces the default retry policy (<see cref="ExponentialBackoffRetryPolicy"/>).</summary>
    /// <typeparam name="TPolicy">The policy type, resolved as a singleton.</typeparam>
    /// <returns>This builder.</returns>
    public DbRelayBuilder UseRetryPolicy<TPolicy>()
        where TPolicy : class, IRetryPolicy
    {
        Services.Replace(ServiceDescriptor.Singleton<IRetryPolicy, TPolicy>());
        return this;
    }

    /// <summary>Configures enqueue options.</summary>
    /// <param name="configure">The configuration callback.</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder Configure(Action<OutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>Configures the background dispatcher.</summary>
    /// <param name="configure">The configuration callback.</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder ConfigureDispatcher(Action<DispatcherOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>Configures retries.</summary>
    /// <param name="configure">The configuration callback.</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder ConfigureRetry(Action<RetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>Configures retention cleanup and statistics.</summary>
    /// <param name="configure">The configuration callback.</param>
    /// <returns>This builder.</returns>
    public DbRelayBuilder ConfigureMaintenance(Action<MaintenanceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    /// Does not run the background dispatcher in this application, for example a web API that only enqueues while a separate
    /// worker service delivers. No delivery handler is required. Retention cleanup still runs unless disabled.
    /// </summary>
    /// <returns>This builder.</returns>
    public DbRelayBuilder DisableDispatcher()
    {
        DispatcherEnabled = false;
        return this;
    }

    private void EnsureNoHandler()
    {
        if (HasDeliveryHandler)
        {
            throw new InvalidOperationException(
                "A delivery handler has already been registered. DbRelay uses a single handler; route by OutboxMessage.Destination or MessageType inside it.");
        }
    }
}
