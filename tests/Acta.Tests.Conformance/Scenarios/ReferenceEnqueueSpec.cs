using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for <c>IActaBuilder.Reference</c>: an enqueue-only host that References a manifest
/// resolves typed routes and enqueues, hosts no worker runtime, and the namespace's real worker
/// executes the row. Uses the <c>add-numbers</c> job registered under the per-test namespace by the
/// worker host (the test base).
/// </summary>
[ConformanceSpec(
    "typed-enqueue.reference",
    "A Reference-only host typed-enqueues without running a worker",
    Area = "Enqueue",
    Contract = "j.Reference<TManifest> feeds the typed route index without declaring a worker, so the host typed-enqueues and the namespace's Run worker completes it."
)]
public sealed class ReferenceEnqueueSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "Reference resolves typed routes and hosts no worker while the Run worker executes its enqueued rows")]
    public async Task Reference_only_host_typed_enqueues_and_the_worker_executes()
    {
        var ct = TestContext.Current.CancellationToken;

        // A separate enqueue-only provider against the same schema: Reference instead of Run.
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Reference<TestJobs.TestJobsManifest>(TestNamespace);
        });
        await using var referenceHost = services.BuildServiceProvider(validateScopes: true);

        Assert.Empty(referenceHost.GetServices<WorkerRuntime>());

        var refJobs = referenceHost.GetRequiredService<IJobs>();
        var outcome = await refJobs.EnqueueAsync(new AddNumbers(2, 3), o => o.DeduplicationKey("ref-typed-1"), ct);
        Assert.Equal(EnqueueJobAction.Inserted, outcome.Action);

        var again = await refJobs.EnqueueAsync(new AddNumbers(2, 3), o => o.DeduplicationKey("ref-typed-1"), ct);
        Assert.Equal(EnqueueJobAction.Deduplicated, again.Action);
        Assert.Equal(outcome.JobId, again.JobId);

        // The real worker (the test-base host) claims and completes the referenced enqueue.
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(outcome, ct));
        var result = await Jobs.GetResultAsync<AddNumbersResult>(outcome, ct);
        Assert.Equal(5, result!.Sum);
    }
}
