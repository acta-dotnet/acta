using System.Data.Common;
using Acta.Configuration;
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
    ) =>
        Run(
            async token =>
            {
                await using var conn = await OpenConnectionAsync(token);
                await using var tx = BeginWriteTransaction(conn);
                await using var cmd = CreateBoundWriteCommand(conn, tx, command, bind);
                var rows = await ReadPrimaryRowsAsync(cmd, mapRow, token);
                if (tx is not null)
                {
                    await tx.CommitAsync(token);
                }

                return rows;
            },
            ct
        );

    public Task<T?> ExecuteSingleAsync<T>(StoreCommand command, Action<DbCommand> bind, Func<DbDataReader, T> mapRow, CancellationToken ct)
        where T : class =>
        Run<T?>(
            async token =>
            {
                await using var conn = await OpenConnectionAsync(token);
                await using var tx = BeginWriteTransaction(conn);
                await using var cmd = CreateBoundWriteCommand(conn, tx, command, bind);
                var rows = await ReadPrimaryRowsAsync(cmd, mapRow, token);
                if (tx is not null)
                {
                    await tx.CommitAsync(token);
                }

                return rows.Count > 0 ? rows[^1] : null;
            },
            ct
        );

    public Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct) =>
        Run<object?>(
            async token =>
            {
                await using var conn = await OpenConnectionAsync(token);
                await using var tx = BeginWriteTransaction(conn);
                await using var cmd = CreateBoundWriteCommand(conn, tx, command, bind);
                await cmd.ExecuteNonQueryAsync(token);
                if (tx is not null)
                {
                    await tx.CommitAsync(token);
                }

                return null;
            },
            ct
        );

    private DbTransaction? BeginWriteTransaction(DbConnection conn) =>
        _dialect.WrapsMutationInTransaction ? _dialect.BeginImmediateTransaction(conn) : null;

    // Binds parameters before configuring the routine: the Postgres routine command text is built
    // from the bound parameter list, so it must be populated first. Inline providers set the body up
    // front and bind afterward.
    private DbCommand CreateBoundWriteCommand(DbConnection conn, DbTransaction? tx, StoreCommand command, Action<DbCommand> bind)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _commandTimeoutSeconds;
        if (_dialect.SupportsRoutines)
        {
            bind(cmd);
            _dialect.ConfigureRoutineCommand(cmd, Schema, command.RoutineName);
        }
        else
        {
            cmd.Transaction = tx;
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
