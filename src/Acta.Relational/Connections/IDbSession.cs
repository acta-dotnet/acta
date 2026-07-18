using System.Data.Common;
using Acta.Configuration;
using Acta.Relational.Commands;

namespace Acta.Relational.Connections;

/// <summary>
/// Internal relational execute surface consumed by the shared stores. Owns connection open, deadlock
/// retry, routine-vs-inline dispatch, the inline write transaction, and primary-result-set selection,
/// so a shared store stays provider-free. Product behavior consumes semantic stores, never this seam.
/// </summary>
internal interface IDbSession
{
    DbProvider Provider { get; }

    string Schema { get; }

    Task<DbConnection> OpenConnectionAsync(CancellationToken ct);

    /// <summary>Runs a read command loaded by resource path with no write transaction.</summary>
    Task<T> QueryAsync<T>(
        string sqlPath,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    );

    /// <summary>
    /// Runs a routine-dispatched (routine providers) or inline (inline providers) read command with no
    /// write transaction. For the routine reads whose provider bodies are functions, not literal SELECTs.
    /// </summary>
    Task<T> QueryAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    );

    /// <summary>Runs a write command and maps every row of its primary result set.</summary>
    Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    );

    /// <summary>Runs a write command and maps the single primary-set row, or null when none.</summary>
    Task<T?> ExecuteSingleAsync<T>(StoreCommand command, Action<DbCommand> bind, Func<DbDataReader, T> mapRow, CancellationToken ct)
        where T : class;

    /// <summary>Runs a write command that returns no result set.</summary>
    Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="action"/> under the session's bounded transient-conflict retry. For raw
    /// writes composed outside the named operations (test support); named operations retry already.
    /// </summary>
    Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
}
