using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for GetCheckpoints: a job's slots list by kind then name with state and value intact.</summary>
[ConformanceSpec(
    "job-payload-reads.checkpoints",
    "GetCheckpoints lists a job's slots by kind then name",
    Area = "Reads",
    Contract = "GetCheckpoints lists a job's slots by kind then name with state and value round-tripped, an empty set for a job without slots, and null for a missing id."
)]
public sealed class GetCheckpointsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "GetCheckpoints lists variable and signal slots with kind, state, and value round-tripped")]
    public async Task Checkpoints_round_trip_kind_state_and_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var execution = Services.GetRequiredService<ExecutionOperations>();

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );

        var variableValue = JobPayload.Text("stage-two");
        await CheckpointSlot.SetAsync(execution, enqueued.JobId, CheckpointKindCode.Variable, "fetch.status", variableValue, ct);

        var signalValue = JobPayload.Json(new AddNumbers(2, 3));
        await Services
            .GetRequiredService<SignalsOperations>()
            .RaiseSignalAsync(
                new RaiseSignalSlot(
                    enqueued.JobId,
                    CheckpointKindCode.Signal,
                    "s.ready",
                    signalValue.Format.Id,
                    signalValue.Data.ToArray(),
                    new JobControlInput("op", null, "seed")
                ),
                ct
            );

        var response = await Services.GetRequiredService<GetCheckpointsHandler>().ReadAsync(enqueued.JobId, ct);
        Assert.NotNull(response);
        var items = response.Items;
        Assert.Equal(2, items.Count);

        // Ordered by kind (Variable=10 before Signal=20) then name.
        var variable = items[0];
        Assert.Equal(CheckpointKindCode.Variable, variable.Kind);
        Assert.Equal("fetch.status", variable.Name);
        Assert.Null(variable.Status);
        Assert.NotNull(variable.Value);
        Assert.Equal(variableValue.Format.Id, variable.Value!.Value.Format.Id);
        Assert.Equal(variableValue.Data.ToArray(), variable.Value.Value.Data.ToArray());

        var signal = items[1];
        Assert.Equal(CheckpointKindCode.Signal, signal.Kind);
        Assert.Equal("s.ready", signal.Name);
        Assert.Equal(CheckpointStatusCode.Set, signal.Status);
        Assert.NotNull(signal.Value);
        Assert.Equal(signalValue.Data.ToArray(), signal.Value!.Value.Data.ToArray());
    }

    [Fact(DisplayName = "GetCheckpoints returns an empty set for a job with no slots and null for a missing id")]
    public async Task Checkpoints_empty_for_no_slots_and_null_for_a_missing_job()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );

        var handler = Services.GetRequiredService<GetCheckpointsHandler>();
        var none = await handler.ReadAsync(enqueued.JobId, ct);
        Assert.NotNull(none);
        Assert.Empty(none.Items);
        Assert.Null(await handler.ReadAsync(-123_456_789L, ct));
    }
}
