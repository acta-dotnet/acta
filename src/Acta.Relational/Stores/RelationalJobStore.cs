using System.Data.Common;
using System.Globalization;
using Acta.Features.Jobs;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IJobStore"/> over <see cref="IDbSession"/>. One implementation for
/// every SQL provider: reads and control verbs are written once, and provider differences live behind
/// the session (routine vs inline, result-set selection) and the dialect (parameter creation, bulk binds).
/// </summary>
internal sealed class RelationalJobStore(IDbSession session, ISqlDialect dialect) : IJobStore
{
    public async ValueTask<JobSnapshot?> GetJobAsync(long jobId, CancellationToken ct) =>
        await session.QueryAsync<JobSnapshot?>(
            "Features/Jobs/Sql/GetJob.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId))),
            async (reader, token) =>
                await reader.ReadAsync(token) ? DbProjectionResolver.Resolve<JobSnapshotRow>()(reader).ToSnapshot() : null,
            ct
        );

    public async ValueTask<JobStatusCode?> GetJobStatusAsync(long jobId, CancellationToken ct) =>
        await session.QueryAsync<JobStatusCode?>(
            "Features/Jobs/Sql/GetJobStatus.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId))),
            async (reader, token) => await reader.ReadAsync(token) ? (JobStatusCode)reader.GetByteFromNumeric(0) : (JobStatusCode?)null,
            ct
        );

    public Task<JobInputRecord?> GetJobInputAsync(long jobId, CancellationToken ct) =>
        session.QueryAsync<JobInputRecord?>(
            "Features/Jobs/Sql/GetJobInput.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId))),
            async (reader, token) => await reader.ReadAsync(token) ? DbProjectionResolver.Resolve<JobInputRow>()(reader).ToRecord() : null,
            ct
        );

    public Task<JobResultRecord?> GetJobResultAsync(long jobId, int? executionNumber, CancellationToken ct) =>
        session.QueryAsync<JobResultRecord?>(
            "Features/Jobs/Sql/GetJobResult.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobResult.JobId, jobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobResult.ExecutionNumber, executionNumber)));
            },
            async (reader, token) => await reader.ReadAsync(token) ? DbProjectionResolver.Resolve<JobResultRow>()(reader).ToRecord() : null,
            ct
        );

    public async Task<IReadOnlyList<JobCheckpointItem>> GetJobCheckpointsAsync(long jobId, CancellationToken ct) =>
        await session.QueryAsync<IReadOnlyList<JobCheckpointItem>>(
            "Features/Jobs/Sql/GetJobCheckpoints.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobCheckpoint.JobId, jobId))),
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<JobCheckpointReadRow>();
                var rows = new List<JobCheckpointItem>();
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToItem());
                }

                return rows;
            },
            ct
        );

    public async ValueTask<JobExplainData?> GetJobExplanationAsync(long jobId, CancellationToken ct) =>
        await session.QueryAsync<JobExplainData?>(
            "Features/Jobs/Sql/GetJobExplanation.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId))),
            async (reader, token) =>
            {
                var readHeader = DbProjectionResolver.Resolve<ExplainHeaderRow>();
                var readStep = DbProjectionResolver.Resolve<ExplainStepRow>();
                var readCheckpoint = DbProjectionResolver.Resolve<ExplainCheckpointRow>();

                ExplainHeaderRow? header = null;
                while (await reader.ReadAsync(token))
                {
                    header = readHeader(reader);
                }

                var steps = new List<ExplainStepRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        steps.Add(readStep(reader));
                    }
                }

                var checkpoints = new List<ExplainCheckpointRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        checkpoints.Add(readCheckpoint(reader));
                    }
                }

                return header is null ? null : new JobExplainData(header, steps, checkpoints);
            },
            ct
        );

    public async ValueTask<JobLineageData?> GetJobLineageMapAsync(long jobId, int childFetchLimit, CancellationToken ct) =>
        await session.QueryAsync<JobLineageData?>(
            "Features/Jobs/Sql/GetJobLineageMap.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.ChildFetchLimit, childFetchLimit)));
            },
            async (reader, token) =>
            {
                var readJob = DbProjectionResolver.Resolve<LineageJobRow>();
                var readStep = DbProjectionResolver.Resolve<LineageStepRow>();
                var readCheckpoint = DbProjectionResolver.Resolve<ExplainCheckpointRow>();
                var readChild = DbProjectionResolver.Resolve<LineageChildRow>();

                LineageJobRow? focus = null;
                while (await reader.ReadAsync(token))
                {
                    focus = readJob(reader);
                }

                var ancestors = new List<LineageJobRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        ancestors.Add(readJob(reader));
                    }
                }

                var steps = new List<LineageStepRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        steps.Add(readStep(reader));
                    }
                }

                var checkpoints = new List<ExplainCheckpointRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        checkpoints.Add(readCheckpoint(reader));
                    }
                }

                var children = new List<LineageChildRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        children.Add(readChild(reader));
                    }
                }

                return focus is null ? null : new JobLineageData(focus, ancestors, steps, checkpoints, children);
            },
            ct
        );

    public Task<JobPage> ListJobsAsync(JobPageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Features/Jobs/Sql/ListJobs.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.NamespaceFilter, request.JobNamespace)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobRuntime.StatusCode, request.Status)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.JobNameFilter, request.JobName)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.ParentIdFilter, request.ParentJobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.TenantId, request.TenantId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.CorrelationKeyFilter, request.CorrelationKey)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.CursorCreatedAtUtc, request.CursorCreatedAtUtc)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.CursorId, request.CursorId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.PageTake, request.Take)));
                cmd.Parameters.Add(
                    dialect.CreateParameter(DbParams.For(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null))
                );
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<JobListProjectionRow>();
                var rows = new List<JobListItem>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToListRow().ToItem());
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new JobPage(rows, total);
            },
            ct
        );

    public async ValueTask<long?> ResolveJobIdByRefAsync(Guid jobRef, CancellationToken ct) =>
        await session.QueryAsync<long?>(
            "Features/Jobs/Sql/ResolveJobIdByRef.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.JobRef, jobRef))),
            async (reader, token) => await reader.ReadAsync(token) ? (reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0)) : null,
            ct
        );

    public async ValueTask<long?> ResolveJobIdByDeduplicationKeyAsync(string jobNamespace, string deduplicationKey, CancellationToken ct) =>
        await session.QueryAsync<long?>(
            "Features/Jobs/Sql/ResolveJobIdByDeduplicationKey.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.NamespaceName, jobNamespace)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.DeduplicationKey, deduplicationKey)));
            },
            async (reader, token) => await reader.ReadAsync(token) ? reader.GetInt64(0) : (long?)null,
            ct
        );

    public Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueOneAsync(JobEnqueueRow row, Guid jobRef, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Jobs", "EnqueueOne"),
            cmd => dialect.BindEnqueueOne(cmd, row, jobRef, session.Schema),
            DbProjectionResolver.Resolve<EnqueueOutcomeRow>(),
            ct
        );

    public Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueBatchAsync(
        IReadOnlyList<JobEnqueueRow> rows,
        IReadOnlyList<Guid> jobRefs,
        CancellationToken ct
    ) =>
        session.ExecuteAsync(
            new StoreCommand("Jobs", "EnqueueBatch"),
            cmd => dialect.BindEnqueueBatch(cmd, rows, jobRefs, session.Schema),
            DbProjectionResolver.Resolve<EnqueueOutcomeRow>(),
            ct
        );

    public async Task<CancelJobOutcome> CancelJobAsync(long jobId, JobControlInput input, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Jobs", "CancelJob"),
            cmd => AddControlParameters(cmd, jobId, input, includeReasonMessage: true),
            DbProjectionResolver.Resolve<CancelJobOutcomeRow>(),
            ct
        );

        return rows.Count > 0
            ? rows[^1].ToOutcome()
            : throw new InvalidOperationException(
                "Control command 'CancelJob' returned no rows; it must return exactly one (action, status_code, parent_id) row."
            );
    }

    public Task<JobControlOutcome> PauseJobAsync(long jobId, JobControlInput input, CancellationToken ct) =>
        ControlAsync("PauseJob", cmd => AddControlParameters(cmd, jobId, input, includeReasonMessage: true), ct);

    public Task<JobControlOutcome> ResumeJobAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct) =>
        ControlAsync(
            "ResumeJob",
            cmd =>
            {
                AddControlParameters(cmd, jobId, input, includeReasonMessage: true);
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobRuntime.NextRunAtUtc, nextRunAtUtc)));
            },
            ct
        );

    public Task<JobControlOutcome> RestartJobAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct) =>
        ControlAsync(
            "RestartJob",
            cmd =>
            {
                AddControlParameters(cmd, jobId, input, includeReasonMessage: true);
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobRuntime.NextRunAtUtc, nextRunAtUtc)));
            },
            ct
        );

    public Task<JobControlOutcome> RescheduleJobAsync(long jobId, DateTime nextRunAtUtc, JobControlInput input, CancellationToken ct) =>
        ControlAsync(
            "RescheduleJob",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobRuntime.NextRunAtUtc, nextRunAtUtc)));
                AddActorParameters(cmd, input, includeReasonMessage: true);
            },
            ct
        );

    public Task<JobControlOutcome> ReprioritizeJobAsync(
        long jobId,
        JobPriorityCode priority,
        JobControlInput input,
        CancellationToken ct
    ) =>
        ControlAsync(
            "ReprioritizeJob",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobRuntime.PriorityCode, priority)));
                AddActorParameters(cmd, input, includeReasonMessage: true);
            },
            ct
        );

    public Task<JobControlOutcome> UpdateJobInputAsync(long jobId, JobPayload input, JobControlInput controlInput, CancellationToken ct) =>
        ControlAsync(
            "UpdateJobInput",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.InputFormatId, input.Format.Id)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Input, input.IsNone ? null : input.Data.ToArray())));
                AddActorParameters(cmd, controlInput, includeReasonMessage: true);
            },
            ct
        );

    public Task<JobControlOutcome> PurgeJobAsync(long jobId, JobControlInput input, CancellationToken ct) =>
        ControlAsync("PurgeJob", cmd => AddControlParameters(cmd, jobId, input, includeReasonMessage: false), ct);

    public Task ResetJobStateAsync(long jobId, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Jobs", "ResetJobState"),
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId))),
            ct
        );

    private async Task<JobControlOutcome> ControlAsync(string operation, Action<DbCommand> bind, CancellationToken ct) =>
        await session.ExecuteSingleAsync(new StoreCommand("Jobs", operation), bind, DbProjectionResolver.Resolve<JobControlOutcome>(), ct)
        ?? throw new InvalidOperationException(
            $"Control command '{operation}' returned no rows; it must return exactly one (action, status_code) row."
        );

    private void AddControlParameters(DbCommand cmd, long jobId, JobControlInput input, bool includeReasonMessage)
    {
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.Id, jobId)));
        AddActorParameters(cmd, input, includeReasonMessage);
    }

    private void AddActorParameters(DbCommand cmd, JobControlInput input, bool includeReasonMessage)
    {
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ActorCode, input.Actor.ActorCode)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ActorKey, input.Actor.ActorKey)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ReasonCode, input.ReasonCode)));
        if (includeReasonMessage)
        {
            cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ReasonMessage, input.ReasonMessage)));
        }
    }
}
