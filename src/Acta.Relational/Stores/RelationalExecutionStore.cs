using System.Data.Common;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.ChildLatches;
using Acta.Runtime.Modules.Execution.Timers;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IExecutionStore"/> over <see cref="IDbSession"/>: claim/start/complete,
/// steps, checkpoints, child latches, and timers. Provider mechanics (routine vs inline, bulk-shape
/// binding, the batched-completion capability) live behind the session and the dialect.
/// </summary>
internal sealed class RelationalExecutionStore(IDbSession session, ISqlDialect dialect) : IExecutionStore
{
    public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
        ExecuteRowAsync(
            new StoreCommand("Execution", "Checkpoints/CheckpointSlot"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SlotAction, (short)command.Action));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.KindCode, command.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.ValueFormatId, command.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Value, command.Value));
            },
            DbProjectionResolver.Resolve<CheckpointSlotRow>(),
            "checkpoint_slot returned no rows; the contract is exactly one.",
            ct
        );

    public Task RecordJobNoteAsync(long jobId, string message, JobPayload? detail, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Execution", "Notes/RecordJobNote"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, message));
                // Format id 0 with a NULL body is the "no detail" encoding ck_events_detail_pair expects.
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.DetailFormatId, detail?.Format.Id ?? (byte)0));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.Detail, detail?.Data.ToArray()));
            },
            ct
        );

    public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) =>
        session.QueryAsync<IReadOnlyList<long>>(
            "Sql/Execution/ChildLatches/GetChildJobIds.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.ParentId, parentJobId)),
            async (reader, token) =>
            {
                var ids = new List<long>();
                while (await reader.ReadAsync(token))
                {
                    ids.Add(reader.GetInt64(0));
                }

                return ids;
            },
            ct
        );

    public Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(int namespaceId, CancellationToken ct) =>
        session.QueryAsync<IReadOnlyList<StaleChildLatch>>(
            "Sql/Execution/ChildLatches/GetStaleChildLatches.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, namespaceId)),
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<StaleChildLatch>();
                var rows = new List<StaleChildLatch>();
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader));
                }

                return rows;
            },
            ct
        );

    public Task<SleepDecision> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct) =>
        ExecuteRowAsync(
            new StoreCommand("Execution", "Timers/ArmOrConsumeSleepTimer"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SleepDelaySeconds, command.DelaySeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SleepResumeAtUtc, command.ResumeAtUtc));
            },
            DbProjectionResolver.Resolve<SleepDecision>(),
            "arm_or_consume_sleep_timer returned no decision.",
            ct
        );

    public Task<ClaimResult> ClaimBatchAsync(ClaimRequest request, int leaseTtlSeconds, CancellationToken ct)
    {
        return request.MaxBatch <= 0
            ? Task.FromResult(ClaimResult.Empty)
            : MapClaimAsync(
                new StoreCommand("Execution", "ClaimBatch"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, request.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, request.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ClaimLimit, request.MaxBatch));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, leaseTtlSeconds));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StartExecuting, request.StartExecuting));
                },
                rows => ClaimResultMapper.Map(rows),
                ct
            );
    }

    public Task<ClaimResult> ClaimOneAsync(ClaimRequest request, int leaseTtlSeconds, long? jobId, CancellationToken ct)
    {
        return jobId is null
            ? ClaimBatchAsync(request with { MaxBatch = 1 }, leaseTtlSeconds, ct)
            : MapClaimAsync(
                new StoreCommand("Execution", "ClaimOne"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, request.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, request.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, leaseTtlSeconds));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId.Value));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StartExecuting, request.StartExecuting));
                },
                rows => rows.Count == 0 ? ClaimResult.Empty : new ClaimResult(rows.Select(ClaimResultMapper.ToClaimedJob).ToList(), null),
                ct
            );
    }

    private async Task<ClaimResult> MapClaimAsync(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<IReadOnlyList<ClaimReadyRow>, ClaimResult> map,
        CancellationToken ct
    ) => map(await session.ExecuteAsync(command, bind, DbProjectionResolver.Resolve<ClaimReadyRow>(), ct));

    public async Task<StartExecutionAction> StartExecutionAsync(
        long jobId,
        int workerId,
        int expectedExecutionNumber,
        int expectedVersion,
        int leaseTtlSeconds,
        CancellationToken ct
    )
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Execution", "StartExecution"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, workerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobRuntime.ExecutionNumber, expectedExecutionNumber));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobRuntime.Version, expectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, leaseTtlSeconds));
            },
            reader => reader.IsDBNull(0) ? (byte?)null : reader.GetByteFromNumeric(0),
            ct
        );

        return rows.Count > 0 && rows[^1] is { } action
            ? (StartExecutionAction)action
            : throw new InvalidOperationException("StartExecution returned no action.");
    }

    public Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionRequest request, CancellationToken ct) =>
        ExecuteRowAsync(
            new StoreCommand("Execution", "CompleteExecution"),
            cmd =>
            {
                if (request.FinalStatus is null)
                {
                    foreach (var spec in BuildNonRecurringParameters(request))
                    {
                        cmd.Parameters.Add(dialect.CreateParameter(spec));
                    }

                    // Inline-only providers reference every named parameter in the body, so the two
                    // recurring-only params must be bound inert on the non-recurring path.
                    if (!dialect.SupportsRoutines)
                    {
                        cmd.Parameters.Add(
                            dialect.CreateParameter(new DbParameterSpec("p_recurring_result_cap", request.RecurringResultCap, DbKind.Int32))
                        );
                        cmd.Parameters.Add(
                            dialect.CreateParameter(new DbParameterSpec("p_schedule_advances", "[]", DbKind.UnicodeString, Size: 8))
                        );
                    }
                }
                else
                {
                    dialect.BindRecurringCompletion(cmd, request, session.Schema);
                }
            },
            DbProjectionResolver.Resolve<CompleteExecutionResult>(),
            "complete_execution returned no action.",
            ct
        );

    public async Task<IReadOnlyList<bool>> CompleteExecutionsBatchAsync(
        IReadOnlyList<CompleteExecutionRequest> requests,
        CancellationToken ct
    )
    {
        if (!dialect.SupportsRoutines)
        {
            throw new NotSupportedException("The SQLite provider has no batched-completion routine; Bulk degrades to Direct.");
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("Execution", "CompleteExecutionsBatch"),
            cmd => dialect.BindCompleteExecutionsBatch(cmd, requests, session.Schema),
            DbProjectionResolver.Resolve<BatchOutcomeRow>(),
            ct
        );

        if (rows.Count != requests.Count)
        {
            throw new InvalidOperationException($"complete_executions_batch returned {rows.Count} outcomes for {requests.Count} requests.");
        }

        var finalized = new bool[requests.Count];
        foreach (var row in rows)
        {
            finalized[row.Ordinal] = row.Finalized;
        }

        return finalized;
    }

    public async Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Execution", "ReclaimStuckJobs"),
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, namespaceId)),
            DbProjectionResolver.Resolve<ReclaimedJobRow>(),
            ct
        );

        return ReclaimResultMapper.Map(rows);
    }

    public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
        ExecuteRowAsync(
            new StoreCommand("Execution", "StartStep"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.Name, name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AtMostOnce, atMostOnce));
            },
            DbProjectionResolver.Resolve<StartStepDecision>(),
            "start_step returned no decision.",
            ct
        );

    public Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct) =>
        ExecuteRowAsync(
            new StoreCommand("Execution", "CompleteStep"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepSucceeded, command.Succeeded));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.ResultFormatId, command.ResultFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.Result, command.Result));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.ReasonCode, command.ReasonCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.ReasonMessage, command.ReasonMessage));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepRetryDelaySeconds, command.DelaySeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepMaxAttempts, command.MaxAttempts));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepRetryWindowSeconds, command.RetryWindowSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.Version, command.ExpectedVersion));
            },
            DbProjectionResolver.Resolve<CompleteStepDecision>(),
            "complete_step returned no decision.",
            ct
        );

    private async Task<T> ExecuteRowAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        string missingMessage,
        CancellationToken ct
    )
        where T : class =>
        await session.ExecuteSingleAsync(command, bind, mapRow, ct) ?? throw new InvalidOperationException(missingMessage);

    // Scalar parameter list for the non-recurring complete_execution shape (identical across providers).
    private static List<DbParameterSpec> BuildNonRecurringParameters(CompleteExecutionRequest request)
    {
        var resultBytes = request.Result.IsEmpty ? [] : request.Result.ToArray();
        return
        [
            DbParams.For(ActaSchema.Job.Id, request.JobId),
            DbParams.For(ActaSchema.Sql.LeasedByWorkerId, request.WorkerId),
            DbParams.For(ActaSchema.JobRuntime.ExecutionNumber, request.ExpectedExecutionNumber),
            DbParams.For(ActaSchema.JobEvent.ReasonCode, request.JobEventReasonCode is { } rc ? (short)rc : null),
            DbParams.For(ActaSchema.JobEvent.ReasonMessage, request.ReasonMessage),
            DbParams.For(ActaSchema.JobResult.ResultFormatId, request.ResultFormatId),
            DbParams.For(ActaSchema.JobResult.Result, resultBytes),
            DbParams.For(ActaSchema.Sql.ExecutionSucceeded, request.Outcome == ExecutionOutcome.Succeeded),
            DbParams.For(ActaSchema.JobEvent.DurationMs, request.DurationMs is { } duration ? duration : null),
            DbParams.For(ActaSchema.Sql.RescheduleStatusCode, request.RescheduleStatusCode),
            DbParams.For(ActaSchema.Sql.RescheduleDelaySeconds, request.RescheduleDelaySeconds),
            DbParams.For(ActaSchema.Sql.RescheduleResumeAtUtc, request.RescheduleResumeAtUtc),
            DbParams.For(ActaSchema.Sql.WaitSignalName, request.WaitSignalName),
            DbParams.For(ActaSchema.Sql.HandlerStatusCode, request.HandlerStatusCode),
            DbParams.For(ActaSchema.Sql.RetentionSeconds, request.RetentionSeconds),
            DbParams.For(ActaSchema.Sql.FinalStatus, request.FinalStatus is { } fs ? (byte)fs : (byte?)null),
            DbParams.For(ActaSchema.Sql.JobNextRunAtUtc, request.JobNextRunAtUtc),
            DbParams.For(ActaSchema.Sql.FailureCount, request.FailureCount),
        ];
    }
}
