using System.Data.Common;
using System.Globalization;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schema;

/// <summary>
/// Conformance for the data-model-hardening re-cut: the seeded <c>sys</c> namespace, every new CHECK
/// constraint, and the denormalized namespace/definition columns that must always agree with
/// the owning <c>jobs</c> row. Constraint proofs assert the provider's <see cref="DbException"/>
/// subtype (never message text), and each violating statement is paired with a compliant control so
/// the fact proves the constraint fired, not that the statement shape itself was invalid.
/// </summary>
[ConformanceSpec(
    "schema.hardening-facts",
    "Hardened schema enforces its checks, seed, and denormalized invariants",
    Area = "Schema",
    Contract = "The hardened M001 schema enforces every new CHECK, admits one unresolved alert per deduplication key, seeds sys, and keeps runtimes in step with jobs.",
    Arrange = "A live provider schema carries the seeded sys namespace and jobs enqueued through EnqueueOne, EnqueueBatch, a child enqueue, and Restart.",
    Act = "Constraint-violating writes are attempted directly, a second unresolved alert is inserted on a key that already has one, and denormalized rows are read back.",
    Assert = "Every violating statement fails with a provider exception, a deduplication key admits a new row only once its incident is resolved, and denormalized rows agree."
)]
public abstract class SchemaHardeningSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "The seeded sys namespace (id 1, name sys) exists on a fresh install")]
    public async Task Sys_namespace_seed_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;

        var sys = await Db.From<JobNamespace>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);

        Assert.NotNull(sys);
        Assert.Equal("sys", sys!.Name);
    }

    [Fact(DisplayName = "ck_definitions_max_attempts rejects an UPDATE to zero while a positive value updates cleanly")]
    public async Task Definitions_check_family_rejects_max_attempts_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeder = new ActaTestSeeder(Db);
        var nsId = await seeder.SeedJobNamespaceAsync(TestKey("defs-ck"), ct: ct);
        var defId = await seeder.SeedJobDefinitionAsync(nsId, TestKey("defs-ck-def"), ct: ct);

        Assert.Equal(
            1,
            await Db.From<JobDefinition>().Where(d => d.Id == defId).UpdateOnlyAsync(() => new JobDefinition { MaxAttempts = 5 }, ct)
        );

        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.From<JobDefinition>().Where(d => d.Id == defId).UpdateOnlyAsync(() => new JobDefinition { MaxAttempts = 0 }, ct)
        );
    }

    [Fact(DisplayName = "ck_alerts_job_ref_pair and ck_alerts_occurrence_count each reject their violating INSERT")]
    public async Task Alerts_checks_reject_job_ref_pair_and_zero_occurrence_count()
    {
        var ct = TestContext.Current.CancellationToken;

        // Positive control: the same shape with no violation inserts cleanly.
        Assert.True(await Db.From<JobAlert>().InsertAsync<long>(NewAlertRow(), ct) > 0);

        // ck_alerts_job_ref_pair: job_id set without job_ref (the shape raise_job_alert produces for an
        // unknown job_id, load-bearing on the alert write path since the data-model-hardening re-cut).
        await Assert.ThrowsAnyAsync<DbException>(() => Db.From<JobAlert>().InsertAsync<long>(NewAlertRow(jobId: 999_999_999), ct));

        // ck_alerts_occurrence_count: occurrence_count below 1.
        await Assert.ThrowsAnyAsync<DbException>(() => Db.From<JobAlert>().InsertAsync<long>(NewAlertRow(occurrenceCount: 0), ct));
    }

    [Fact(
        DisplayName = "ux_alerts_dedupe admits one unresolved row per (namespace_id, dedupe_key) and stops filtering once it is resolved"
    )]
    public async Task Alerts_dedupe_index_admits_one_open_incident_per_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"schema-hardening:{TestId}";

        // The incident: one unresolved row on the key.
        var openId = await Db.From<JobAlert>().InsertAsync<long>(NewAlertRow(deduplicationKey: key), ct);
        Assert.True(openId > 0);

        // A second unresolved row on the same key is what the index exists to refuse - this is the
        // constraint the raise leans on to keep an incident single.
        await Assert.ThrowsAnyAsync<DbException>(() => Db.From<JobAlert>().InsertAsync<long>(NewAlertRow(deduplicationKey: key), ct));

        // Resolving the row takes it out of the filtered index, which is what frees the key for the next
        // incident. Without the filter this insert would fail exactly like the one above.
        Assert.Equal(
            1,
            await Db.From<JobAlert>().Where(a => a.Id == openId).UpdateOnlyAsync(() => new JobAlert { ResolvedAtUtc = DateTime.UtcNow }, ct)
        );
        Assert.True(await Db.From<JobAlert>().InsertAsync<long>(NewAlertRow(deduplicationKey: key), ct) > 0);

        // And resolved rows do not collide with each other either: the filter excludes every one of them.
        Assert.Equal(
            1,
            await Db.From<JobAlert>()
                .Where(a => a.DedupeKey == key && a.Id != openId)
                .UpdateOnlyAsync(() => new JobAlert { ResolvedAtUtc = DateTime.UtcNow }, ct)
        );
        Assert.True(await Db.From<JobAlert>().InsertAsync<long>(NewAlertRow(deduplicationKey: key), ct) > 0);
    }

    private JobAlert NewAlertRow(long? jobId = null, Guid? jobRef = null, string? deduplicationKey = null, int occurrenceCount = 1) =>
        new()
        {
            NamespaceId = Runtime.RegisteredNamespaceIds[TestNamespace],
            // A fresh ref per row: ux_alerts_ref accepts the all-zero default exactly once per
            // schema, and the conformance schema is append-only, so defaulting it made the
            // positive control fail on every run after the first. Minting also makes that
            // control prove what it claims - that a valid alert row inserts.
            AlertRef = Acta.AlertRef.New().Value,
            JobId = jobId,
            JobRef = jobRef,
            OriginCode = AlertOriginCode.Manual,
            SeverityCode = AlertSeverityCode.Info,
            Kind = AlertKindCode.FirstFailure,
            Title = "schema-hardening check",
            Message = "schema-hardening check",
            ChannelName = "default",
            DedupeKey = deduplicationKey,
            OccurrenceCount = occurrenceCount,
            DeliveryStatusCode = AlertDeliveryStatusCode.Pending,
        };

    [Fact(DisplayName = "ck_runtimes_counters rejects an UPDATE to a negative failure_count")]
    public async Task Runtimes_check_rejects_negative_failure_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);

        Assert.Equal(
            1,
            await Db.From<JobRuntime>().Where(r => r.Id == enq.JobId).UpdateOnlyAsync(() => new JobRuntime { FailureCount = 2 }, ct)
        );

        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.From<JobRuntime>().Where(r => r.Id == enq.JobId).UpdateOnlyAsync(() => new JobRuntime { FailureCount = -1 }, ct)
        );
    }

    [Fact(DisplayName = "Closed-family constraints reject unassigned values and 255")]
    public async Task Closed_code_family_rejects_unassigned_and_255()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);

        Task<int> SetStatusAsync(byte status) =>
            Db.ExecuteRawAsync(
                "UPDATE {schema}.runtimes SET status_code = @p_status WHERE job_id = @p_job_id",
                ct,
                ("@p_status", status),
                ("@p_job_id", enq.JobId)
            );

        Assert.Equal(1, await SetStatusAsync((byte)JobStatusCode.Ready));
        await Assert.ThrowsAnyAsync<DbException>(() => SetStatusAsync(99));
        await Assert.ThrowsAnyAsync<DbException>(() => SetStatusAsync(byte.MaxValue));
    }

    [Fact(DisplayName = "Consumer payload format 255 remains storable")]
    public async Task Consumer_payload_format_255_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        var custom = JobPayloadFormat.Custom(byte.MaxValue, "custom-max");

        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "INSERT INTO {schema}.checkpoints (job_id, kind_code, name, status_code, value_format_id, value) "
                    + "VALUES (@p_job_id, @p_kind, @p_name, NULL, @p_format, @p_value)",
                ct,
                ("@p_job_id", enq.JobId),
                ("@p_kind", (byte)JobCheckpointKindCode.Variable),
                ("@p_name", "custom-format"),
                ("@p_format", custom.Id),
                ("@p_value", new byte[] { 0xFF })
            )
        );

        var stored = await Db.From<JobCheckpoint>().Where(c => c.JobId == enq.JobId && c.Name == "custom-format").SingleOrDefaultAsync(ct);
        Assert.NotNull(stored);
        Assert.Equal(byte.MaxValue, stored!.ValueFormatId);
    }

    [Fact(DisplayName = "ck_checkpoints_kind_shape rejects every mismatched kind/status/due shape on INSERT and on UPDATE")]
    public async Task Checkpoints_kind_shape_rejects_mismatched_shapes()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        var due = DateTime.UtcNow.AddMinutes(5);

        // status_code is written as a literal rather than bound, for the reason the steps fixture
        // below omits its nullable column: a null TINYINT parameter leaves SQL Server no type to
        // infer. due_at_utc is bound only on the calls that set it.
        Task<int> InsertAsync(string name, JobCheckpointKindCode kind, JobCheckpointStatusCode? status, DateTime? dueAtUtc)
        {
            var statusSql = status is null ? "NULL" : ((byte)status.Value).ToString(CultureInfo.InvariantCulture);
            var sql =
                "INSERT INTO {schema}.checkpoints (job_id, kind_code, name, status_code, due_at_utc, value_format_id) "
                + $"VALUES (@p_job_id, @p_kind, @p_name, {statusSql}, {(dueAtUtc is null ? "NULL" : "@p_due")}, 0)";

            return dueAtUtc is null
                ? Db.ExecuteRawAsync(sql, ct, ("@p_job_id", enq.JobId), ("@p_kind", (byte)kind), ("@p_name", name))
                : Db.ExecuteRawAsync(
                    sql,
                    ct,
                    ("@p_job_id", enq.JobId),
                    ("@p_kind", (byte)kind),
                    ("@p_name", name),
                    ("@p_due", dueAtUtc.Value)
                );
        }

        Task<int> SetStatusAsync(string name, JobCheckpointStatusCode? status)
        {
            var statusSql = status is null ? "NULL" : ((byte)status.Value).ToString(CultureInfo.InvariantCulture);
            return Db.ExecuteRawAsync(
                "UPDATE {schema}.checkpoints SET status_code = " + statusSql + " WHERE job_id = @p_job_id AND name = @p_name",
                ct,
                ("@p_job_id", enq.JobId),
                ("@p_name", name)
            );
        }

        // One control per legal arm, so a rejection below proves the shape was refused and not the
        // statement.
        Assert.Equal(1, await InsertAsync("ok-variable", JobCheckpointKindCode.Variable, null, null));
        Assert.Equal(1, await InsertAsync("ok-progress", JobCheckpointKindCode.Progress, null, null));
        Assert.Equal(1, await InsertAsync("ok-signal", JobCheckpointKindCode.Signal, JobCheckpointStatusCode.Pending, due));
        Assert.Equal(1, await InsertAsync("ok-latch", JobCheckpointKindCode.ChildLatch, JobCheckpointStatusCode.Set, null));
        Assert.Equal(1, await InsertAsync("ok-timer", JobCheckpointKindCode.Timer, JobCheckpointStatusCode.Pending, due));
        Assert.Equal(1, await InsertAsync("ok-timer-consumed", JobCheckpointKindCode.Timer, JobCheckpointStatusCode.Consumed, due));

        // A stateful kind with no status. This is the shape the stateful arms admitted while they
        // tested membership alone: `NULL IN (...)` is unknown, and a CHECK passes on unknown.
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-signal-null", JobCheckpointKindCode.Signal, null, due));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-latch-null", JobCheckpointKindCode.ChildLatch, null, null));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-timer-null", JobCheckpointKindCode.Timer, null, due));

        // A stateful kind carrying a status its own arm does not list.
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertAsync("bad-timer-set", JobCheckpointKindCode.Timer, JobCheckpointStatusCode.Set, due)
        );
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertAsync("bad-signal-consumed", JobCheckpointKindCode.Signal, JobCheckpointStatusCode.Consumed, due)
        );

        // A stateless kind carrying either of the columns bound to the stateful arms.
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertAsync("bad-variable-status", JobCheckpointKindCode.Variable, JobCheckpointStatusCode.Pending, null)
        );
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertAsync("bad-progress-status", JobCheckpointKindCode.Progress, JobCheckpointStatusCode.Set, null)
        );
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-variable-due", JobCheckpointKindCode.Variable, null, due));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-progress-due", JobCheckpointKindCode.Progress, null, due));

        // The constraint is re-evaluated on every write, and an UPDATE is how a live slot would
        // actually reach a bad shape, so the same shapes have to be unreachable that way too.
        Assert.Equal(1, await SetStatusAsync("ok-signal", JobCheckpointStatusCode.Set));
        await Assert.ThrowsAnyAsync<DbException>(() => SetStatusAsync("ok-signal", null));
        await Assert.ThrowsAnyAsync<DbException>(() => SetStatusAsync("ok-timer", JobCheckpointStatusCode.Set));
        await Assert.ThrowsAnyAsync<DbException>(() => SetStatusAsync("ok-variable", JobCheckpointStatusCode.Pending));
    }

    [Fact(DisplayName = "ck_steps_attempt_number rejects an INSERT with attempt_number zero")]
    public async Task Steps_check_rejects_attempt_number_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);

        // Raw insert, omitting the nullable `result` column entirely: binding it explicit-NULL trips
        // SQL Server's inability to infer a SqlDbType for a null varbinary parameter (the same class of
        // issue ActaTestSeeder.SeedJobAsync sidesteps for jobs.input).
        Task<int> InsertStepAsync(string name, short attemptNumber) =>
            Db.ExecuteRawAsync(
                "INSERT INTO {schema}.steps (job_id, name, status_code, attempt_number, result_format_id) VALUES (@p_job_id, @p_name, 10, @p_attempt, 0)",
                ct,
                ("@p_job_id", enq.JobId),
                ("@p_name", name),
                ("@p_attempt", attemptNumber)
            );

        Assert.Equal(1, await InsertStepAsync("step-valid", 1));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertStepAsync("step-invalid", 0));
    }

    [Fact(DisplayName = "ck_workers_max_concurrency rejects an INSERT with max_concurrency zero")]
    public async Task Workers_check_rejects_max_concurrency_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];

        JobWorker NewWorker(int maxConcurrency) =>
            new()
            {
                NamespaceId = nsId,
                // Same reason as the alerts fixture above: ux_workers_ref makes the all-zero default a
                // once-per-schema value, which broke the positive control on any re-run.
                WorkerRef = Acta.WorkerRef.New().Value,
                Status = WorkerStatusCode.Active,
                DeploymentVersion = "test",
                Host = "test-host",
                MaxConcurrency = maxConcurrency,
                LastHeartbeatAtUtc = DateTime.UtcNow,
            };

        Assert.True(await Db.From<JobWorker>().InsertAsync<int>(NewWorker(4), ct) > 0);
        await Assert.ThrowsAnyAsync<DbException>(() => Db.From<JobWorker>().InsertAsync<int>(NewWorker(0), ct));
    }

    [Fact(DisplayName = "runtimes and tags agree with jobs on namespace_id after EnqueueOne, EnqueueBatch, a child enqueue, and Restart")]
    public async Task Denormalized_namespace_id_agrees_with_jobs_across_write_paths()
    {
        var ct = TestContext.Current.CancellationToken;
        var tags = new[] { new TagInput("kind", "denorm-check") };

        var one = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2)), Tags: tags),
            ct
        );
        await AssertNamespaceAgreementAsync(one.JobId, ct);

        var batch = await Jobs.EnqueueBatchAsync(
            [new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4)), Tags: tags)],
            ct
        );
        await AssertNamespaceAgreementAsync(batch[0].JobId, ct);

        var parent = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(5, 6))),
            ct
        );
        var child = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(7, 8)),
                Tags: tags,
                ParentJobId: parent.JobId
            ),
            ct
        );
        await AssertNamespaceAgreementAsync(child.JobId, ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(one, ct));
        var restart = await Jobs.RestartAsync(one, ct: ct);
        Assert.Equal(ControlAction.Applied, restart.Action);
        await AssertNamespaceAgreementAsync(one.JobId, ct);
    }

    [Fact(DisplayName = "A recurring slot's schedule row agrees with its job on namespace_id and definition_id")]
    public async Task Recurring_slot_schedule_agrees_with_its_job_namespace_and_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, "recurring-ping"), ct);
        Assert.NotNull(slotId);

        var job = await Db.From<Job>().Where(j => j.Id == slotId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);

        var schedule = Assert.Single(await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct));
        Assert.Equal(job!.NamespaceId, schedule.NamespaceId);
        Assert.Equal(job.DefinitionId, schedule.DefinitionId);
    }

    private async Task AssertNamespaceAgreementAsync(long jobId, CancellationToken ct)
    {
        var job = await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);
        var runtime = await Db.From<JobRuntime>().Where(r => r.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(runtime);
        Assert.Equal(job!.NamespaceId, runtime!.NamespaceId);

        var tags = await Db.From<Tag>().Where(t => t.ScopeCode == TagScopeCode.Job && t.ScopeId == jobId).ToListAsync(ct);
        Assert.NotEmpty(tags);
        Assert.All(tags, t => Assert.Equal(job.NamespaceId, t.NamespaceId));
    }
}
