using System.Data.Common;
using System.Runtime.ExceptionServices;
using Acta.Relational.Commands;
using Acta.Relational.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta.Relational.Connections;

/// <summary>
/// Provider-backed execute surface. Owns connection open, deadlock retry, routine-vs-inline dispatch,
/// provider-owned write transactions, and primary-result-set selection so shared stores stay provider-free.
/// </summary>
internal sealed class DbSession : IDbSession
{
    private readonly string _connectionString;
    private readonly ISqlDialect _dialect;
    private readonly SqlResourceCatalog _sql;
    private readonly int _commandTimeoutSeconds;
    private readonly int _retryAttempts;
    private readonly ILogger _log;

    public DbSession(SqlProviderOptions options, ISqlDialect dialect, SqlResourceCatalog sql, ILogger<DbSession>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ConnectionString);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(sql);
        IdentifierSyntax.ValidateBareIdentifier(options.Schema, nameof(options.Schema));

        Provider = dialect.Provider;
        Schema = options.Schema;
        _connectionString = options.ConnectionString;
        _dialect = dialect;
        _sql = sql;
        _commandTimeoutSeconds = (int)Math.Ceiling(options.CommandTimeout.TotalSeconds);
        _retryAttempts = Math.Max(1, options.DeadlockRetryAttempts);
        _log = log ?? NullLogger<DbSession>.Instance;
    }

    public DbProvider Provider { get; }

    public string Schema { get; }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        // Acta-owned connections never enlist in an ambient System.Transactions scope: the driver defaults
        // (Enlist=true) would silently give an owned enqueue the transactional contract, and a second
        // connection in the scope forces distributed-transaction escalation the providers cannot honor.
        // The explicit paths (ExecuteInTransactionAsync, the staging extensions) supply their transaction
        // directly and never reach here, so only owned opens are rejected.
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException(
                "An ambient System.Transactions.TransactionScope is active, and Acta-owned connections never "
                    + "enlist in one. Rewrite to one of: pass the open transaction to the transactional IJobs "
                    + "enqueue overload for an atomic commit in the same database; stage through the provider "
                    + "outbox primitive (AddToActaOutboxAsync) for a different database; or wrap this call in a "
                    + "TransactionScope(TransactionScopeOption.Suppress) for a deliberate independent Acta commit."
            );
        }

        var connection = _dialect.CreateConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task<T> QueryAsync<T>(
        string sqlPath,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    ) =>
        Run(
            async token =>
            {
                await using var conn = await OpenConnectionAsync(token);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = _sql.Load(sqlPath);
                cmd.CommandTimeout = _commandTimeoutSeconds;
                bind(cmd);
                await using var reader = await cmd.ExecuteReaderAsync(token);
                return await read(reader, token);
            },
            ct
        );

    public Task<T> QueryAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    )
    {
        var kind = _sql.Resolve(command);
        return Run(
            async token =>
            {
                await using var conn = await OpenConnectionAsync(token);
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = _commandTimeoutSeconds;
                if (kind == StoreExecutionKind.Routine)
                {
                    bind(cmd);
                    _dialect.ConfigureRoutineCommand(cmd, Schema, command.RoutineName);
                }
                else
                {
                    cmd.CommandText = _sql.Load(command.SqlPath);
                    bind(cmd);
                }

                await using var reader = await cmd.ExecuteReaderAsync(token);
                return await read(reader, token);
            },
            ct
        );
    }

    public Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) => ExecuteThenCommitAsync(command, bind, (cmd, kind, token) => ReadPrimaryRowsAsync(cmd, mapRow, kind, token), ct);

    public async Task<T?> ExecuteSingleAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class
    {
        var rows = await ExecuteThenCommitAsync(command, bind, (cmd, kind, token) => ReadPrimaryRowsAsync(cmd, mapRow, kind, token), ct);
        return rows.Count > 0 ? rows[^1] : null;
    }

    /// <summary>
    /// Caller-transaction execute: joins the supplied transaction rather than owning one. No connection
    /// open/dispose, no BeginWriteTransaction, no commit/rollback, and no DeadlockRetry - Acta never
    /// retries inside the caller's transaction; any failure requires the caller to roll it back.
    /// </summary>
    public Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
        DbTransaction transaction,
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
    {
        var kind = _sql.Resolve(command);
        var connection = ValidateCallerTransaction(transaction);
        _dialect.PrepareCallerConnection(connection);
        // Single attempt (Acta never retries inside the caller's transaction) but through the same
        // IsCancellation funnel the owned paths use: a token-cancelled provider command (SqlClient surfaces
        // it as SqlException 3980/0, not OperationCanceledException) is translated here too.
        return UnwrapClientFailureAsync(() =>
            DeadlockRetry.RunAsync(
                async token =>
                {
                    var cmd = CreateBoundWriteCommand(connection, transaction, command, kind, bind);
                    return await UseWriteResourceAsync(cmd, c => ReadPrimaryRowsAsync(c, mapRow, kind, token));
                },
                static _ => false,
                maxAttempts: 1,
                ct,
                _dialect.IsCancellation
            )
        );
    }

    /// <summary>
    /// Structural validation only (no database-identity probe): the transaction must be attached to an
    /// open connection of this provider's concrete ADO.NET type. Fails before any command executes.
    /// </summary>
    private DbConnection ValidateCallerTransaction(DbTransaction transaction)
    {
        var connection = CallerTransaction.RequireOpenConnection(transaction);
        return !_dialect.OwnsConnection(connection)
            ? throw new ArgumentException(
                $"The supplied transaction is bound to a '{connection.GetType().Name}', which is not the {Provider} provider this "
                    + "Acta client is configured for.",
                nameof(transaction)
            )
            : connection;
    }

    public Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct) =>
        ExecuteThenCommitAsync<object?>(
            command,
            bind,
            async (cmd, _, token) =>
            {
                await cmd.ExecuteNonQueryAsync(token);
                return null;
            },
            ct
        );

    /// <summary>
    /// Retries database-aborted attempts after cleanup. SQLite keeps its transaction open until
    /// reading succeeds, then commits outside retry. Server calls commit inside the command.
    /// Binding, mapping, and resource-disposal failures are excluded from conflict classification.
    /// </summary>
    private async Task<T> ExecuteThenCommitAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbCommand, StoreExecutionKind, CancellationToken, Task<T>> execute,
        CancellationToken ct
    )
    {
        var kind = _sql.Resolve(command);
        var executed = await Run(
            async token =>
            {
                var conn = await OpenConnectionAsync(token);
                DbTransaction? tx = null;
                try
                {
                    tx = _dialect.BeginOwnedWriteTransaction(conn);
                    var cmd = CreateBoundWriteCommand(conn, tx, command, kind, bind);
                    return new ExecutedBatch<T>(conn, tx, await UseWriteResourceAsync(cmd, c => execute(c, kind, token)));
                }
                catch
                {
                    // Roll the failed attempt back here rather than leaning on a scope exit: the
                    // connection and transaction outlive this lambda on the success path, so only the
                    // throwing path may dispose them, and it must - transaction first - before a retry
                    // opens a fresh one.
                    await DisposeFailedAttemptAsync(tx, conn);
                    throw;
                }
            },
            ct
        );

        // Single attempt, but through the same IsCancellation funnel the retried region uses: a
        // token-cancelled commit must still surface as OperationCanceledException on the provider
        // (SqlClient) that reports one as a plain SqlException.
        return await DeadlockRetry.RunAsync(
            async token =>
            {
                await using (executed.Connection)
                await using (executed.Transaction)
                {
                    if (executed.Transaction is not null)
                    {
                        await executed.Transaction.CommitAsync(token);
                    }
                }

                return executed.Value;
            },
            static _ => false,
            maxAttempts: 1,
            ct,
            _dialect.IsCancellation
        );
    }

    /// <summary>
    /// Tears a failed attempt down without ever becoming the failure itself. The transaction goes first
    /// (its dispose is what rolls back) and the connection goes regardless, because a rollback on a
    /// connection the database already aborted is the likeliest thing here to throw, and losing the
    /// connection to it would leak one per failed attempt. Both teardown failures are swallowed so the
    /// attempt's own exception is what propagates: DeadlockRetry classifies whatever leaves this scope,
    /// and a dispose exception in its place would make a transient stop looking like one and abandon a
    /// retry that would have succeeded. Swallowed toward the caller is not swallowed outright: each one
    /// is logged at warning, because a connection that failed to dispose never returns to the pool and
    /// pool exhaustion is otherwise the first symptom an operator sees.
    /// </summary>
    private async ValueTask DisposeFailedAttemptAsync(DbTransaction? tx, DbConnection conn)
    {
        try
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Acta: rolling back a failed database attempt threw while disposing its transaction; teardown continues "
                    + "and the attempt's own failure is what propagates to the caller."
            );
        }
        finally
        {
            try
            {
                await conn.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Acta: disposing the connection of a failed database attempt threw; that connection may never return "
                        + "to the pool, and the attempt's own failure is what propagates to the caller."
                );
            }
        }
    }

    /// <summary>
    /// Carries the still-open connection and uncommitted transaction of a batch that executed cleanly
    /// out of the retried region, so the commit can happen outside it.
    /// </summary>
    private readonly record struct ExecutedBatch<T>(DbConnection Connection, DbTransaction? Transaction, T Value);

    /// <summary>
    /// Binds parameters before configuring the routine: the Postgres routine command text is built
    /// from the bound parameter list, so it must be populated first. Inline commands set the body up
    /// front and bind afterward.
    /// </summary>
    private DbCommand CreateBoundWriteCommand(
        DbConnection conn,
        DbTransaction? tx,
        StoreCommand command,
        StoreExecutionKind kind,
        Action<DbCommand> bind
    )
    {
        var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandTimeout = _commandTimeoutSeconds;
            // SQLite joins the session-owned transaction; server commands own their database boundary.
            // The caller-transaction path always joins the supplied transaction.
            cmd.Transaction = tx;
            if (kind == StoreExecutionKind.Routine)
            {
                bind(cmd);
                _dialect.ConfigureRoutineCommand(cmd, Schema, command.RoutineName);
            }
            else
            {
                cmd.CommandText = _sql.Load(command.SqlPath);
                bind(cmd);
            }

            return cmd;
        }
        catch (Exception ex)
        {
            try
            {
                cmd.Dispose();
            }
            catch (Exception cleanup)
            {
                _log.LogWarning(cleanup, "Acta: disposing an unbound command failed.");
            }
            throw new ClientCommandFailure(ex);
        }
    }

    /// <summary>
    /// Server commands expose one business result set. SQLite and outbox inline batches select the
    /// final result set. Drain completely before reporting success or committing a session-owned transaction.
    /// </summary>
    private async Task<IReadOnlyList<T>> ReadPrimaryRowsAsync<T>(
        DbCommand cmd,
        Func<DbDataReader, T> mapRow,
        StoreExecutionKind kind,
        CancellationToken ct
    )
    {
        var dataReader = await cmd.ExecuteReaderAsync(ct);
        return await UseWriteResourceAsync(
            dataReader,
            async reader =>
            {
                var rows = new List<T>();
                if (kind == StoreExecutionKind.Inline && _dialect.ResultSetIsLast)
                {
                    do
                    {
                        rows.Clear();
                        while (await reader.ReadAsync(ct))
                        {
                            rows.Add(MapRow(reader, mapRow));
                        }
                    } while (await reader.NextResultAsync(ct));
                }
                else
                {
                    while (await reader.ReadAsync(ct))
                    {
                        rows.Add(MapRow(reader, mapRow));
                    }

                    // A trailing statement can fail after the business rows arrived. Observe that failure
                    // before reporting success, without applying the business mapper to unrelated result shapes.
                    while (await reader.NextResultAsync(ct))
                    {
                        while (await reader.ReadAsync(ct)) { }
                    }
                }

                return rows;
            }
        );
    }

    private static T MapRow<T>(DbDataReader reader, Func<DbDataReader, T> mapRow)
    {
        try
        {
            return mapRow(reader);
        }
        catch (Exception ex)
        {
            throw new ClientCommandFailure(ex);
        }
    }

    /// <summary>
    /// A mapper/binder/teardown exception is never evidence that the database aborted the operation.
    /// Preserves its original public exception while excluding it from provider conflict classification.
    /// </summary>
    private sealed class ClientCommandFailure(Exception cause) : Exception("Client command processing failed.", cause);

    private async Task<T> UseWriteResourceAsync<TResource, T>(TResource resource, Func<TResource, Task<T>> execute)
        where TResource : IAsyncDisposable
    {
        T result;
        try
        {
            result = await execute(resource);
        }
        catch
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception cleanup)
            {
                _log.LogWarning(cleanup, "Acta: disposing a failed command or reader failed.");
            }
            throw;
        }

        try
        {
            await resource.DisposeAsync();
        }
        catch (Exception ex)
        {
            throw new ClientCommandFailure(ex);
        }
        return result;
    }

    private static async Task<T> UnwrapClientFailureAsync<T>(Func<Task<T>> execute)
    {
        try
        {
            return await execute();
        }
        catch (ClientCommandFailure ex)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException!);
            throw;
        }
    }

    public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => Run(action, ct);

    private Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        UnwrapClientFailureAsync(() =>
            DeadlockRetry.RunAsync(
                action,
                ex => ex is not ClientCommandFailure && _dialect.IsTransientConflict(ex),
                _retryAttempts,
                ct,
                _dialect.IsCancellation
            )
        );
}
