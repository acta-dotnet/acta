using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for GetJobCheckpoints: a job's slots list by kind then name with state and value intact.</summary>
[ConformanceSpec(
    "job-payload-reads.checkpoints",
    "GetJobCheckpoints lists a job's slots by kind then name",
    Area = "Reads",
    Contract = "GetJobCheckpoints lists a job's slots by kind then name with state and value round-tripped, and nothing for a job without slots or a missing id."
)]
public sealed class GetJobCheckpointsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "GetJobCheckpoints lists variable and signal slots with kind, state, and value round-tripped")]
    public async Task Checkpoints_round_trip_kind_state_and_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var execution = Services.GetRequiredService<Acta.Runtime.Execution>();

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );

        var variableValue = JobPayload.Text("stage-two");
        await CheckpointSlot.SetAsync(execution, enqueued.JobId, JobCheckpointKindCode.Variable, "fetch.status", variableValue, ct);

        var signalValue = JobPayload.Json(new AddNumbers(2, 3));
        await Services
            .GetRequiredService<Acta.Runtime.Signals>()
            .RaiseSignalAsync(
                new SignalSlotRaise(
                    enqueued.JobId,
                    JobCheckpointKindCode.Signal,
                    "s.ready",
                    signalValue.Format.Id,
                    signalValue.Data.ToArray(),
                    new JobControlInput(new JobControlActor(ActorCode.Operator, "op"), JobEventReasonCode.JobControlManual, "seed")
                ),
                ct
            );

        var items = await Services.GetRequiredService<GetJobCheckpointsHandler>().ReadAsync(enqueued.JobId, ct);
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

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );

        Assert.Empty(await Services.GetRequiredService<GetJobCheckpointsHandler>().ReadAsync(enqueued.JobId, ct));
        Assert.Empty(await Services.GetRequiredService<GetJobCheckpointsHandler>().ReadAsync(-123_456_789L, ct));
    }
}
