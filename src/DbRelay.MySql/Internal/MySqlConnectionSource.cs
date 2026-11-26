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

    private const int MaxAttempts = 3;

    private readonly Func<MySqlConnection> _factory;
    private long _deadlockRetries;

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

        return connection;
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
        RunAsync((connection, _, ct) => operation(connection, ct), inTransaction: false, cancellationToken);

    /// <summary>
    /// Runs <paramref name="operation"/> in a READ COMMITTED transaction on a new provider-owned connection and commits it. MySQL's
    /// default, REPEATABLE READ, would take gap locks on the ranges DbRelay scans, which could block producers' inserts and
    /// deadlock with them. Retried on 1213 and 1205: the transaction is rolled back (disposed uncommitted) before the retry.
    /// </summary>
    public Task<T> ExecuteInTransactionAsync<T>(
        Func<MySqlConnection, MySqlTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync((connection, transaction, ct) => operation(connection, transaction!, ct), inTransaction: true, cancellationToken);

    private async Task<T> RunAsync<T>(
        Func<MySqlConnection, MySqlTransaction?, CancellationToken, Task<T>> operation, bool inTransaction, CancellationToken cancellationToken)
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
                        return await operation(connection, null, cancellationToken).ConfigureAwait(false);
                    }

                    var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        var result = await operation(connection, transaction, cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return result;
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
        exception.Number is DeadlockErrorNumber or LockWaitTimeoutErrorNumber;
}
