namespace Acta.Runtime;

/// <summary><see cref="IJobs.ReprioritizeAsync"/>: changes a non-terminal job's claim priority.</summary>
internal sealed class ReprioritizeJobHandler(
    JobLookupResolution lookup,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    public async ValueTask<ReprioritizeJobResponse> HandleAsync(ReprioritizeJobCommand command, CancellationToken ct)
    {
        RequireDefined(command.Priority);
        if (await lookup.ResolveAsync(command.Job, ct) is not { } jobId)
        {
            return new ReprioritizeJobResponse(0, ControlAction.NotFound, null, null);
        }

        var input = JobControl.Operator(command.ReasonMessage, command.ActorKey, command.ExpectedVersion);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("ReprioritizeJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobRuntime.PriorityCode, command.Priority));
                JobControl.BindActor(cmd, dialect, input);
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<JobControlRow>(),
            ct
        );
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new ReprioritizeJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    private static void RequireDefined(JobPriorityCode priority)
    {
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }
    }
}
