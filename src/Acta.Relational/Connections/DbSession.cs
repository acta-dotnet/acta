using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Acta.Relational.Commands;
using Acta.Relational.Resources;

namespace Acta.Relational.Connections;

/// <summary>
/// Provider-backed execute surface. Owns connection open, deadlock retry, routine-vs-inline dispatch,
/// the inline write transaction, and primary-result-set selection so shared stores stay provider-free.
/// </summary>
internal sealed class DbSession : IDbSession
{
    private readonly string _connectionString;
    private readonly ISqlDialect _dialect;
    private readonly SqlResourceCatalog _sql;
    private readonly int _commandTimeoutSeconds;
    private readonly int _retryAttempts;

    public DbSession(SqlProviderOptions options, ISqlDialect dialect, SqlResourceCatalog sql)
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
    ) =>
        Run(
            async token =>
            {
                await using var conn = await OpenConnectionAsync(token);
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = _commandTimeoutSeconds;
                if (_dialect.SupportsRoutines)
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

    public Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) => ExecuteThenCommitAsync(command, bind, (cmd, token) => ReadPrimaryRowsAsync(cmd, mapRow, token), ct);

    public async Task<T?> ExecuteSingleAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class
    {
        var rows = await ExecuteThenCommitAsync(command, bind, (cmd, token) => ReadPrimaryRowsAsync(cmd, mapRow, token), ct);
        return rows.Count > 0 ? rows[^1] : null;
    }

    // Caller-transaction execute: joins the supplied transaction rather than owning one. No connection
    // open/dispose, no BeginWriteTransaction, no commit/rollback, and no DeadlockRetry - Acta never
    // retries inside the caller's transaction; any failure requires the caller to roll it back.
    public Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
        DbTransaction transaction,
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
    {
        var connection = ValidateCallerTransaction(transaction);
        _dialect.PrepareCallerConnection(connection);
        // Single attempt (Acta never retries inside the caller's transaction) but through the same
        // IsCancellation funnel the owned paths use: a token-cancelled provider command (SqlClient surfaces
        // it as SqlException 3980/0, not OperationCanceledException) is translated here too.
        return DeadlockRetry.RunAsync(
            async token =>
            {
                await using var cmd = CreateBoundWriteCommand(connection, transaction, command, bind);
                return await ReadPrimaryRowsAsync(cmd, mapRow, token);
            },
            static _ => false,
            maxAttempts: 1,
            ct,
            _dialect.IsCancellation
        );
    }

    // Structural validation only (no database-identity probe): the transaction must be attached to an
    // open connection of this provider's concrete ADO.NET type. Fails before any command executes.
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
            async (cmd, token) =>
            {
                await cmd.ExecuteNonQueryAsync(token);
                return null;
            },
            ct
        );

    // The one owned-write shape, split at the point where the batch has fully executed and is ready to
    // commit. Everything before that point - open, begin, bind, execute - runs inside DeadlockRetry: a
    // transient there leaves nothing committed, and the attempt's transaction is rolled back by its
    // dispose before the next attempt re-opens. The commit and teardown run OUTSIDE the retry, because
    // a transient raised once the batch has landed is not safely replayable: re-running the whole batch
    // against rows it already changed reads as a lost CAS. Such a failure surfaces as the error it is,
    // for the executor's retryable-abort path and the caller's own failure budget to recover.
    private async Task<T> ExecuteThenCommitAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbCommand, CancellationToken, Task<T>> execute,
        CancellationToken ct
    )
    {
        var executed = await Run(
            async token =>
            {
                var conn = await OpenConnectionAsync(token);
                DbTransaction? tx = null;
                try
                {
                    tx = BeginWriteTransaction(conn);
                    await using var cmd = CreateBoundWriteCommand(conn, tx, command, bind);
                    return new ExecutedBatch<T>(conn, tx, await execute(cmd, token));
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

    // Tears a failed attempt down without ever becoming the failure itself. The transaction goes first
    // (its dispose is what rolls back) and the connection goes regardless, because a rollback on a
    // connection the database already aborted is the likeliest thing here to throw, and losing the
    // connection to it would leak one per failed attempt. Both teardown failures are swallowed so the
    // attempt's own exception is what propagates: DeadlockRetry classifies whatever leaves this scope,
    // and a dispose exception in its place would make a transient stop looking like one and abandon a
    // retry that would have succeeded.
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Both catches tear down a failed attempt and must never become the failure themselves. A rollback on a "
            + "connection the database already aborted is the likeliest thing here to throw, and letting it propagate "
            + "would replace the attempt's own exception - DeadlockRetry classifies whatever leaves this scope, so a "
            + "dispose error in a transient's place would abandon a retry that would have succeeded. Deliberately "
            + "silent: the caller rethrows the original failure, which is the report."
    )]
    private static async ValueTask DisposeFailedAttemptAsync(DbTransaction? tx, DbConnection conn)
    {
        try
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }
        catch (Exception)
        {
            // Deliberately ignored: the caller rethrows the original failure.
        }
        finally
        {
            try
            {
                await conn.DisposeAsync();
            }
            catch (Exception)
            {
                // Deliberately ignored: the caller rethrows the original failure.
            }
        }
    }

    // Carries the still-open connection and uncommitted transaction of a batch that executed cleanly
    // out of the retried region, so the commit can happen outside it.
    private readonly record struct ExecutedBatch<T>(DbConnection Connection, DbTransaction? Transaction, T Value);

    private DbTransaction? BeginWriteTransaction(DbConnection conn) =>
        _dialect.WrapsMutationInTransaction ? _dialect.BeginImmediateTransaction(conn) : null;

    // Binds parameters before configuring the routine: the Postgres routine command text is built
    // from the bound parameter list, so it must be populated first. Inline providers set the body up
    // front and bind afterward.
    private DbCommand CreateBoundWriteCommand(DbConnection conn, DbTransaction? tx, StoreCommand command, Action<DbCommand> bind)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _commandTimeoutSeconds;
        // Join whatever transaction the caller passes. On the owned path this is the inline write
        // transaction (SQLite) or null (routine providers, already single-CALL atomic); on the
        // caller-transaction path it is the supplied transaction for every provider.
        cmd.Transaction = tx;
        if (_dialect.SupportsRoutines)
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

    // Routine providers return one result set; inline providers put the outcome in the LAST set
    // (leading statements are guards/writes), so advance and keep the final result set.
    private async Task<IReadOnlyList<T>> ReadPrimaryRowsAsync<T>(DbCommand cmd, Func<DbDataReader, T> mapRow, CancellationToken ct)
    {
        var rows = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (_dialect.ResultSetIsLast)
        {
            do
            {
                rows.Clear();
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(mapRow(reader));
                }
            } while (await reader.NextResultAsync(ct));
        }
        else
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(mapRow(reader));
            }
        }

        return rows;
    }

    public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => Run(action, ct);

    private Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        DeadlockRetry.RunAsync(action, _dialect.IsTransientConflict, _retryAttempts, ct, _dialect.IsCancellation);
}
