using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

[ConformanceSpec(
    "get-worker.by-id",
    "GetWorker returns one worker by id and null for an unknown id",
    Area = "Reads",
    Contract = "GetWorker returns the durable worker projection matching the supplied id and null when no row matches.",
    Arrange = "A worker is started with known host, version, process, and concurrency values.",
    Act = "GetWorker is called with the assigned id and then with an id that matches no row.",
    Assert = "The known worker preserves every durable identity and lifecycle field and the unknown id returns null."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.GetWorkerAsync))]
public abstract class GetWorkerSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A known worker id returns its durable detail projection")]
    public async Task Returns_worker_for_known_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerRef = WorkerRef.New();
        var started = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            "test",
            null,
            "detail-host",
            "deploy-detail",
            "engine-detail",
            ".NET detail",
            4242,
            12,
            ct,
            workerRef.Value
        );

        var worker = await Operations.Workers.GetAsync(workerRef, ct);

        Assert.NotNull(worker);
        Assert.Equal(started.WorkerId, worker!.WorkerId);
        Assert.Equal(TestNamespace, worker.JobNamespace);
        Assert.Equal("detail-host", worker.Host);
        Assert.Equal("deploy-detail", worker.DeploymentVersion);
        Assert.Equal("engine-detail", worker.EngineVersion);
        Assert.Equal(".NET detail", worker.DotnetVersion);
        Assert.Equal(4242, worker.ProcessId);
        Assert.Equal(12, worker.MaxConcurrency);
        Assert.True(worker.LastHeartbeatAtUtc >= worker.StartedAtUtc);
        Assert.True(worker.ModifiedAtUtc >= worker.StartedAtUtc);
    }

    [Fact(DisplayName = "An unknown worker id returns null")]
    public async Task Returns_null_for_unknown_id()
    {
        var worker = await Operations.Workers.GetAsync(WorkerRef.New(), TestContext.Current.CancellationToken);

        Assert.Null(worker);
    }
}
