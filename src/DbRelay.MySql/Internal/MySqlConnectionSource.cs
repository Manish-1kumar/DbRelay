using System.Data;
using MySqlConnector;

namespace DbRelay.MySql.Internal;

/// <summary>Creates, opens and disposes the connections DbRelay owns, and runs its statements and short transactions on them.</summary>
internal sealed class MySqlConnectionSource
{
    /// <summary>ER_LOCK_DEADLOCK: the server chose this statement's transaction as a deadlock victim and rolled it back.</summary>
    internal const int DeadlockErrorNumber = 1213;

    /// <summary>ER_LOCK_WAIT_TIMEOUT.</summary>
    internal const int LockWaitTimeoutErrorNumber = 1205;

    /// <summary>ER_CHECKREAD, "Record has changed since last read": MariaDB 11.8+ with innodb_snapshot_isolation (MY-11).</summary>
    internal const int RecordChangedErrorNumber = 1020;

    private const int MaxAttempts = 3;

    private readonly Func<MySqlConnection> _factory;
    private long _deadlockRetries;
    private int _readCommittedState; // 0 unknown, 1 allowed, 2 statement-based binary log (MY-05)
    private string? _database;

    public MySqlConnectionSource(Func<MySqlConnection> factory, int commandTimeoutSeconds)
    {
        _factory = factory;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    public int CommandTimeoutSeconds { get; }

    /// <summary>How many times an operation hit error 1213 or 1205 and was retried (diagnostics and tests).</summary>
    internal long DeadlockRetries => Interlocked.Read(ref _deadlockRetries);

    public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _factory()
            ?? throw new InvalidOperationException("The DbRelay MySQL connection factory returned null. It must return a new, unopened MySqlConnection.");

        if (connection.State != ConnectionState.Closed)
        {
            // Not disposed: an already-open connection is not ours to dispose.
            throw new InvalidOperationException(
                "The DbRelay MySQL connection factory returned a connection that is not closed. It must return a new, unopened MySqlConnection; DbRelay opens and disposes it.");
        }

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _database ??= connection.Database;
        return connection;
    }

    /// <summary>The current database of DbRelay's own connections, which is where an unqualified outbox table lives (MY-07).</summary>
    public async Task<string> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_database is null)
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        return _database ?? string.Empty;
    }

    public MySqlCommand CreateCommand(MySqlConnection connection, string commandText, MySqlTransaction? transaction = null)
    {
#pragma warning disable CA2100 // Command text is built only from validated, quoted identifiers; all values are parameters.
        return new MySqlCommand(commandText, connection, transaction)
        {
            CommandType = CommandType.Text,
            CommandTimeout = CommandTimeoutSeconds,
        };
#pragma warning restore CA2100
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a new provider-owned connection, as autocommitted statements. Retried on 1213 and 1205:
    /// every operation passed here is one statement, which the server has rolled back.
    /// </summary>
    public Task<T> ExecuteAsync<T>(Func<MySqlConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, inTransaction: false, cancellationToken);

    private async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = CreateCommand(connection, sql);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rolls back DbRelay's own transaction after a failure, so a pooled connection is never returned with it open, even with
    /// <c>ConnectionReset=false</c>. A broken connection cannot be rolled back, but the server ends its transaction when it disconnects.
    /// </summary>
    private async Task TryRollbackAsync(MySqlConnection connection)
    {
        try
        {
            await ExecuteAsync(connection, "ROLLBACK", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MySqlException or InvalidOperationException)
        {
            // The connection is broken; it is disposed next and not reused.
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> in a READ COMMITTED transaction on a new provider-owned connection and commits it. (When the
    /// binary log is statement-based, the session default instead; see <see cref="ReadCommittedAllowedAsync"/>.) MySQL's
    /// default, REPEATABLE READ, would take gap locks on the ranges DbRelay scans, which could block producers' inserts and
    /// deadlock with them. Retried on 1213, 1205 and 1020: the transaction is rolled back before the retry.
    /// </summary>
    /// <remarks>
    /// MY-04: the transaction is begun with SQL, not with MySqlConnector's <c>BeginTransaction</c>, which sets the isolation level for the
    /// whole session (<c>SET SESSION TRANSACTION ...</c>) and so would leave a pooled session at READ COMMITTED for the application.
    /// <c>SET TRANSACTION</c> applies to the next transaction only. The commands therefore carry no <see cref="MySqlTransaction"/>.
    /// </remarks>
    public Task<T> ExecuteInTransactionAsync<T>(
        Func<MySqlConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, inTransaction: true, cancellationToken);

    private async Task<T> RunAsync<T>(
        Func<MySqlConnection, CancellationToken, Task<T>> operation, bool inTransaction, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                try
                {
                    if (!inTransaction)
                    {
                        return await operation(connection, cancellationToken).ConfigureAwait(false);
                    }

                    var begin = await ReadCommittedAllowedAsync(connection, cancellationToken).ConfigureAwait(false)
                        ? "SET TRANSACTION ISOLATION LEVEL READ COMMITTED; START TRANSACTION"
                        : "START TRANSACTION";
                    await ExecuteAsync(connection, begin, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var result = await operation(connection, cancellationToken).ConfigureAwait(false);
                        await ExecuteAsync(connection, "COMMIT", cancellationToken).ConfigureAwait(false);
                        return result;
                    }
                    catch
                    {
                        await TryRollbackAsync(connection).ConfigureAwait(false);
                        throw;
                    }
                }
                catch (MySqlException ex) when (IsRetriable(ex) && attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _deadlockRetries);
                }
                catch (MySqlException ex) when (cancellationToken.IsCancellationRequested)
                {
                    // Callers such as the dispatcher treat only OperationCanceledException as a normal shutdown.
                    throw new OperationCanceledException("The MySQL operation was cancelled.", ex, cancellationToken);
                }
            }

#pragma warning disable CA5394 // Jitter only; not security sensitive.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 50) * attempt), cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5394
        }
    }

    private static bool IsRetriable(MySqlException exception) =>
        exception.Number is DeadlockErrorNumber or LockWaitTimeoutErrorNumber or RecordChangedErrorNumber;

    /// <summary>
    /// False when the binary log is on and statement-based (MY-05): InnoDB then rejects writes under READ COMMITTED (error 1665), so
    /// DbRelay keeps the session default and accepts its gap locks. Read once per connection source.
    /// </summary>
    private async Task<bool> ReadCommittedAllowedAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var state = Volatile.Read(ref _readCommittedState);
        if (state == 0)
        {
            var command = CreateCommand(connection, MySqlOutboxSql.BinlogSettings);
            await using (command.ConfigureAwait(false))
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    var logBin = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
                    var format = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var statementBased = logBin is "1" or "ON" && format.Equals("STATEMENT", StringComparison.OrdinalIgnoreCase);
                    state = statementBased ? 2 : 1;
                    Volatile.Write(ref _readCommittedState, state);
                }
            }
        }

        return state == 1;
    }
}
