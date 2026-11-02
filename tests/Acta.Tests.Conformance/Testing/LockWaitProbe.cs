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
    /// <summary>
    /// Returns once a session running the statement that contains <paramref name="sqlFragment"/> is blocked on a lock of
    /// the given kind. Fails when <paramref name="operation"/> completes first, since it then never waited, and when no
    /// such wait shows within <see cref="SpecWaits.Gate"/>.
    /// </summary>
    public static Task WaitAsync(IDbSession db, string sqlFragment, LockWait kind, Task operation, CancellationToken ct) =>
        WaitAsync(db, sqlFragment, kind, operation, blocker: null, ct);

    /// <summary>
    /// Counts only a wait on a lock <paramref name="blocker"/>'s session holds, so a statement whose text other specs'
    /// sessions also run is told apart from theirs.
    /// </summary>
    public static async Task WaitAsync(
        IDbSession db,
        string sqlFragment,
        LockWait kind,
        Task operation,
        DbTransaction? blocker,
        CancellationToken ct
    )
    {
        int? blockerSession = blocker is null ? null : await SessionIdAsync(db.Provider, blocker, ct);
        await using var connection = await db.OpenConnectionAsync(ct);
        var deadline = DateTime.UtcNow + SpecWaits.Gate;
        while (DateTime.UtcNow < deadline)
        {
            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException($"The operation running '{sqlFragment}' finished without waiting on a {kind} lock.");
            }

            if (await CountWaitersAsync(db.Provider, connection, sqlFragment, kind, blockerSession, ct) > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }

        throw new TimeoutException($"No session running '{sqlFragment}' waited on a {kind} lock within {SpecWaits.Gate.TotalSeconds:0} s.");
    }

    private static async Task<int> SessionIdAsync(DbProvider provider, DbTransaction transaction, CancellationToken ct)
    {
        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = provider == DbProvider.SqlServer ? "SELECT CAST(@@SPID AS INT)" : "SELECT pg_backend_pid()";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountWaitersAsync(
        DbProvider provider,
        DbConnection connection,
        string sqlFragment,
        LockWait kind,
        int? blockerSession,
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
                + "WHERE r.session_id <> @@SPID AND r.wait_type LIKE 'LCK%' AND r.wait_resource LIKE @p_resource AND b.event_info LIKE @p_sql"
                + (blockerSession is null ? "" : " AND r.blocking_session_id = @p_blocker");
            AddParameter(cmd, "@p_resource", kind == LockWait.ScheduleLock ? "APPLICATION%" : "KEY%");
        }
        else
        {
            // An advisory lock waits as 'advisory', a row lock on the holder's transaction id or the tuple.
            cmd.CommandText =
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() "
                + "AND wait_event_type = 'Lock' AND query LIKE @p_sql AND "
                + (kind == LockWait.ScheduleLock ? "wait_event = 'advisory'" : "wait_event IN ('transactionid', 'tuple')")
                + (blockerSession is null ? "" : " AND @p_blocker = ANY(pg_blocking_pids(pid))");
        }

        AddParameter(cmd, "@p_sql", $"%{sqlFragment}%");
        if (blockerSession is { } session)
        {
            AddParameter(cmd, "@p_blocker", session);
        }

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
