namespace Acta.Runtime;

/// <summary>
/// <see cref="AlertLifecycle.GetAlertableEventsAsync"/>: one read of the next alertable events, and the
/// safe horizon that read stops behind: two command timeouts, so it needs no option of its own.
/// </summary>
internal sealed class GetAlertableEventsHandler(IDbSession session, ISqlDialect dialect, SqlProviderOptions options)
{
    public TimeSpan SafeHorizonLag { get; } = TimeSpan.FromSeconds(SafeHorizonLagSeconds(options.CommandTimeout));

    public Task<IReadOnlyList<AlertableEvent>> HandleAsync(GetAlertableEventsQuery query, CancellationToken ct) =>
        session.QueryListAsync(
            "GetAlertableEvents",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.NamespaceId, query.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertCursorUtc, query.CursorUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorEventId, query.CursorEventId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertHorizonUtc, query.HorizonUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertBatchSize, query.BatchSize));
            },
            DbProjectionResolver.Resolve<AlertableEvent>(),
            ct
        );

    /// <summary>The horizon lag in whole seconds for a command timeout, rounded up so a sub-second timeout never makes it zero.</summary>
    internal static int SafeHorizonLagSeconds(TimeSpan commandTimeout)
    {
        // events.id is allocated at insert, not commit, and the alertable insert sits mid-routine with
        // blocking parent-row locks after it - so a lower id can commit after a higher one was already read
        // and checkpointed, and nothing would read it again. created_at_utc is stamped inside the writing
        // transaction and never post-dates its commit: waiting for the stamp to fall behind the horizon waits
        // out every transaction that could still commit a lower id. A writing routine is one client
        // statement, so its transaction lives at most one CommandTimeout from the statement's start (past
        // that the client cancels it and it dies); one timeout covers the gap between stamp and commit, the
        // second covers PostgreSQL's now() (the transaction's start instant, which can precede the insert by
        // the same bound).
        return (int)Math.Min(int.MaxValue, Math.Ceiling(commandTimeout.TotalSeconds) * 2);
    }
}
