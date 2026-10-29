namespace Acta;

/// <summary>
/// Finds the live schedule a path names, through the definition to the schedule's own key, with the database clock a
/// verb decides against. Null when the namespace, the definition, or the live schedule is absent; each surfaces as
/// NotFound.
/// </summary>
internal sealed class ResolveScheduleTargetHandler(IDbSession session, ISqlDialect dialect, IActaClock clock)
{
    public async ValueTask<ResolveScheduleTargetResponse?> HandleAsync(ResolveScheduleTargetQuery query, CancellationToken ct)
    {
        var schedule = query.Schedule;
        ArgumentNullException.ThrowIfNull(schedule);
        var jobNamespace = NameSyntax.Canonicalize(schedule.JobNamespace, nameof(schedule.JobNamespace));
        var jobName = NameSyntax.Canonicalize(schedule.JobName, nameof(schedule.JobName));
        var scheduleName = NameSyntax.Canonicalize(
            schedule.ScheduleName,
            nameof(schedule.ScheduleName),
            maxLength: NameSyntax.ExtendedMaxLength
        );

        var row = await session.QuerySingleAsync(
            "ResolveScheduleTarget",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNameFilter, jobName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleName, scheduleName));
            },
            DbProjectionResolver.Resolve<ResolveScheduleTargetRow>(),
            ct
        );
        if (row is null)
        {
            return null;
        }

        var nowUtc = await clock.GetUtcNowAsync(ct);
        var live = new LiveSchedule(
            row.ScheduleName,
            row.Expression,
            row.TimeZoneId,
            row.MisfireStrategy,
            row.ExpressionKind,
            row.NextOccurrenceAtUtc,
            row.Status,
            row.PausedUntilUtc,
            row.Version
        );
        return new ResolveScheduleTargetResponse(
            row.ScheduleId,
            row.JobId,
            row.DefinitionId,
            row.NamespaceId,
            nowUtc,
            live,
            row.MaxOutstanding,
            row.OutstandingCount,
            row.StateVersion
        );
    }
}
