using Npgsql;

namespace DbRelay.PostgreSql.Internal;

/// <summary>
/// Holds the data source DbRelay uses and disposes it with the service provider only when DbRelay created it.
/// Caller-supplied data sources are never disposed.
/// </summary>
internal sealed class PostgreSqlDataSourceHolder : IDisposable, IAsyncDisposable
{
    public PostgreSqlDataSourceHolder(NpgsqlDataSource dataSource, bool ownsDataSource)
    {
        DataSource = dataSource;
        OwnsDataSource = ownsDataSource;
    }

    public NpgsqlDataSource DataSource { get; }

    public bool OwnsDataSource { get; }

    public void Dispose()
    {
        if (OwnsDataSource)
        {
            DataSource.Dispose();
        }
    }

    public ValueTask DisposeAsync() => OwnsDataSource ? DataSource.DisposeAsync() : ValueTask.CompletedTask;
}
