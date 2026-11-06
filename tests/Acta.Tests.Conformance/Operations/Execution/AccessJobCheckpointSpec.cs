using System.Data.Common;
using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>access_job_checkpoint</c>, the one CRUD call behind job variables and the progress checkpoint.</summary>
[ConformanceSpec(
    "access-job-checkpoint.crud",
    "A checkpoint is set, read, kept on first write, and deleted by one call",
    Area = "Execution",
    Contract = "A checkpoint keyed by job, kind, and name is overwritten by Set, kept by GetOrSet, removed by Delete, and never written without a payload."
)]
public sealed class AccessJobCheckpointSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private static readonly byte Json = JobPayloadFormat.Json.Id;

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "Set inserts a checkpoint and a later Set overwrites its value in place, one version on")]
    public async Task Set_inserts_then_overwrites_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        Assert.Equal(1, (await CheckpointAsync(AccessJobCheckpointAction.Set, job, "color", Value("red"), ct)).Found);
        var inserted = Assert.Single(await CheckpointsAsync(job, ct));
        Assert.Equal(CheckpointKindCode.Variable, inserted.Kind);
        Assert.Null(inserted.Status);
        Assert.Equal(Value("red"), inserted.Value);
        Assert.Equal(0, inserted.Version);

        Assert.Equal(1, (await CheckpointAsync(AccessJobCheckpointAction.Set, job, "color", Value("blue"), ct)).Found);

        var overwritten = Assert.Single(await CheckpointsAsync(job, ct));
        Assert.Equal(Value("blue"), overwritten.Value);
        Assert.Equal(1, overwritten.Version);
        var read = await CheckpointAsync(AccessJobCheckpointAction.Get, job, "color", null, ct);
        Assert.Equal(1, read.Found);
        Assert.Equal(Json, read.ValueFormatId);
        Assert.Equal(Value("blue"), read.Value);
        Assert.Equal(1, read.Version);
    }

    [Fact(DisplayName = "Get, Exists, and Delete answer not found for a checkpoint that was never written")]
    public async Task Missing_checkpoint_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        foreach (var action in new[] { AccessJobCheckpointAction.Get, AccessJobCheckpointAction.Exists, AccessJobCheckpointAction.Delete })
        {
            var row = await CheckpointAsync(action, job, "absent", null, ct);
            Assert.Equal(0, row.Found);
            Assert.Null(row.Value);
            Assert.Null(row.Version);
        }
    }

    [Fact(DisplayName = "Delete removes the checkpoint and reports it once, and a second Delete finds nothing")]
    public async Task Delete_removes_the_checkpoint_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        await CheckpointAsync(AccessJobCheckpointAction.Set, job, "doomed", Value("x"), ct);
        Assert.Equal(1, (await CheckpointAsync(AccessJobCheckpointAction.Exists, job, "doomed", null, ct)).Found);

        Assert.Equal(1, (await CheckpointAsync(AccessJobCheckpointAction.Delete, job, "doomed", null, ct)).Found);
        Assert.Equal(0, (await CheckpointAsync(AccessJobCheckpointAction.Delete, job, "doomed", null, ct)).Found);

        Assert.Equal(0, (await CheckpointAsync(AccessJobCheckpointAction.Exists, job, "doomed", null, ct)).Found);
        Assert.Empty(await CheckpointsAsync(job, ct));
    }

    [Fact(DisplayName = "GetOrSet stores the first value and hands it back unchanged to every later caller")]
    public async Task Get_or_set_keeps_the_first_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        var first = await CheckpointAsync(AccessJobCheckpointAction.GetOrSet, job, "seed", Value("first"), ct);
        var second = await CheckpointAsync(AccessJobCheckpointAction.GetOrSet, job, "seed", Value("second"), ct);

        Assert.Equal(1, first.Found);
        Assert.Equal(Value("first"), first.Value);
        Assert.Equal(0, first.Version);
        Assert.Equal(1, second.Found);
        Assert.Equal(Value("first"), second.Value);
        Assert.Equal(0, second.Version);
        var stored = Assert.Single(await CheckpointsAsync(job, ct));
        Assert.Equal(Value("first"), stored.Value);
        Assert.Equal(0, stored.Version);
    }

    [Fact(DisplayName = "The same name under another kind or another job is a different checkpoint")]
    public async Task Checkpoints_are_keyed_by_job_kind_and_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var other = await EnqueueAsync(ct);
        await CheckpointAsync(AccessJobCheckpointAction.Set, job, "shared", Value("variable"), ct);

        Assert.Equal(0, (await CheckpointAsync(AccessJobCheckpointAction.Exists, other, "shared", null, ct)).Found);
        Assert.Equal(
            0,
            (
                await Execution.AccessJobCheckpointAsync(
                    Command(AccessJobCheckpointAction.Exists, job, "shared", null, CheckpointKindCode.Progress),
                    ct
                )
            ).Found
        );

        await Execution.AccessJobCheckpointAsync(
            Command(AccessJobCheckpointAction.Set, job, "shared", Value("progress"), CheckpointKindCode.Progress),
            ct
        );

        Assert.Equal(Value("variable"), (await CheckpointAsync(AccessJobCheckpointAction.Get, job, "shared", null, ct)).Value);
        Assert.Equal(2, (await CheckpointsAsync(job, ct)).Count);
    }

    [Fact(DisplayName = "A write without a payload is rejected and stores nothing")]
    public async Task Write_without_a_payload_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        await Assert.ThrowsAnyAsync<DbException>(() =>
            Execution.AccessJobCheckpointAsync(
                new AccessJobCheckpointCommand(AccessJobCheckpointAction.Set, job, CheckpointKindCode.Variable, "empty", 0, null),
                ct
            )
        );
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Execution.AccessJobCheckpointAsync(
                new AccessJobCheckpointCommand(AccessJobCheckpointAction.GetOrSet, job, CheckpointKindCode.Variable, "empty", Json, null),
                ct
            )
        );

        Assert.Empty(await CheckpointsAsync(job, ct));
    }

    // A job staged Executing, as a claim leaves it, so the checkpoint writes pass the execution fence.
    private async Task<ExecutionFence> EnqueueAsync(CancellationToken ct) =>
        await ExecutionFenceStaging.ExecutingAsync(
            Db,
            (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct)).JobId,
            ct
        );

    private static byte[] Value(string text) => Encoding.UTF8.GetBytes($"\"{text}\"");

    private static AccessJobCheckpointCommand Command(
        AccessJobCheckpointAction action,
        ExecutionFence job,
        string name,
        byte[]? value,
        CheckpointKindCode kind = CheckpointKindCode.Variable
    ) => new(action, job, kind, name, value is null ? (byte)0 : Json, value);

    private Task<AccessJobCheckpointResponse> CheckpointAsync(
        AccessJobCheckpointAction action,
        ExecutionFence job,
        string name,
        byte[]? value,
        CancellationToken ct
    ) => Execution.AccessJobCheckpointAsync(Command(action, job, name, value), ct);

    private Task<IReadOnlyList<CheckpointEntity>> CheckpointsAsync(ExecutionFence job, CancellationToken ct)
    {
        var jobId = job.JobId;
        return Db.From<CheckpointEntity>().Where(c => c.JobId == jobId).ToListAsync(ct);
    }
}
