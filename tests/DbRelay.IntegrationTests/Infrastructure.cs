using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Dapper;
using DbRelay.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DbRelay.IntegrationTests;

public sealed record OrderPlaced(Guid OrderId, int Sequence, string Note);

/// <summary>A raw outbox row read with Dapper, including columns the public API does not expose (lock token).</summary>
public sealed class OutboxRow
{
    public Guid Id { get; set; }

    public short Status { get; set; }

    public int AttemptCount { get; set; }

    public Guid? LockToken { get; set; }

    public string? LockedBy { get; set; }

    public DateTime AvailableAt { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public DateTime? DeadLetteredAt { get; set; }

    public string? LastError { get; set; }

    public int? LastErrorLength { get; set; }

    public DateTime DbNow { get; set; }

    public OutboxMessageStatus State => (OutboxMessageStatus)Status;
}

/// <summary>One row of the test-only update history (see <see cref="TestSchema.InstallHistoryTriggerAsync"/>).</summary>
public sealed class HistoryRow
{
    public short Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTime AvailableAt { get; set; }

    public DateTime At { get; set; }
}

/// <summary>One isolated schema per test: the outbox table plus an <c>orders</c> business table. Dropped on dispose.</summary>
public sealed class TestSchema : IAsyncDisposable
{
    private TestSchema(PostgreSqlFixture fixture, string name)
    {
        Fixture = fixture;
        Name = name;
    }

    public PostgreSqlFixture Fixture { get; }

    public string Name { get; }

    public string Outbox => $"\"{Name}\".outbox_messages";

    public string Orders => $"\"{Name}\".orders";

    public static async Task<TestSchema> CreateAsync(PostgreSqlFixture fixture)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        var schema = new TestSchema(fixture, DbRelay.Testing.TestSchemaNames.New("it"));

        // Schema creation goes through the public IOutboxSchemaManager, exactly as an application would do it.
        await using (var producer = schema.CreateProducer())
        {
            await producer.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"CREATE TABLE {schema.Orders} (id uuid PRIMARY KEY, sequence integer NOT NULL, note text NOT NULL)");
        return schema;
    }

    /// <summary>An enqueue-only DbRelay service provider (like a web node with <c>DisableDispatcher()</c>).</summary>
    public ServiceProvider CreateProducer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbRelay(relay =>
        {
            relay.UsePostgreSql(Fixture.ConnectionStringFor("dbrelay-it-producer", 10), pg => pg.Schema = Name);
            relay.DisableDispatcher();
            relay.ConfigureMaintenance(m =>
            {
                m.CleanupEnabled = false;
                m.StatisticsInterval = null;
            });
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>A Dapper business insert plus an outbox enqueue in one caller-owned transaction, then commit.</summary>
    public async Task<Guid> EnqueueOrderAsync(IOutbox outbox, int sequence, string note = "order", CancellationToken cancellationToken = default)
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var orderId = Guid.NewGuid();
        await connection.ExecuteAsync(
            $"INSERT INTO {Orders} (id, sequence, note) VALUES (@orderId, @sequence, @note)",
            new { orderId, sequence, note },
            transaction);
        var messageId = await outbox.EnqueueAsync(transaction, new OrderPlaced(orderId, sequence, note), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messageId;
    }

    public async Task<OutboxRow?> GetRowAsync(Guid id)
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<OutboxRow>(RowSql + " WHERE id = @id", new { id });
    }

    public async Task<OutboxRow> GetRequiredRowAsync(Guid id) =>
        await GetRowAsync(id) ?? throw new InvalidOperationException($"Outbox row {id} does not exist.");

    public async Task<List<OutboxRow>> GetRowsAsync()
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync();
        return (await connection.QueryAsync<OutboxRow>(RowSql)).AsList();
    }

    public async Task<Dictionary<OutboxMessageStatus, int>> CountByStatusAsync()
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<(short Status, long Count)>($"SELECT status, count(*) FROM {Outbox} GROUP BY status");
        return rows.ToDictionary(r => (OutboxMessageStatus)r.Status, r => (int)r.Count);
    }

    public async Task<DateTime> DbNowAsync()
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<DateTime>("SELECT now()");
    }

    /// <summary>
    /// Test-only instrumentation: an AFTER UPDATE trigger that appends every row change, stamped with the statement's
    /// <c>now()</c> (the same value the DbRelay statement used), to <c>history</c>. Lives in the test schema only.
    /// </summary>
    public async Task InstallHistoryTriggerAsync()
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            $"""
            CREATE TABLE "{Name}".history (seq bigserial PRIMARY KEY, id uuid NOT NULL, status smallint NOT NULL,
                attempt_count integer NOT NULL, available_at timestamptz NOT NULL, at timestamptz NOT NULL);
            CREATE FUNCTION "{Name}".record_history() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                INSERT INTO "{Name}".history (id, status, attempt_count, available_at, at)
                VALUES (NEW.id, NEW.status, NEW.attempt_count, NEW.available_at, now());
                RETURN NEW;
            END
            $fn$;
            CREATE TRIGGER record_history AFTER UPDATE ON {Outbox} FOR EACH ROW EXECUTE FUNCTION "{Name}".record_history();
            """);
    }

    public async Task<List<HistoryRow>> GetHistoryAsync(Guid id)
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync();
        return (await connection.QueryAsync<HistoryRow>(
            $"SELECT status AS Status, attempt_count AS AttemptCount, available_at AS AvailableAt, at AS At FROM \"{Name}\".history WHERE id = @id ORDER BY seq",
            new { id })).AsList();
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{Name}\" CASCADE");
    }

    private string RowSql =>
        "SELECT id AS Id, status AS Status, attempt_count AS AttemptCount, lock_token AS LockToken, locked_by AS LockedBy, " +
        "available_at AS AvailableAt, last_attempt_at AS LastAttemptAt, delivered_at AS DeliveredAt, " +
        "dead_lettered_at AS DeadLetteredAt, last_error AS LastError, char_length(last_error) AS LastErrorLength, now() AS DbNow " +
        $"FROM {Outbox}";
}

/// <summary>Builds real generic hosts running the real DbRelay dispatcher against PostgreSQL.</summary>
public static class RelayHost
{
    public static IHost Build(
        TestSchema schema,
        string applicationName,
        Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> handler,
        Action<DispatcherOptions>? dispatcher = null,
        Action<RetryOptions>? retry = null,
        TimeSpan? shutdownTimeout = null,
        CapturingLoggerProvider? logs = null,
        int maxPoolSize = 20,
        Action<IServiceCollection>? services = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(30));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs ?? new CapturingLoggerProvider());
        builder.Services.AddDbRelay(relay =>
        {
            relay.UsePostgreSql(schema.Fixture.ConnectionStringFor(applicationName, maxPoolSize), pg => pg.Schema = schema.Name);
            relay.UseDeliveryHandler(handler);
            relay.ConfigureDispatcher(d =>
            {
                d.PollingInterval = TimeSpan.FromMilliseconds(50);
                d.LeaseDuration = TimeSpan.FromSeconds(30);
                d.DeliveryTimeout = TimeSpan.FromSeconds(10);
                d.MaxErrorBackoff = TimeSpan.FromSeconds(2);
                dispatcher?.Invoke(d);
            });
            relay.ConfigureRetry(r =>
            {
                r.InitialDelay = TimeSpan.FromMilliseconds(100);
                r.MaxDelay = TimeSpan.FromSeconds(1);
                retry?.Invoke(r);
            });
            relay.ConfigureMaintenance(m =>
            {
                m.CleanupEnabled = false;
                m.StatisticsInterval = null;
            });
        });
        services?.Invoke(builder.Services);
        return builder.Build();
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description, int pollMs = 50)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#}s waiting for: {description}");
            }

            await Task.Delay(pollMs, TestContext.Current.CancellationToken);
        }
    }

    public static Task UntilAsync(Func<bool> condition, TimeSpan timeout, string description, int pollMs = 20) =>
        UntilAsync(() => Task.FromResult(condition()), timeout, description, pollMs);
}

public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, string? Exception, string State);

/// <summary>Captures everything logged through Microsoft.Extensions.Logging: message, structured state, scopes and exceptions.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly ConcurrentQueue<string> _scopes = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries;

    public IReadOnlyCollection<string> Scopes => _scopes;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>All captured text, for "never appears in logs" assertions.</summary>
    public string AllText() =>
        string.Join("\n", _entries.Select(e => $"{e.Category}|{e.Level}|{e.EventId}|{e.Message}|{e.State}|{e.Exception}").Concat(_scopes));

    public int Count(int eventId) => _entries.Count(e => e.EventId.Id == eventId);

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            owner._scopes.Enqueue(Describe(state));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner._entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception?.ToString(), Describe(state)));
        }

        private static string Describe<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(";", pairs.Select(p => p.Key + "=" + Convert.ToString(p.Value, CultureInfo.InvariantCulture)))
                : Convert.ToString(state, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

/// <summary>Writes measurements to the test output and to <c>integration-metrics.log</c> next to the test assembly.</summary>
public static class Metrics
{
    private static readonly object Gate = new();

    public static void Report(string test, string text)
    {
        var tfm = AppContext.TargetFrameworkName ?? "unknown";
        var line = $"{DateTimeOffset.Now:O} [{tfm}] {test}: {text}";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        lock (Gate)
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "integration-metrics.log"), line + Environment.NewLine);
        }
    }
}
