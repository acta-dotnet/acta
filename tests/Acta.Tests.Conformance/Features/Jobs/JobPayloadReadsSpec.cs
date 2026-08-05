using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for the job payload reads: <c>GetJobInput</c> returns the stored input payload and
/// format (null when no row matches), and <c>GetJobCheckpoints</c> lists a job's durable slots ordered
/// by kind then name with the value payload and state round-tripped.
/// </summary>
[ConformanceSpec(
    "job-payload-reads.input-and-checkpoints",
    "GetJobInput reads stored input and GetJobCheckpoints lists a job's slots",
    Area = "Reads",
    Contract = "GetJobInput returns a job's stored input payload and format or null when no row matches, and GetJobCheckpoints lists slots ordered by kind then name.",
    Arrange = "A job is enqueued with a known input, and a separate job is seeded with a variable and a signal checkpoint.",
    Act = "GetJobInput reads the enqueued input and a missing id, and GetJobCheckpoints reads the seeded and an empty job.",
    Assert = "Input equals what was enqueued and is null for a missing id, and the checkpoint list round-trips kind, state, and value and is empty for a job with none."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobInputAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobCheckpointsAsync))]
public abstract class JobPayloadReadsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "GetJobInput returns the payload the job was enqueued with")]
    public async Task Input_readable_after_enqueue_equals_enqueued()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IJobStore>();

        var input = JobPayload.Json(new AddNumbers(4, 9));
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", input), ct);

        var record = await store.GetJobInputAsync(enqueued.JobId, ct);
        Assert.NotNull(record);
        Assert.Equal(input.Format.Id, record!.FormatId);
        Assert.Equal(input.Data.ToArray(), record.Data.ToArray());

        var payload = await Jobs.GetInputAsync(enqueued, ct);
        Assert.NotNull(payload);
        Assert.Equal(input.Format.Id, payload!.Value.Format.Id);
        Assert.Equal(input.Data.ToArray(), payload.Value.Data.ToArray());
    }

    [Fact(DisplayName = "GetJobInput returns null when no job row matches the id")]
    public async Task Input_null_for_missing_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IJobStore>();

        var record = await store.GetJobInputAsync(-987_654_321L, ct);
        Assert.Null(record);
    }

    [Fact(DisplayName = "GetJobCheckpoints lists variable and signal slots with kind, state, and value round-tripped")]
    public async Task Checkpoints_round_trip_kind_state_and_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IJobStore>();
        var execution = Services.GetRequiredService<IExecutionStore>();

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );

        var variableValue = JobPayload.Text("stage-two");
        await CheckpointSlot.SetAsync(execution, enqueued.JobId, JobCheckpointKindCode.Variable, "fetch.status", variableValue, ct);

        var signalValue = JobPayload.Json(new AddNumbers(2, 3));
        await Services
            .GetRequiredService<ISignalStore>()
            .RaiseSignalAsync(
                new RaiseSignalCommand(
                    enqueued.JobId,
                    JobCheckpointKindCode.Signal,
                    "s.ready",
                    signalValue.Format.Id,
                    signalValue.Data.ToArray(),
                    new JobControlInput(new JobControlActor(JobActorCode.Operator, "op"), JobEventReasonCode.JobControlManual, "seed")
                ),
                ct
            );

        var items = await store.GetJobCheckpointsAsync(enqueued.JobId, ct);
        Assert.Equal(2, items.Count);

        // Ordered by kind (Variable=10 before Signal=20) then name.
        var variable = items[0];
        Assert.Equal(JobCheckpointKindCode.Variable, variable.Kind);
        Assert.Equal("fetch.status", variable.Name);
        Assert.Null(variable.Status);
        Assert.NotNull(variable.Value);
        Assert.Equal(variableValue.Format.Id, variable.Value!.Value.Format.Id);
        Assert.Equal(variableValue.Data.ToArray(), variable.Value.Value.Data.ToArray());

        var signal = items[1];
        Assert.Equal(JobCheckpointKindCode.Signal, signal.Kind);
        Assert.Equal("s.ready", signal.Name);
        Assert.Equal(JobCheckpointStatusCode.Set, signal.Status);
        Assert.NotNull(signal.Value);
        Assert.Equal(signalValue.Data.ToArray(), signal.Value!.Value.Data.ToArray());
    }

    [Fact(DisplayName = "GetJobCheckpoints returns an empty list for a job with no slots and a missing id")]
    public async Task Checkpoints_empty_when_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IJobStore>();

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );

        Assert.Empty(await store.GetJobCheckpointsAsync(enqueued.JobId, ct));
        Assert.Empty(await store.GetJobCheckpointsAsync(-123_456_789L, ct));
    }
}
