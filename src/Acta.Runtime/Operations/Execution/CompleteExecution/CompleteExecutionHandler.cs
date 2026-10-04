namespace Acta.Runtime;

/// <summary>
/// <see cref="Execution.CompleteExecutionAsync"/>: one routine call per attempt. A non-recurring completion binds
/// the scalar shape every provider shares; a recurring fire binds the provider's schedule-advance shape.
/// </summary>
internal sealed class CompleteExecutionHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<CompleteExecutionResult> HandleAsync(CompleteExecutionCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("CompleteExecution"),
            cmd =>
            {
                if (command.FinalStatus is null)
                {
                    foreach (var spec in NonRecurringParameters(command))
                    {
                        cmd.Parameters.Add(dialect.CreateParameter(spec));
                    }

                    dialect.BindNonRecurringCompletionDefaults(cmd, command);
                }
                else
                {
                    dialect.BindRecurringCompletion(cmd, command, session.Schema);
                }
            },
            DbProjectionResolver.Resolve<CompleteExecutionResult>(),
            ct
        );

    private static List<DbParameterSpec> NonRecurringParameters(CompleteExecutionCommand command)
    {
        var resultBytes = command.Result.IsEmpty ? [] : command.Result.ToArray();
        return
        [
            DbParams.For(ActaSchema.Job.Id, command.JobId),
            DbParams.For(ActaSchema.Sql.LeasedByWorkerId, command.WorkerId),
            DbParams.For(ActaSchema.Runtime.ExecutionNumber, command.ExpectedExecutionNumber),
            DbParams.For(ActaSchema.Event.ReasonCode, command.EventReasonCode is { } rc ? (short)rc : null),
            DbParams.For(ActaSchema.Event.ReasonMessage, command.ReasonMessage),
            DbParams.For(ActaSchema.Result.ResultFormatId, command.ResultFormatId),
            DbParams.For(ActaSchema.Result.Result, resultBytes),
            DbParams.For(ActaSchema.Sql.ExecutionSucceeded, command.Outcome == ExecutionOutcome.Succeeded),
            DbParams.For(ActaSchema.Event.DurationMs, command.DurationMs is { } duration ? duration : null),
            DbParams.For(ActaSchema.Sql.RescheduleStatusCode, command.RescheduleStatusCode),
            DbParams.For(ActaSchema.Sql.RescheduleDelaySeconds, command.RescheduleDelaySeconds),
            DbParams.For(ActaSchema.Sql.RescheduleResumeAtUtc, command.RescheduleResumeAtUtc),
            DbParams.For(ActaSchema.Sql.WaitSignalName, command.WaitSignalName),
            DbParams.For(ActaSchema.Sql.HandlerStatusCode, command.HandlerStatusCode),
            DbParams.For(ActaSchema.Sql.RetentionSeconds, command.RetentionSeconds),
            DbParams.For(ActaSchema.Sql.FinalStatus, command.FinalStatus is { } fs ? (byte)fs : (byte?)null),
            DbParams.For(ActaSchema.Sql.JobNextRunAtUtc, command.JobNextRunAtUtc),
            DbParams.For(ActaSchema.Sql.FailureCount, command.FailureCount),
        ];
    }
}
