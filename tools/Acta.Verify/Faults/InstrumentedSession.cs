using System.Data.Common;

namespace Acta.Verify;

/// <summary>
/// Holds the first matching store write at the effects barrier and optionally loses its reply. With a job id, only a
/// completion batch that carries that job matches, so a system job's completion cannot take the injection.
/// </summary>
internal sealed record StoreInjection(string Operation, string Boundary, bool LoseReply = false, long? JobId = null);

internal sealed class InstrumentedSession(IDbSession inner, StoreInjection injection, EffectClient effects) : IDbSession
{
    private int _fired;
    public DbProvider Provider => inner.Provider;
    public string Schema => inner.Schema;

    public Task<DbConnection> OpenConnectionAsync(CancellationToken ct) => inner.OpenConnectionAsync(ct);

    public Task<T> QueryAsync<T>(
        string stem,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    ) => inner.QueryAsync(stem, bind, read, ct);

    public async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
    {
        var rows = await inner.ExecuteAsync(command, bind, mapRow, ct);
        if (
            command.Operation == injection.Operation
            && (injection.JobId is not { } jobId || rows.Any(r => r is CompleteExecutionsRow c && c.JobId == jobId))
            && Interlocked.CompareExchange(ref _fired, 1, 0) == 0
        )
        {
            await effects.CallAsync("barrier", "root", injection.Boundary, false, 0, ct);
            if (injection.LoseReply)
            {
                throw new TimeoutException("Instrumented committed reply loss.");
            }
        }
        return rows;
    }

    public Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
        DbTransaction transaction,
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) => inner.ExecuteInTransactionAsync(transaction, command, bind, mapRow, ct);

    public async Task<T?> ExecuteSingleAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class
    {
        var row = await inner.ExecuteSingleAsync(command, bind, mapRow, ct);
        if (command.Operation == injection.Operation && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
        {
            await effects.CallAsync("barrier", "root", injection.Boundary, false, 0, ct);
            if (injection.LoseReply)
            {
                throw new TimeoutException("Instrumented committed reply loss.");
            }
        }
        return row;
    }

    public Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct) => inner.ExecuteAsync(command, bind, ct);

    public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        inner.RunWithRetryAsync(action, ct);
}
