using System.Data.Common;
using System.Globalization;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Schema;

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
    Contract = "The hardened baseline schema enforces every new CHECK, admits one unresolved alert per deduplication key, seeds sys, and keeps jobs_state in step with jobs."
)]
public sealed class SchemaHardeningSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "The seeded sys namespace (id 1, name sys) exists on a fresh install")]
    public async Task Sys_namespace_seed_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;

        var sys = await Db.From<NamespaceEntity>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);

        Assert.NotNull(sys);
        Assert.Equal("sys", sys!.NamespaceName);
    }

    [Fact(DisplayName = "ck_definitions_max_attempts rejects an UPDATE to zero while a positive value updates cleanly")]
    public async Task Definitions_check_family_rejects_max_attempts_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeder = new ActaTestSeeder(Db);
        var nsId = await seeder.SeedJobNamespaceAsync(Unique("defs-ck"), ct: ct);
        var defId = await seeder.SeedJobDefinitionAsync(nsId, Unique("defs-ck-def"), ct: ct);

        Assert.Equal(
            1,
            await Db.From<DefinitionEntity>().Where(d => d.Id == defId).UpdateOnlyAsync(() => new DefinitionEntity { MaxAttempts = 5 }, ct)
        );

        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.From<DefinitionEntity>().Where(d => d.Id == defId).UpdateOnlyAsync(() => new DefinitionEntity { MaxAttempts = 0 }, ct)
        );
    }

    [Fact(DisplayName = "ck_alerts_job_ref_pair and ck_alerts_occurrence_count each reject their violating INSERT")]
    public async Task Alerts_checks_reject_job_ref_pair_and_zero_occurrence_count()
    {
        var ct = TestContext.Current.CancellationToken;

        // Positive control: the same shape with no violation inserts cleanly.
        Assert.True(await Db.From<AlertEntity>().InsertAsync<long>(NewAlertRow(), ct) > 0);

        // ck_alerts_job_ref_pair: job_id set without job_ref (the shape raise_alert produces for an
        // unknown job_id, load-bearing on the alert write path since the data-model-hardening re-cut).
        await Assert.ThrowsAnyAsync<DbException>(() => Db.From<AlertEntity>().InsertAsync<long>(NewAlertRow(jobId: 999_999_999), ct));

        // ck_alerts_occurrence_count: occurrence_count below 1.
        await Assert.ThrowsAnyAsync<DbException>(() => Db.From<AlertEntity>().InsertAsync<long>(NewAlertRow(occurrenceCount: 0), ct));
    }

    [Fact(
        DisplayName = "ux_alerts_deduplication_key admits one unresolved row per (namespace_id, deduplication_key) and stops filtering once it is resolved"
    )]
    public async Task Alerts_dedupe_index_admits_one_open_incident_per_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"schema-hardening:{TestId}";

        // The incident: one unresolved row on the key.
        var openId = await Db.From<AlertEntity>().InsertAsync<long>(NewAlertRow(deduplicationKey: key), ct);
        Assert.True(openId > 0);

        // A second unresolved row on the same key is what the index exists to refuse - this is the
        // constraint the raise leans on to keep an incident single.
        await Assert.ThrowsAnyAsync<DbException>(() => Db.From<AlertEntity>().InsertAsync<long>(NewAlertRow(deduplicationKey: key), ct));

        // Resolving the row takes it out of the filtered index, which is what frees the key for the next
        // incident. Without the filter this insert would fail exactly like the one above.
        Assert.Equal(
            1,
            await Db.From<AlertEntity>()
                .Where(a => a.Id == openId)
                .UpdateOnlyAsync(() => new AlertEntity { ResolvedAtUtc = DateTime.UtcNow }, ct)
        );
        Assert.True(await Db.From<AlertEntity>().InsertAsync<long>(NewAlertRow(deduplicationKey: key), ct) > 0);

        // And resolved rows do not collide with each other either: the filter excludes every one of them.
        Assert.Equal(
            1,
            await Db.From<AlertEntity>()
                .Where(a => a.DeduplicationKey == key && a.Id != openId)
                .UpdateOnlyAsync(() => new AlertEntity { ResolvedAtUtc = DateTime.UtcNow }, ct)
        );
        Assert.True(await Db.From<AlertEntity>().InsertAsync<long>(NewAlertRow(deduplicationKey: key), ct) > 0);
    }

    private AlertEntity NewAlertRow(long? jobId = null, Guid? jobRef = null, string? deduplicationKey = null, int occurrenceCount = 1) =>
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
            AlertMessage = "schema-hardening check",
            ChannelName = "default",
            DeduplicationKey = deduplicationKey,
            OccurrenceCount = occurrenceCount,
            DeliveryStatusCode = AlertDeliveryStatusCode.Pending,
        };

    [Fact(DisplayName = "ck_jobs_state_counters rejects an UPDATE to a negative failure_count")]
    public async Task Jobs_state_check_rejects_negative_failure_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);

        Assert.Equal(
            1,
            await Db.From<JobStateEntity>().Where(r => r.Id == enq.JobId).UpdateOnlyAsync(() => new JobStateEntity { FailureCount = 2 }, ct)
        );

        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.From<JobStateEntity>().Where(r => r.Id == enq.JobId).UpdateOnlyAsync(() => new JobStateEntity { FailureCount = -1 }, ct)
        );
    }

    [Fact(
        DisplayName = "ck_jobs_state_inflight_leased, ck_jobs_state_result_idle, and ck_jobs_state_ready_due bind a status to its columns, and ck_jobs_state_result_pair refuses half a result"
    )]
    public async Task Jobs_state_status_requires_its_paired_column()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);

        Task<int> SetLeasedAsync(JobStatusCode status) =>
            Db.ExecuteRawAsync(
                "UPDATE {schema}.jobs_state SET job_status_code = @p_status, leased_by_worker_id = @p_worker, lease_expires_at_utc = @p_expires "
                    + "WHERE job_id = @p_job_id",
                ct,
                ("@p_status", (byte)status),
                ("@p_worker", 424_242),
                ("@p_expires", DateTime.UtcNow.AddMinutes(5)),
                ("@p_job_id", enq.JobId)
            );

        Task<int> SetUnleasedAsync(JobStatusCode status) =>
            Db.ExecuteRawAsync(
                "UPDATE {schema}.jobs_state SET job_status_code = @p_status, leased_by_worker_id = NULL, lease_expires_at_utc = NULL "
                    + "WHERE job_id = @p_job_id",
                ct,
                ("@p_status", (byte)status),
                ("@p_job_id", enq.JobId)
            );

        // The shape every claim writes: the in-flight status and the whole lease in one statement.
        Assert.Equal(1, await SetLeasedAsync(JobStatusCode.Executing));

        // Releasing the lease on the way out of flight is the control for the two rejections below.
        Assert.Equal(1, await SetUnleasedAsync(JobStatusCode.Ready));

        await Assert.ThrowsAnyAsync<DbException>(() => SetUnleasedAsync(JobStatusCode.Executing));

        // An untyped NULL parameter binds as text on SQL Server, so a missing result is written as a literal.
        Task<int> SetResultAsync(byte formatId, byte[]? result) =>
            result is null
                ? Db.ExecuteRawAsync(
                    "UPDATE {schema}.jobs_state SET result_format_id = @p_format, result = NULL WHERE job_id = @p_job_id",
                    ct,
                    ("@p_format", formatId),
                    ("@p_job_id", enq.JobId)
                )
                : Db.ExecuteRawAsync(
                    "UPDATE {schema}.jobs_state SET result_format_id = @p_format, result = @p_result WHERE job_id = @p_job_id",
                    ct,
                    ("@p_format", formatId),
                    ("@p_result", result),
                    ("@p_job_id", enq.JobId)
                );

        // ck_jobs_state_result_pair holds a format to its bytes; ck_jobs_state_result_idle keeps a result off an
        // Executing row, so a claim that did not clear the result is refused.
        Assert.Equal(1, await SetResultAsync(JobPayloadFormat.Bytes.Id, [1, 2, 3]));
        await Assert.ThrowsAnyAsync<DbException>(() => SetResultAsync(JobPayloadFormat.Bytes.Id, null));
        await Assert.ThrowsAnyAsync<DbException>(() => SetResultAsync(JobPayloadFormat.None.Id, [1, 2, 3]));
        await Assert.ThrowsAnyAsync<DbException>(() => SetLeasedAsync(JobStatusCode.Executing));
        Assert.Equal(1, await SetResultAsync(JobPayloadFormat.None.Id, null));
        Assert.Equal(1, await SetLeasedAsync(JobStatusCode.Executing));

        // ck_jobs_state_ready_due is the same shape of rule on the other hot column: a Ready row is due
        // at a known instant, so the claim can walk ix_jobs_state_claim_ready instead of sorting.
        var seeder = new ActaTestSeeder(Db);
        var nsId = await seeder.SeedJobNamespaceAsync(Unique("ready-due"), ct: ct);
        var defId = await seeder.SeedJobDefinitionAsync(nsId, Unique("ready-due-def"), ct: ct);

        async Task<long> InsertRuntimeAsync(JobStatusCode status, DateTime? nextRunAtUtc)
        {
            var (jobId, _) = await seeder.SeedJobAsync(nsId, defId, ct: ct);
            return await Db.From<JobStateEntity>()
                .InsertAsync<long>(
                    new JobStateEntity
                    {
                        Id = jobId,
                        NamespaceId = nsId,
                        Status = status,
                        Priority = JobPriorityCode.Normal,
                        NextRunAtUtc = nextRunAtUtc,
                    },
                    ct
                );
        }

        // The two shapes the write paths produce: Ready on a known instant, and a Suspended row parked
        // on an unbounded wait. They are the control for the rejections below.
        var readyId = await InsertRuntimeAsync(JobStatusCode.Ready, DateTime.UtcNow.AddMinutes(5));
        var suspendedId = await InsertRuntimeAsync(JobStatusCode.Suspended, null);

        await Assert.ThrowsAnyAsync<DbException>(() => InsertRuntimeAsync(JobStatusCode.Ready, null));

        // A live row reaches the bad shape by UPDATE, which is how the sleep timer could null the
        // instant under a worker whose lease had already been reclaimed.
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.From<JobStateEntity>().Where(r => r.Id == readyId).UpdateOnlyAsync(() => new JobStateEntity { NextRunAtUtc = null }, ct)
        );
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.From<JobStateEntity>()
                .Where(r => r.Id == suspendedId)
                .UpdateOnlyAsync(() => new JobStateEntity { Status = JobStatusCode.Ready }, ct)
        );
    }

    [Fact(
        DisplayName = "A code column stores an id this build does not know, and ck_checkpoints_kind_shape still binds the kinds it names"
    )]
    public async Task Code_column_stores_an_unknown_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        const byte Unassigned = 99;

        static string Literal(byte? code) => code is null ? "NULL" : code.Value.ToString(CultureInfo.InvariantCulture);

        Task<int> SetStatusAsync(byte status) =>
            Db.ExecuteRawAsync(
                "UPDATE {schema}.jobs_state SET job_status_code = @p_status WHERE job_id = @p_job_id",
                ct,
                ("@p_status", status),
                ("@p_job_id", enq.JobId)
            );

        Task<int> InsertCheckpointAsync(string name, byte kind, byte? status, bool due)
        {
            var sql =
                "INSERT INTO {schema}.checkpoints"
                + " (job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, due_at_utc, value_format_id) "
                + $"VALUES (@p_job_id, {Literal(kind)}, @p_name, {Literal(status)}, {(due ? "@p_due" : "NULL")}, 0)";
            return due
                ? Db.ExecuteRawAsync(sql, ct, ("@p_job_id", enq.JobId), ("@p_name", name), ("@p_due", DateTime.UtcNow.AddMinutes(5)))
                : Db.ExecuteRawAsync(sql, ct, ("@p_job_id", enq.JobId), ("@p_name", name));
        }

        // No value-list CHECK: a newer build adds a code value without a table change.
        Assert.Equal(1, await SetStatusAsync(Unassigned));
        Assert.Equal(1, await SetStatusAsync((byte)JobStatusCode.Ready));

        // A newer build's kind, in either shape, and a newer build's status on each stateful arm.
        Assert.Equal(1, await InsertCheckpointAsync("unassigned-kind-stateless", Unassigned, null, due: false));
        Assert.Equal(1, await InsertCheckpointAsync("unassigned-kind-stateful", Unassigned, Unassigned, due: true));
        Assert.Equal(1, await InsertCheckpointAsync("unassigned-signal-status", (byte)CheckpointKindCode.Signal, Unassigned, due: false));
        Assert.Equal(1, await InsertCheckpointAsync("unassigned-timer-status", (byte)CheckpointKindCode.Timer, Unassigned, due: true));

        // A known kind keeps its shape whatever its status: a timer with no due instant is still refused.
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertCheckpointAsync("unassigned-timer-no-due", (byte)CheckpointKindCode.Timer, Unassigned, due: false)
        );

        // A newer build's entity kind.
        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "INSERT INTO {schema}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search) "
                    + $"VALUES ({Literal(Unassigned)}, @p_entity_id, @p_tag_name, NULL, NULL)",
                ct,
                ("@p_entity_id", enq.JobId),
                ("@p_tag_name", "unassigned-entity")
            )
        );
    }

    [Fact(DisplayName = "Consumer payload format 255 remains storable")]
    public async Task Consumer_payload_format_255_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        var custom = JobPayloadFormat.Custom(byte.MaxValue, "custom-max");

        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "INSERT INTO {schema}.checkpoints"
                    + " (job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value) "
                    + "VALUES (@p_job_id, @p_kind, @p_name, NULL, @p_format, @p_value)",
                ct,
                ("@p_job_id", enq.JobId),
                ("@p_kind", (byte)CheckpointKindCode.Variable),
                ("@p_name", "custom-format"),
                ("@p_format", custom.Id),
                ("@p_value", new byte[] { 0xFF })
            )
        );

        var stored = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == enq.JobId && c.CheckpointName == "custom-format")
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(stored);
        Assert.Equal(byte.MaxValue, stored!.ValueFormatId);
    }

    [Fact(DisplayName = "ck_checkpoints_kind_shape rejects every mismatched kind/status/due shape on INSERT and on UPDATE")]
    public async Task Checkpoints_kind_shape_rejects_mismatched_shapes()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        var due = DateTime.UtcNow.AddMinutes(5);

        // checkpoint_status_code is written as a literal rather than bound, for the reason the steps fixture
        // below omits its nullable column: a null TINYINT parameter leaves SQL Server no type to
        // infer. due_at_utc is bound only on the calls that set it.
        Task<int> InsertAsync(string name, CheckpointKindCode kind, CheckpointStatusCode? status, DateTime? dueAtUtc)
        {
            var statusSql = status is null ? "NULL" : ((byte)status.Value).ToString(CultureInfo.InvariantCulture);
            var sql =
                "INSERT INTO {schema}.checkpoints"
                + " (job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, due_at_utc, value_format_id) "
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

        Task<int> SetStatusAsync(string name, CheckpointStatusCode? status)
        {
            var statusSql = status is null ? "NULL" : ((byte)status.Value).ToString(CultureInfo.InvariantCulture);
            return Db.ExecuteRawAsync(
                "UPDATE {schema}.checkpoints SET checkpoint_status_code = "
                    + statusSql
                    + " WHERE job_id = @p_job_id AND checkpoint_name = @p_name",
                ct,
                ("@p_job_id", enq.JobId),
                ("@p_name", name)
            );
        }

        // One control per legal arm, so a rejection below proves the shape was refused and not the
        // statement.
        Assert.Equal(1, await InsertAsync("ok-variable", CheckpointKindCode.Variable, null, null));
        Assert.Equal(1, await InsertAsync("ok-progress", CheckpointKindCode.Progress, null, null));
        Assert.Equal(1, await InsertAsync("ok-signal", CheckpointKindCode.Signal, CheckpointStatusCode.Pending, due));
        Assert.Equal(1, await InsertAsync("ok-latch", CheckpointKindCode.ChildLatch, CheckpointStatusCode.Set, null));
        Assert.Equal(1, await InsertAsync("ok-timer", CheckpointKindCode.Timer, CheckpointStatusCode.Pending, due));
        Assert.Equal(1, await InsertAsync("ok-timer-consumed", CheckpointKindCode.Timer, CheckpointStatusCode.Consumed, due));

        // A stateful kind with no status. This is the shape the stateful arms admitted while they
        // tested membership alone: `NULL IN (...)` is unknown, and a CHECK passes on unknown.
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-signal-null", CheckpointKindCode.Signal, null, due));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-latch-null", CheckpointKindCode.ChildLatch, null, null));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-timer-null", CheckpointKindCode.Timer, null, due));

        // A stateless kind carrying either of the columns bound to the stateful arms.
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertAsync("bad-variable-status", CheckpointKindCode.Variable, CheckpointStatusCode.Pending, null)
        );
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertAsync("bad-progress-status", CheckpointKindCode.Progress, CheckpointStatusCode.Set, null)
        );
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-variable-due", CheckpointKindCode.Variable, null, due));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync("bad-progress-due", CheckpointKindCode.Progress, null, due));

        // A timer with no due instant is a wait nothing can wake. The three arm routines always compute
        // one, so the shape only exists if something wrote around them.
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertAsync("bad-timer-no-due", CheckpointKindCode.Timer, CheckpointStatusCode.Pending, null)
        );
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertAsync("bad-timer-consumed-no-due", CheckpointKindCode.Timer, CheckpointStatusCode.Consumed, null)
        );

        // The constraint is re-evaluated on every write, and an UPDATE is how a live slot would
        // actually reach a bad shape, so the same shapes have to be unreachable that way too.
        Assert.Equal(1, await SetStatusAsync("ok-signal", CheckpointStatusCode.Set));
        await Assert.ThrowsAnyAsync<DbException>(() => SetStatusAsync("ok-signal", null));
        await Assert.ThrowsAnyAsync<DbException>(() => SetStatusAsync("ok-variable", CheckpointStatusCode.Pending));
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.ExecuteRawAsync(
                "UPDATE {schema}.checkpoints SET due_at_utc = NULL WHERE job_id = @p_job_id AND checkpoint_name = @p_name",
                ct,
                ("@p_job_id", enq.JobId),
                ("@p_name", "ok-timer")
            )
        );
    }

    [Fact(DisplayName = "ck_steps_attempt_number rejects an INSERT with attempt_number zero")]
    public async Task Steps_check_rejects_attempt_number_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);

        // Raw insert, omitting the nullable `result` column entirely: binding it explicit-NULL trips
        // SQL Server's inability to infer a SqlDbType for a null varbinary parameter (the same class of
        // issue ActaTestSeeder.SeedJobAsync sidesteps for jobs.input).
        Task<int> InsertStepAsync(string name, int attemptNumber) =>
            Db.ExecuteRawAsync(
                "INSERT INTO {schema}.steps (job_id, step_name, step_status_code, attempt_number, result_format_id)"
                    + " VALUES (@p_job_id, @p_name, 10, @p_attempt, 0)",
                ct,
                ("@p_job_id", enq.JobId),
                ("@p_name", name),
                ("@p_attempt", attemptNumber)
            );

        Assert.Equal(1, await InsertStepAsync("step-valid", 1));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertStepAsync("step-invalid", 0));
    }

    [Fact(DisplayName = "ck_steps_terminal_no_retry rejects a retry instant on every terminal status, on INSERT and on UPDATE")]
    public async Task Steps_terminal_rows_carry_no_retry_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        var retry = DateTime.UtcNow.AddMinutes(5);

        // Pending is the one status a retry instant belongs to.
        Assert.Equal(1, await InsertStepAsync(enq.JobId, "retry-pending", StepStatusCode.Pending, ct, nextRetryAtUtc: retry));

        foreach (var terminal in new[] { StepStatusCode.Succeeded, StepStatusCode.Exhausted, StepStatusCode.Interrupted })
        {
            Assert.Equal(1, await InsertStepAsync(enq.JobId, $"terminal-{(byte)terminal}", terminal, ct));
            await Assert.ThrowsAnyAsync<DbException>(() =>
                InsertStepAsync(enq.JobId, $"terminal-retry-{(byte)terminal}", terminal, ct, nextRetryAtUtc: retry)
            );
        }

        // The live path into a terminal status is an UPDATE of the pending row, which is where
        // start_step and complete_step have to drop the instant as part of the transition.
        await Assert.ThrowsAnyAsync<DbException>(() => SetStepStatusAsync(enq.JobId, "retry-pending", StepStatusCode.Interrupted, ct));
    }

    [Fact(DisplayName = "ck_steps_result_succeeded admits a stored result only on a Succeeded row")]
    public async Task Steps_result_lives_only_on_a_succeeded_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);

        Assert.Equal(1, await InsertStepAsync(enq.JobId, "result-succeeded", StepStatusCode.Succeeded, ct, resultFormatId: 1));

        foreach (var status in new[] { StepStatusCode.Pending, StepStatusCode.Exhausted, StepStatusCode.Interrupted })
        {
            Assert.Equal(1, await InsertStepAsync(enq.JobId, $"no-result-{(byte)status}", status, ct));
            await Assert.ThrowsAnyAsync<DbException>(() =>
                InsertStepAsync(enq.JobId, $"result-{(byte)status}", status, ct, resultFormatId: 1)
            );
        }

        // complete_step writes the status and the result together, so a Succeeded row can never be moved
        // off that status while the result stays behind.
        await Assert.ThrowsAnyAsync<DbException>(() => SetStepStatusAsync(enq.JobId, "result-succeeded", StepStatusCode.Exhausted, ct));
    }

    [Fact(DisplayName = "ck_steps_failure_pair rejects a failure message with no failure code, on INSERT and on UPDATE")]
    public async Task Steps_failure_message_requires_a_failure_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var enq = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);

        // Both legal shapes: no failure at all, and the full pair complete_step writes on a failure.
        Assert.Equal(1, await InsertStepAsync(enq.JobId, "failure-none", StepStatusCode.Pending, ct));
        Assert.Equal(
            1,
            await InsertStepAsync(
                enq.JobId,
                "failure-pair",
                StepStatusCode.Pending,
                ct,
                failureCode: StepFailureCode.UnhandledException,
                failureMessage: "boom"
            )
        );

        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertStepAsync(enq.JobId, "failure-orphan", StepStatusCode.Pending, ct, failureMessage: "boom")
        );

        // The same orphan reached by UPDATE: clearing only the code off a paired row.
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.ExecuteRawAsync(
                "UPDATE {schema}.steps SET failure_code = NULL WHERE job_id = @p_job_id AND step_name = @p_name",
                ct,
                ("@p_job_id", enq.JobId),
                ("@p_name", "failure-pair")
            )
        );
    }

    // Raw composition for the same reason the attempt_number fixture above gives: nullable columns are
    // omitted rather than bound explicit-NULL, and code columns are rendered as literals, because
    // SQL Server infers no type for a null TINYINT or VARBINARY parameter.
    private Task<int> InsertStepAsync(
        long jobId,
        string name,
        StepStatusCode status,
        CancellationToken ct,
        DateTime? nextRetryAtUtc = null,
        byte resultFormatId = 0,
        StepFailureCode? failureCode = null,
        string? failureMessage = null
    )
    {
        List<string> columns = ["job_id", "step_name", "step_status_code", "attempt_number", "result_format_id"];
        List<string> values =
        [
            "@p_job_id",
            "@p_name",
            ((byte)status).ToString(CultureInfo.InvariantCulture),
            "1",
            resultFormatId.ToString(CultureInfo.InvariantCulture),
        ];
        List<(string Name, object? Value)> parameters = [("@p_job_id", jobId), ("@p_name", name)];

        if (resultFormatId != 0)
        {
            // ck_steps_result_pair binds the payload to the format id, so a non-zero id has to carry
            // bytes; without them that constraint fires first and the rejection proves nothing.
            columns.Add("result");
            values.Add("@p_result");
            parameters.Add(("@p_result", new byte[] { 0x01 }));
        }

        if (nextRetryAtUtc is { } retry)
        {
            columns.Add("next_retry_at_utc");
            values.Add("@p_retry");
            parameters.Add(("@p_retry", retry));
        }

        if (failureCode is { } code)
        {
            columns.Add("failure_code");
            values.Add(((byte)code).ToString(CultureInfo.InvariantCulture));
        }

        if (failureMessage is { } message)
        {
            columns.Add("failure_message");
            values.Add("@p_message");
            parameters.Add(("@p_message", message));
        }

        return Db.ExecuteRawAsync(
            $"INSERT INTO {{schema}}.steps ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})",
            ct,
            [.. parameters]
        );
    }

    private Task<int> SetStepStatusAsync(long jobId, string name, StepStatusCode status, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.steps SET step_status_code = "
                + ((byte)status).ToString(CultureInfo.InvariantCulture)
                + " WHERE job_id = @p_job_id AND step_name = @p_name",
            ct,
            ("@p_job_id", jobId),
            ("@p_name", name)
        );

    [Fact(DisplayName = "ck_workers_max_concurrent_executors rejects an INSERT with max_concurrent_executors zero")]
    public async Task Workers_check_rejects_max_concurrent_executors_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];

        WorkerEntity NewWorker(int maxConcurrentExecutors) =>
            new()
            {
                NamespaceId = nsId,
                // Same reason as the alerts fixture above: ux_workers_ref makes the all-zero default a
                // once-per-schema value, which broke the positive control on any re-run.
                WorkerRef = Acta.WorkerRef.New().Value,
                Status = WorkerStatusCode.Active,
                DeploymentVersion = "test",
                HostName = "test-host",
                MaxConcurrentExecutors = maxConcurrentExecutors,
                LastSeenAtUtc = DateTime.UtcNow,
            };

        Assert.True(await Db.From<WorkerEntity>().InsertAsync<int>(NewWorker(4), ct) > 0);
        await Assert.ThrowsAnyAsync<DbException>(() => Db.From<WorkerEntity>().InsertAsync<int>(NewWorker(0), ct));
    }

    [Fact(DisplayName = "jobs_state agrees with jobs on namespace_id after EnqueueJob, EnqueueJobs, a child enqueue, and Restart")]
    public async Task Denormalized_namespace_id_agrees_with_jobs_across_write_paths()
    {
        var ct = TestContext.Current.CancellationToken;

        var one = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);
        await AssertNamespaceAgreementAsync(one.JobId, ct);

        var batch = (
            await Jobs.EnqueueBatchAsync([new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4)))], ct)
        ).Jobs;
        await AssertNamespaceAgreementAsync(batch[0].JobId, ct);

        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(5, 6))),
            ct
        );
        var child = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(7, 8)), ParentJobId: parent.JobId),
            ct
        );
        await AssertNamespaceAgreementAsync(child.JobId, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(one, ct));
        var restart = await Jobs.RestartAsync(one, ct: ct);
        Assert.Equal(ControlAction.Applied, restart.Action);
        await AssertNamespaceAgreementAsync(one.JobId, ct);
    }

    [Fact(DisplayName = "A recurring slot's schedule and cursor rows agree with its job on namespace_id and definition_id")]
    public async Task Recurring_slot_schedule_agrees_with_its_job_namespace_and_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = (await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, "recurring-ping"), ct))?.JobId;
        Assert.NotNull(slotId);

        var job = await Db.From<JobEntity>().Where(j => j.Id == slotId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);

        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId!.Value, ct));
        Assert.Equal(job!.NamespaceId, schedule.Config.NamespaceId);
        Assert.Equal(job.NamespaceId, schedule.Cursor.NamespaceId);
        Assert.Equal(job.DefinitionId, schedule.DefinitionId);
    }

    private async Task AssertNamespaceAgreementAsync(long jobId, CancellationToken ct)
    {
        var job = await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);
        var runtime = await Db.From<JobStateEntity>().Where(r => r.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(runtime);
        Assert.Equal(job!.NamespaceId, runtime!.NamespaceId);
    }
}
