using Acta.Modules.Execution;
using Acta.Modules.Execution.Workers;
using Acta.Payloads;
using Acta.Testing;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for multi-worker-per-process registration. Three <c>j.Run(...)</c> calls in one process
/// register three workers - each owning its own namespace and manifest catalog - and each claims and runs
/// jobs only in its own namespace. Proves the per-worker fan-out (one runtime trio per <c>Run</c>).
/// </summary>
[ConformanceSpec(
    "multi-worker.registration",
    "Three Run calls register three workers isolated per namespace",
    Area = "Workers",
    Contract = "Three Run calls in one process register three workers each owning its own namespace and manifest catalog and each claims and runs jobs only in its namespace.",
    Arrange = "One process configures three j.Run calls, one per namespace.",
    Act = "Each namespace enqueues a job and its owning runtime runs one tick.",
    Assert = "Three workers are registered and each completes only its own namespace's job to Done."
)]
public abstract class MultiWorkerRegistrationSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private string[] Namespaces => [TestKey("mw-a"), TestKey("mw-b"), TestKey("mw-c")];

    private IJobs Jobs => Services.GetRequiredService<IJobs>();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            foreach (var ns in Namespaces)
            {
                j.Run<TestJobs.TestJobsManifest>(ns, ownerTeam: "test");
            }
        });
    }

    protected override async ValueTask AfterInitializeAsync()
    {
        foreach (var runtime in Services.GetServices<WorkerRuntime>())
        {
            await runtime.InitializeAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Three workers register, each owning one namespace and running jobs only in its own namespace")]
    public async Task ThreeWorkers_EachClaimAndRunOnlyInTheirOwnNamespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtimes = Services.GetServices<WorkerRuntime>().ToList();
        Assert.Equal(3, runtimes.Count);

        foreach (var ns in Namespaces)
        {
            var runtime = runtimes.Single(r => r.RegisteredNamespaceIds.ContainsKey(ns));
            var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(ns, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);

            Assert.Equal(RunOnceOutcome.Completed, await runtime.RunOnceAsync(enqueued, ct));
            Assert.Equal(JobStatusCode.Done, await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct));
        }
    }
}
