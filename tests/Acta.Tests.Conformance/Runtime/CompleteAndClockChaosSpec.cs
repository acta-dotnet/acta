using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

[ConformanceSpec(
    "chaos.complete-clock",
    "CompleteExecution transient failures and DB clock skew are explicit",
    Area = "Chaos",
    Contract = "Transient storage failures before and after CompleteExecution converge to one state, and DB/app clock skew is enforced at initialization.",
    Arrange = "A counting probe job is enqueued with store fault injection armed to fail CompleteExecution once, before or after its commit.",
    Act = "The runtime runs the job through the injected completion failure, and the before-commit case is then reclaimed and rerun.",
    Assert = "A before-commit failure reruns to exactly one Succeeded finish while an after-commit failure leaves the job Done with no rerun."
)]
public abstract class CompleteAndClockChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
    }

    [Fact(
        DisplayName = "A complete before-commit failure leaves Executing with no success event, and reclaim reruns to a single Succeeded finish"
    )]
    public async Task Sql_transient_failure_before_complete_commit_is_reclaimed_and_rerun()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. CompleteExecution fails before commit; the job stays Executing with no success event.
        _faults.ThrowBeforeCompleteOnce();
        await Assert.ThrowsAsync<TimeoutException>(() => Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Executing, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Empty(
            (await GetEventsByJobId.Run(Services, enqueued.JobId, ct)).Where(e =>
                e.JobEventCode == JobEventCode.JobExecutionFinished && e.ExecutionStatus == ExecutionStatusCode.Succeeded
            )
        );

        // --- 2. Reclaim and rerun converge to one Succeeded finish; the handler ran twice.
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        ChaosSpecHelpers.AssertRecoveryEvent(events, JobStatusCode.Executing, JobStatusCode.Ready);
        ChaosSpecHelpers.AssertSingleFinished(events, ExecutionStatusCode.Succeeded, JobStatusCode.Executing, JobStatusCode.Done);
        Assert.Equal(2, ChaosProbes.CountingInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "A complete after-commit failure leaves Done with one success event and is not rerun")]
    public async Task Sql_transient_failure_after_complete_commit_is_not_rerun()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. CompleteExecution commits, then the post-commit failure surfaces; the job stays Done.
        _faults.ThrowAfterCompleteOnce();
        await Assert.ThrowsAsync<TimeoutException>(() => Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Done, await Jobs.GetStatusAsync(enqueued, ct));

        // --- 2. A retry finds nothing to claim; the handler ran exactly once.
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        ChaosSpecHelpers.AssertSingleFinished(events, ExecutionStatusCode.Succeeded, JobStatusCode.Executing, JobStatusCode.Done);
    }
}

[ConformanceSpec(
    "chaos.clock-skew-init",
    "Worker initialization enforces DB/app clock skew",
    Area = "Chaos",
    Contract = "Worker initialization fails when DB/app clocks differ beyond the fail threshold unless AllowClockSkew is set.",
    Arrange = "A worker runtime is configured with a 30-second injected GetUtcNow skew and AllowClockSkew off, alongside a second runtime with AllowClockSkew on.",
    Act = "InitializeAsync is called on both skewed runtimes.",
    Assert = "The default runtime is rejected with a clock-skew error and records no worker while the AllowClockSkew runtime initializes an Active worker."
)]
public abstract class ClockSkewInitializationChaosSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TestJobs.TestJobsManifest>(testNamespace, ownerTeam: "test", description: GetType().FullName);
        });
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterFrameworkJobs = false;
            o.AllowClockSkew = false;
        });
        _faults = services.AddStoreFaultInjection();
        _faults.SkewGetUtcNowBy(TimeSpan.FromSeconds(30));
    }

    protected override ValueTask AfterInitializeAsync() => ValueTask.CompletedTask;

    [Fact(DisplayName = "Clock skew is not silently ignored and the explicit AllowClockSkew override admits the same skew")]
    public async Task Clock_skew_blocks_or_allows_worker_initialization()
    {
        var ct = TestContext.Current.CancellationToken;

        // --- 1. The default path rejects a skewed clock and records no worker.
        var runtime = Services.GetServices<WorkerRuntime>().Single();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.InitializeAsync(ct));
        Assert.Contains("clock skew", ex.Message, StringComparison.OrdinalIgnoreCase);
        var namespaceRow = await Db.From<JobNamespace>().Where(n => n.Name == TestNamespace).SingleOrDefaultAsync(ct);
        if (namespaceRow is not null)
        {
            Assert.Empty(await Db.From<JobWorker>().Where(w => w.NamespaceId == namespaceRow.Id).ToListAsync(ct));
        }

        // --- 2. AllowClockSkew admits the same skew and records a running worker.
        // TestNamespace is already truncated to the 64-char namespaces.name limit, so a naive
        // suffix overflows. Append within a trimmed head to stay inside the bound.
        var allowedHead = TestNamespace.Length > 55 ? TestNamespace[..55] : TestNamespace;
        var allowedNamespace = allowedHead.TrimEnd('-') + "-allowed";
        await using var allowed = BuildClockSkewProvider(allowedNamespace, allowClockSkew: true);
        await allowed.GetServices<WorkerRuntime>().Single().InitializeAsync(ct);

        var allowedNs = await ChaosSpecHelpers.NamespaceIdAsync(Db, allowedNamespace, ct);
        var worker = Assert.Single(await Db.From<JobWorker>().Where(w => w.NamespaceId == allowedNs).ToListAsync(ct));
        Assert.Equal(WorkerStatusCode.Active, worker.Status);
    }

    private ServiceProvider BuildClockSkewProvider(string testNamespace, bool allowClockSkew)
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TestJobs.TestJobsManifest>(testNamespace, ownerTeam: "test", description: GetType().FullName);
        });
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterFrameworkJobs = false;
            o.AllowClockSkew = allowClockSkew;
        });
        var faults = services.AddStoreFaultInjection();
        faults.SkewGetUtcNowBy(TimeSpan.FromSeconds(30));
        return services.BuildServiceProvider(validateScopes: true);
    }
}
