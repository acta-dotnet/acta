namespace Acta;

/// <summary><see cref="ISchedules.GetAsync"/>: canonicalizes the path, then one point read in any status.</summary>
internal sealed class GetScheduleHandler(IDbSession session, ISqlDialect dialect)
{
    public ValueTask<GetScheduleResponse?> HandleAsync(GetScheduleQuery query, CancellationToken ct)
    {
        var schedule = query.Schedule;
        ArgumentNullException.ThrowIfNull(schedule);
        var jobNamespace = NameSyntax.Canonicalize(schedule.JobNamespace, nameof(schedule.JobNamespace));
        var jobName = NameSyntax.Canonicalize(schedule.JobName, nameof(schedule.JobName), NameSyntax.ExtendedMaxLength);
        var scheduleName = NameSyntax.Canonicalize(
            schedule.ScheduleName,
            nameof(schedule.ScheduleName),
            maxLength: NameSyntax.ExtendedMaxLength
        );

        return new(
            session.QuerySingleAsync(
                "GetSchedule",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNameFilter, jobName));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleName, scheduleName));
                },
                DbProjectionResolver.Resolve<GetScheduleResponse>(),
                ct
            )
        );
    }
}
