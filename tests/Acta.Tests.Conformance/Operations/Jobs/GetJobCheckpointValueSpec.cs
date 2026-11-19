using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for GetJobCheckpointValue: one checkpoint's value reads back as stored, by the kind and the name the
/// checkpoint list shows, and null stands for a checkpoint that holds no value, one that is not there, the scheduler's
/// firing record, a job that is not there, and a deduplication key looked up in another namespace.
/// </summary>
[ConformanceSpec(
    "job-payload-reads.checkpoint-value",
    "GetJobCheckpointValue reads one checkpoint's stored value",
    Area = "Reads",
    Contract = "GetJobCheckpointValue returns a checkpoint's stored value by kind and name, and null when it holds none or no checkpoint or job matches."
)]
[Deploys("add-numbers")]
public sealed class GetJobCheckpointValueSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobCheckpointValueSpec>>
{
    [Fact(DisplayName = "A variable, a raised signal's value, and progress each read back in the format and bytes stored")]
    public async Task Each_value_reads_back_as_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var fence = await ExecutingJobAsync(ct);
        var variable = JobPayload.Bytes([0, 1, 254, 255]);
        var progress = JobPayload.Json(40);
        var signal = JobPayload.Json(new AddNumbers(2, 3));
        await SetAsync(fence, CheckpointKindCode.Variable, "fetch.status", variable, ct);
        await SetAsync(fence, CheckpointKindCode.Progress, "sys.progress", progress, ct);
        await RaiseAsync(fence.JobId, "s.ready", signal, ct);

        foreach (
            var (kind, name, stored) in new[]
            {
                (CheckpointKindCode.Variable, "fetch.status", variable),
                (CheckpointKindCode.Progress, "sys.progress", progress),
                (CheckpointKindCode.Signal, "s.ready", signal),
            }
        )
        {
            var value = (await Jobs.GetCheckpointValueAsync(JobLookup.ById(fence.JobId), kind, name, ct))?.Value;
            Assert.NotNull(value);
            Assert.Equal(stored.Format.Id, value!.Value.Format.Id);
            Assert.Equal(stored.Data.ToArray(), value.Value.Data.ToArray());
        }
    }

    [Fact(DisplayName = "A value with a format and no bytes reads back empty, not as no value")]
    public async Task An_empty_value_reads_back_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var fence = await ExecutingJobAsync(ct);
        await SetAsync(fence, CheckpointKindCode.Variable, "empty", JobPayload.Bytes([]), ct);

        var value = (await Jobs.GetCheckpointValueAsync(JobLookup.ById(fence.JobId), CheckpointKindCode.Variable, "empty", ct))?.Value;

        Assert.NotNull(value);
        Assert.Equal(JobPayloadFormat.Bytes.Id, value!.Value.Format.Id);
        Assert.Empty(value.Value.Data.ToArray());
    }

    [Fact(DisplayName = "A child's outcome reads back under the parent's sys.child name")]
    public async Task A_child_outcome_reads_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var fence = await ExecutingJobAsync(ct);
        var name = "sys.child." + fence.JobId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var outcome = System.Text.Encoding.UTF8.GetBytes("{\"childJobId\":1,\"status\":100,\"executionNumber\":1}");
        await Db.ExecuteRawAsync(
            "INSERT INTO {schema}.checkpoints (job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value)"
                + " VALUES (@p_job_id, @p_kind, @p_name, @p_status, 1, @p_value)",
            ct,
            ("@p_job_id", fence.JobId),
            ("@p_kind", (byte)CheckpointKindCode.Child),
            ("@p_name", name),
            ("@p_status", (byte)CheckpointStatusCode.Set),
            ("@p_value", outcome)
        );

        var value = (await Jobs.GetCheckpointValueAsync(JobLookup.ById(fence.JobId), CheckpointKindCode.Child, name, ct))?.Value;

        Assert.NotNull(value);
        Assert.Equal(JobPayloadFormat.Json.Id, value!.Value.Format.Id);
        Assert.Equal(outcome, value.Value.Data.ToArray());
    }

    [Fact(DisplayName = "Only the stored name reads a value: another case or white space around it answers null on every provider")]
    public async Task Only_the_stored_name_reads_a_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var fence = await ExecutingJobAsync(ct);
        await SetAsync(fence, CheckpointKindCode.Variable, "stage", JobPayload.Json("packed"), ct);
        var job = JobLookup.ById(fence.JobId);

        Assert.NotNull(await Jobs.GetCheckpointValueAsync(job, CheckpointKindCode.Variable, "stage", ct));
        foreach (var name in new[] { "Stage", "STAGE", "stage ", " stage", "stage\t", "" })
        {
            Assert.Null(await Jobs.GetCheckpointValueAsync(job, CheckpointKindCode.Variable, name, ct));
        }
    }

    [Fact(DisplayName = "A timer and a presence-only signal hold no value and answer null")]
    public async Task A_checkpoint_without_a_value_answers_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var fence = await ExecutingJobAsync(ct);
        await Services
            .GetRequiredService<ExecutionOperations>()
            .ArmOrConsumeJobSleepTimerAsync(new ArmOrConsumeJobSleepTimerCommand(fence, "t.wait", 60, null), ct);
        await RaiseAsync(fence.JobId, "s.present", JobPayload.None, ct);

        var page = await Jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(JobLookup.ById(fence.JobId)), ct);
        Assert.Equal(2, page.Items.Count);
        Assert.Null(await Jobs.GetCheckpointValueAsync(JobLookup.ById(fence.JobId), CheckpointKindCode.Timer, "t.wait", ct));
        Assert.Null(await Jobs.GetCheckpointValueAsync(JobLookup.ById(fence.JobId), CheckpointKindCode.Signal, "s.present", ct));
    }

    [Fact(DisplayName = "An unknown name, and a name read under a kind it is not stored under, answer null")]
    public async Task An_unknown_checkpoint_answers_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var fence = await ExecutingJobAsync(ct);
        await SetAsync(fence, CheckpointKindCode.Variable, "fetch.status", JobPayload.Json("stage-two"), ct);
        var job = JobLookup.ById(fence.JobId);

        Assert.NotNull(await Jobs.GetCheckpointValueAsync(job, CheckpointKindCode.Variable, "fetch.status", ct));
        Assert.Null(await Jobs.GetCheckpointValueAsync(job, CheckpointKindCode.Variable, "no-such-variable", ct));
        Assert.Null(await Jobs.GetCheckpointValueAsync(job, CheckpointKindCode.Signal, "fetch.status", ct));
        Assert.Null(await Jobs.GetCheckpointValueAsync(job, CheckpointKindCode.Variable, new string('a', 129), ct));
    }

    [Fact(DisplayName = "The scheduler's firing record holds a value and is not reachable, as the checkpoint list leaves it out")]
    public async Task The_firing_record_is_not_reachable()
    {
        var ct = TestContext.Current.CancellationToken;
        var fence = await ExecutingJobAsync(ct);
        await Db.ExecuteRawAsync(
            "INSERT INTO {schema}.checkpoints (job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value)"
                + " VALUES (@p_job_id, @p_kind, @p_name, @p_status, 1, @p_value)",
            ct,
            ("@p_job_id", fence.JobId),
            ("@p_kind", (byte)CheckpointKindCode.Firing),
            ("@p_name", "sys.firing"),
            ("@p_status", (byte)CheckpointStatusCode.Pending),
            ("@p_value", "{}"u8.ToArray())
        );

        Assert.Null(await Jobs.GetCheckpointValueAsync(JobLookup.ById(fence.JobId), CheckpointKindCode.Firing, "sys.firing", ct));
    }

    [Fact(DisplayName = "A job that does not exist answers null, by id and by ref")]
    public async Task A_missing_job_answers_null()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await Jobs.GetCheckpointValueAsync(JobLookup.ById(long.MaxValue), CheckpointKindCode.Variable, "fetch.status", ct));
        Assert.Null(await Jobs.GetCheckpointValueAsync(JobRef.New(), CheckpointKindCode.Variable, "fetch.status", ct));
    }

    [Fact(DisplayName = "A deduplication key reads its own namespace's job, and from another namespace reaches nothing")]
    public async Task A_deduplication_key_is_read_in_its_own_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("checkpoint-value");
        var enqueued = await Jobs.EnqueueAsync(new AddNumbers(1, 2), o => o.DeduplicationKey(key), ct);
        var fence = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);
        await SetAsync(fence, CheckpointKindCode.Variable, "fetch.status", JobPayload.Json("stage-two"), ct);

        Assert.NotNull(
            await Jobs.GetCheckpointValueAsync(
                JobLookup.ByDeduplicationKey(TestNamespace, key),
                CheckpointKindCode.Variable,
                "fetch.status",
                ct
            )
        );
        Assert.Null(
            await Jobs.GetCheckpointValueAsync(
                JobLookup.ByDeduplicationKey(Unique("other"), key),
                CheckpointKindCode.Variable,
                "fetch.status",
                ct
            )
        );
    }

    private async Task<ExecutionFence> ExecutingJobAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );
        return await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);
    }

    // Written as a handler writes it, under the fence of an attempt the job stands executing.
    private Task SetAsync(ExecutionFence fence, CheckpointKindCode kind, string name, JobPayload value, CancellationToken ct) =>
        CheckpointAccess.SetAsync(Services.GetRequiredService<ExecutionOperations>(), fence, kind, name, value, ct);

    private async Task RaiseAsync(long jobId, string name, JobPayload value, CancellationToken ct) =>
        await Services
            .GetRequiredService<SignalsOperations>()
            .RaiseJobSignalAsync(
                new RaiseJobSignalCheckpoint(
                    jobId,
                    CheckpointKindCode.Signal,
                    name,
                    value.Format.Id,
                    value.IsNone ? null : value.Data.ToArray(),
                    new JobControlInput("op", null, "seed")
                ),
                ct
            );
}
