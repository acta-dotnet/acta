using System.Data;
using System.Data.Common;

namespace Acta.Relational.Connections;

/// <summary>
/// Structural transaction guard shared by the ledger caller-transaction execute path
/// (<see cref="DbSession.ExecuteInTransactionAsync"/>) and the provider outbox staging extensions: the
/// supplied transaction must be attached to an open connection. The single home of the "detached" and
/// "not open" message strings. The ledger path additionally checks provider-type ownership; a staging
/// extension is already provider-typed by its signature and needs only this guard.
/// </summary>
internal static class CallerTransaction
{
    public static DbConnection RequireOpenConnection(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection =
            transaction.Connection
            ?? throw new ArgumentException(
                "The supplied transaction is detached from its connection; it may have been committed, rolled back, or disposed.",
                nameof(transaction)
            );
        return connection.State != ConnectionState.Open
            ? throw new ArgumentException("The supplied transaction's connection is not open.", nameof(transaction))
            : connection;
    }
}
