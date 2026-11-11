using System.Data.Common;

namespace Acta.Verify;

/// <summary>
/// Holds the first matching store write at the effects barrier and optionally loses its reply. With a job id, only a
/// completion batch that carries that job matches, so a system job's completion cannot take the injection, and the
/// next completion that carries the job after a lost reply is recorded with the action the database answered.
/// </summary>
internal sealed record StoreInjection(string Operation, string Boundary, bool LoseReply = false, long? JobId = null);

internal sealed class InstrumentedSession(IDbSession inner, StoreInjection injection, EffectClient effects) : IDbSession
{
    private int _fired;
    private int _repeated;
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
        if (command.Operation != injection.Operation)
        {
            return rows;
        }

        var carried = injection.JobId is { } jobId ? rows.OfType<CompleteExecutionsRow>().FirstOrDefault(c => c.JobId == jobId) : null;
        if ((injection.JobId is null || carried is not null) && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
        {
            await effects.CallAsync("barrier", "root", injection.Boundary, false, 0, ct);
            if (injection.LoseReply)
            {
                throw new TimeoutException("Instrumented committed reply loss.");
            }
        }
        else if (carried is not null && injection.LoseReply && Interlocked.CompareExchange(ref _repeated, 1, 0) == 0)
        {
            // The first call after the lost reply that carries the job again: the worker repeated its completion, and
            // what the database answered is recorded under the boundary's name for the case to read.
            await effects.CallAsync("barrier", "root", $"{injection.Boundary}-repeated-{carried.Action}", false, 0, ct);
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

    public Task<T> RunInTransactionAsync<T>(Func<DbTransaction, CancellationToken, Task<T>> action, CancellationToken ct) =>
        inner.RunInTransactionAsync(action, ct);

    public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        inner.RunWithRetryAsync(action, ct);
}
