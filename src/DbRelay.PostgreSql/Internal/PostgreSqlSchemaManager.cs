using Npgsql;
using NpgsqlTypes;

namespace DbRelay.PostgreSql.Internal;

/// <summary>Creates the outbox schema objects with idempotent, non-destructive DDL.</summary>
internal sealed class PostgreSqlSchemaManager : IOutboxSchemaManager
{
    /// <summary>
    /// Transaction-scoped advisory lock key that serializes DbRelay DDL across instances. Concurrent
    /// <c>CREATE ... IF NOT EXISTS</c> statements can otherwise fail with unique violations on the system catalogs.
    /// </summary>
    internal const long DdlLockKey = 0x4462_5265_6C61_7901; // "DbRelay" + 0x01

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlSettings _settings;

    public PostgreSqlSchemaManager(NpgsqlDataSource dataSource, PostgreSqlSettings settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public string GetCreateScript() => _settings.CreateScript;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction);
                await using (lockCommand.ConfigureAwait(false))
                {
                    lockCommand.CommandTimeout = _settings.CommandTimeoutSeconds;
                    lockCommand.Parameters.Add(new NpgsqlParameter<long> { TypedValue = DdlLockKey, NpgsqlDbType = NpgsqlDbType.Bigint });
                    await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // The script contains only validated, quoted identifiers and no values.
                var ddl = new NpgsqlCommand(_settings.CreateScript, connection, transaction);
                await using (ddl.ConfigureAwait(false))
                {
                    ddl.CommandTimeout = _settings.CommandTimeoutSeconds;
                    await ddl.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
