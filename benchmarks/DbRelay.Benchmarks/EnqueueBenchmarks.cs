using BenchmarkDotNet.Attributes;
using Dapper;
using DbRelay;
using Microsoft.Extensions.DependencyInjection;

namespace DbRelay.Benchmarks;

/// <summary>
/// Cost of adding outbox writes to a real business transaction (pooled connection, begin, Dapper insert, commit).
/// The baseline is the same transaction without DbRelay, so the delta is DbRelay's overhead including the extra round trip.
/// </summary>
[MemoryDiagnoser]
[MinIterationCount(10)]
[MaxIterationCount(30)]
public class EnqueueBenchmarks
{
    private ServiceProvider _services = null!;
    private IOutbox _outbox = null!;
    private string _schema = null!;
    private string _insertOrder = null!;

    [Params(Provider.PostgreSql, Provider.SqlServer)]
    public Provider Provider { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _schema = Databases.NewSchema("bench");
        _services = await Databases.CreateSchemaAsync(Provider, _schema);
        _outbox = _services.GetRequiredService<IOutbox>();
        _insertOrder = Databases.InsertOrderSql(_schema);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _services.DisposeAsync();
        await Databases.DropSchemaAsync(Provider, _schema);
    }

    [Benchmark(Baseline = true, Description = "Business insert only (no outbox)")]
    public async Task BusinessOnly()
    {
        await using var connection = await Databases.OpenAsync(Provider);
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(_insertOrder, NewOrder(), transaction);
        await transaction.CommitAsync();
    }

    [Benchmark(Description = "Business insert + 1 outbox message")]
    public async Task BusinessPlusOneMessage()
    {
        await using var connection = await Databases.OpenAsync(Provider);
        await using var transaction = await connection.BeginTransactionAsync();
        var order = NewOrder();
        await connection.ExecuteAsync(_insertOrder, order, transaction);
        await _outbox.EnqueueAsync(transaction, order);
        await transaction.CommitAsync();
    }

    [Benchmark(Description = "Business insert + 10 outbox messages (EnqueueMany)")]
    public async Task BusinessPlusTenMessages()
    {
        await using var connection = await Databases.OpenAsync(Provider);
        await using var transaction = await connection.BeginTransactionAsync();
        var order = NewOrder();
        await connection.ExecuteAsync(_insertOrder, order, transaction);
        await _outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, 10).Select(_ => order));
        await transaction.CommitAsync();
    }

    [Benchmark(Description = "Standalone enqueue (DbRelay-owned connection)")]
    public Task StandaloneEnqueue() => _outbox.EnqueueStandaloneAsync(NewOrder());

    private static BenchMessage NewOrder() => new(Guid.NewGuid(), "customer-42", 99.95m);
}
