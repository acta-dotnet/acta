using System.Data.Common;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Takes a namespace's schedule lock on a gate's open transaction, the way <c>register_schedules</c> and a retire take it
/// exclusive and a slot fire's completion and the schedule verbs take it shared (docs/sql-internals.md, "Schedule
/// lock"), so the lock stays held until the gate ends. SQLite runs one writer at a time and takes no such lock.
/// </summary>
internal static class ScheduleLockGate
{
    /// <summary>
    /// Holds the lock on a connection of its own until disposed. An open transaction on SQLite would hold its one writer,
    /// so there the gate holds nothing.
    /// </summary>
    public static async Task<IAsyncDisposable> OpenAsync(IDbSession db, int namespaceId, bool exclusive, CancellationToken ct)
    {
        if (db.Provider == DbProvider.Sqlite)
        {
            return new Held(null, null);
        }

        var connection = await db.OpenConnectionAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);
        await HoldAsync(db, connection, transaction, namespaceId, exclusive, ct);
        return new Held(connection, transaction);
    }

    public static async Task HoldAsync(
        IDbSession db,
        DbConnection connection,
        DbTransaction transaction,
        int namespaceId,
        bool exclusive,
        CancellationToken ct
    )
    {
        if (db.Provider == DbProvider.Sqlite)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        if (db.Provider == DbProvider.SqlServer)
        {
            cmd.CommandText =
                "DECLARE @r INT; EXEC @r = sys.sp_getapplock @p_resource, @p_mode, 'Transaction', -1; "
                + "IF @r < 0 THROW 50000, 'The schedule lock was not granted.', 1;";
            AddParameter(cmd, "@p_resource", $"{db.Schema}.schedules.{namespaceId}");
            AddParameter(cmd, "@p_mode", exclusive ? "Exclusive" : "Shared");
        }
        else
        {
            cmd.CommandText = exclusive
                ? "SELECT pg_advisory_xact_lock(hashtext(@p_scope), @p_ns)"
                : "SELECT pg_advisory_xact_lock_shared(hashtext(@p_scope), @p_ns)";
            AddParameter(cmd, "@p_scope", $"{db.Schema}.schedules");
            AddParameter(cmd, "@p_ns", namespaceId);
        }

        await cmd.ExecuteScalarAsync(ct);
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    /// <summary>Ending the transaction uncommitted releases the lock.</summary>
    private sealed class Held(DbConnection? connection, DbTransaction? transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }
}
