using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// The P0-02 golden-path spec. Runs the slice end-to-end against a provider:
///   1. Register the <c>add-numbers</c> definition (via <c>Runtime.InitializeAsync</c>).
///   2. Enqueue one <c>AddNumbers(2, 3)</c> via <c>IJobs.EnqueueAsync(JobEnqueueRequest)</c>.
///   3. <c>Runtime.RunOnceAsync</c> claims, executes, and completes the row.
///   4. Query snapshot / timeline / result and assert the full state.
/// Identical assertions run against SqlServer and Postgres via the provider one-liners under
/// <c>Acta.Tests.Conformance.SqlServer</c> / <c>Acta.Tests.Conformance.Postgres</c>.
/// </summary>
[ConformanceSpec(
    "golden-path.end-to-end",
    "A job registers, enqueues, claims, executes, persists and reads back",
    Area = "Execution",
    Contract = "A registered job enqueued through IJobs is claimed, executed and completed to Done with the canonical claim/start/finish timeline and a deserializable result.",
    Arrange = "The add-numbers job definition is registered in the test namespace.",
    Act = "One AddNumbers job is enqueued through IJobs and a single runtime tick claims, executes, and completes it.",
    Assert = "The job lands Done with the canonical Started then Finished timeline and a result that deserializes to the handler output."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.StartExecutionAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobResultAsync))]
[CoversStoreMethod(typeof(IEventStore), nameof(IEventStore.ListEventsAsync))]
public abstract class GoldenPathSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(
        DisplayName = "Job completes Done with a Started then Finished(Succeeded, Executing to Done) timeline and a result that deserializes to the handler output"
    )]
    public async Task End_to_end_golden_path_registers_enqueues_claims_executes_persists_and_queries()
    {
        var ct = TestContext.Current.CancellationToken;

        // --- 1. Definition is registered by InitializeAsync (already ran). Sanity-check.
        Assert.Contains(TestNamespace, Runtime.RegisteredNamespaceIds.Keys);

        // --- 2. Enqueue one AddNumbers(2, 3) through the public IJobs surface. Use the registered
        // serializer (same path the runtime uses on dispatch) so the test never drifts from
        // production semantics.
        var input = new AddNumbers(Left: 2, Right: 3);
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(input);

        var enqueueRequest = new JobEnqueueRequest(
            JobNamespace: TestNamespace,
            JobName: "add-numbers",
            Input: payload,
            DeduplicationKey: null,
            CorrelationKey: null,
            Priority: null
        );

        var enqueued = await Jobs.EnqueueAsync(enqueueRequest, ct);
        Assert.True(enqueued.JobId > 0);

        // --- 3. Run one tick. Should claim the row, invoke the handler, and complete with Done.
        var outcome = await Runtime.RunOnceAsync(enqueued, ct);
        Assert.Equal(RunOnceOutcome.Completed, outcome);

        // --- 4. Read the snapshot / timeline / result.

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(snapshot);
        Assert.Equal("add-numbers", snapshot!.JobName);
        Assert.Equal(TestNamespace, snapshot.JobNamespace);
        Assert.Equal(JobStatusCode.Done, snapshot.Status);

        var status = await Jobs.GetStatusAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Done, status);

        var jobId = enqueued.JobId;

        var events = await GetEventsByJobId.Run(Services, jobId, ct);
        var observedTypes = events.Select(e => e.JobEventCode).ToArray();
        Assert.Equal([JobEventCode.JobExecutionStarted, JobEventCode.JobExecutionFinished], observedTypes);

        var finishedEvent = events.Single(e => e.JobEventCode == JobEventCode.JobExecutionFinished);
        Assert.Equal(ExecutionStatusCode.Succeeded, finishedEvent.ExecutionStatus);
        Assert.Equal(JobStatusCode.Executing, finishedEvent.FromStatus);
        Assert.Equal(JobStatusCode.Done, finishedEvent.ToStatus);

        var result = await Services.GetRequiredService<IJobStore>().GetJobResultAsync(jobId, null, ct);
        Assert.NotNull(result);
        Assert.Equal(JobPayloadFormat.Json.Id, result!.Format.Id);
        var resultPayload = JobPayload.FromBytes(result.Format, result.Data.ToArray());
        var typedResult = serializers.Resolve(result.Format.Id).Deserialize<AddNumbersResult>(resultPayload);
        Assert.Equal(5, typedResult.Sum);
    }
}
