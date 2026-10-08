namespace Acta.Runtime;

/// <summary><see cref="Execution.ArmOrConsumeSleepTimerAsync"/>: one routine call answers the timer's decision.</summary>
internal sealed class ArmOrConsumeSleepTimerHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<ArmOrConsumeSleepTimerResponse> HandleAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("ArmOrConsumeSleepTimer"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SleepDelaySeconds, command.DelaySeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SleepResumeAtUtc, command.ResumeAtUtc));
            },
            DbProjectionResolver.Resolve<ArmOrConsumeSleepTimerResponse>(),
            ct
        );
}
