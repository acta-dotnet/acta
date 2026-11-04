namespace Acta;

/// <summary><see cref="ExecutionOperations.ArmOrConsumeJobSleepTimerAsync"/>: one routine call answers the timer's decision.</summary>
internal sealed class ArmOrConsumeJobSleepTimerHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<ArmOrConsumeJobSleepTimerResponse> HandleAsync(ArmOrConsumeJobSleepTimerCommand command, CancellationToken ct) =>
        ExecutionFencedException.TranslateAsync(
            session.ExecuteOneAsync(
                new StoreCommand("ArmOrConsumeJobSleepTimer"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.Fence.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, command.Fence.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.Fence.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, command.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SleepDelaySeconds, command.DelaySeconds));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SleepResumeAtUtc, command.ResumeAtUtc));
                },
                DbProjectionResolver.Resolve<ArmOrConsumeJobSleepTimerResponse>(),
                ct
            )
        );
}
