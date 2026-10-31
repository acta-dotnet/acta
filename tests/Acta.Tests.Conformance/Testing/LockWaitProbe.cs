using System.Data.Common;

namespace Acta.Tests.Conformance.Testing;

/// <summary>The lock a session is blocked on: a namespace's schedule lock, or a row.</summary>
internal enum LockWait
{
    ScheduleLock,
    Row,
}

/// <summary>
/// Orders a concurrency spec's transactions on a wait the server itself reports, read from its session views, rather
/// than on elapsed time, which a slow connection open or a cold first call can pass for a wait. Server providers only.
/// </summary>
internal static class LockWaitProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Returns once a session running the statement that contains <paramref name="sqlFragment"/> is blocked on a lock of
    /// the given kind. Fails when <paramref name="operation"/> completes first, since it then never waited, and when no
    /// such wait shows within the timeout.
    /// </summary>
    public static async Task WaitAsync(IDbSession db, string sqlFragment, LockWait kind, Task operation, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException($"The operation running '{sqlFragment}' finished without waiting on a {kind} lock.");
            }

            if (await CountWaitersAsync(db.Provider, connection, sqlFragment, kind, ct) > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }

        throw new TimeoutException($"No session running '{sqlFragment}' waited on a {kind} lock within {Timeout.TotalSeconds:0} s.");
    }

    private static async Task<long> CountWaitersAsync(
        DbProvider provider,
        DbConnection connection,
        string sqlFragment,
        LockWait kind,
        CancellationToken ct
    )
    {
        await using var cmd = connection.CreateCommand();
        if (provider == DbProvider.SqlServer)
        {
            // sp_getapplock waits on an APPLICATION resource, a row lock on a KEY; a waiting request's own text is then
            // the lock procedure, so the batch is read from the session's input buffer.
            cmd.CommandText =
                "SELECT COUNT_BIG(*) FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_input_buffer(r.session_id, r.request_id) b "
                + "WHERE r.session_id <> @@SPID AND r.wait_type LIKE 'LCK%' AND r.wait_resource LIKE @p_resource AND b.event_info LIKE @p_sql";
            AddParameter(cmd, "@p_resource", kind == LockWait.ScheduleLock ? "APPLICATION%" : "KEY%");
        }
        else
        {
            // An advisory lock waits as 'advisory', a row lock on the holder's transaction id or the tuple.
            cmd.CommandText =
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() "
                + "AND wait_event_type = 'Lock' AND query LIKE @p_sql AND "
                + (kind == LockWait.ScheduleLock ? "wait_event = 'advisory'" : "wait_event IN ('transactionid', 'tuple')");
        }

        AddParameter(cmd, "@p_sql", $"%{sqlFragment}%");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }
}
