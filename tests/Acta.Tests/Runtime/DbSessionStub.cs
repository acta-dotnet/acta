using System.Data.Common;

namespace Acta.Tests.Runtime;

/// <summary>
/// An <see cref="IDbSession"/> whose every call throws <see cref="NotSupportedException"/>, so reaching the
/// database is visible. A test overrides only the calls its handler is meant to make.
/// </summary>
internal class DbSessionStub : IDbSession
{
    public virtual DbProvider Provider => DbProvider.Sqlite;

    public virtual string Schema => "main";

    public virtual Task<DbConnection> OpenConnectionAsync(CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<T> QueryAsync<T>(
        string stem,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public virtual Task<T> QueryAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
        DbTransaction transaction,
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public virtual Task<T?> ExecuteSingleAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class => throw new NotSupportedException();

    public virtual Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct) =>
        throw new NotSupportedException();

    public virtual Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        throw new NotSupportedException();

    public virtual Task<T> RunInOwnedTransactionAsync<T>(
        Func<DbTransaction, CancellationToken, Task<(T Result, bool Commit)>> work,
        CancellationToken ct
    ) => throw new NotSupportedException();
}
