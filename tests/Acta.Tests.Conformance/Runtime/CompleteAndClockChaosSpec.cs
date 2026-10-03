using Acta.Runtime;
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
    Contract = "Transient storage failures before and after CompleteExecution converge to one state, and DB/app clock skew is enforced at initialization."
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
        DisplayName = "A worker that stops while its completion is still failing leaves Executing with no success event, and reclaim reruns to a single Succeeded finish"
    )]
    public async Task A_worker_that_stops_mid_completion_retry_is_reclaimed_and_rerun()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. The only way the write is lost is the worker stopping while the store still refuses it.
        await ChaosSpecHelpers.StopWorkerMidCompletionRetryAsync(Runtime, _faults, enqueued, ct);

        Assert.Equal(JobStatusCode.Executing, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Empty(
            (await GetEventsByJobId.Run(Services, enqueued.JobId, ct)).Where(e =>
                e.EventCode == EventCode.JobExecutionFinished && e.ExecutionStatus == ExecutionStatusCode.Succeeded
            )
        );

        // --- 2. Reclaim and rerun converge to one Succeeded finish; the handler ran twice.
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        ChaosSpecHelpers.AssertRecoveryEvent(events, JobStatusCode.Executing, JobStatusCode.Ready);
        ChaosSpecHelpers.AssertSingleFinished(events, ExecutionStatusCode.Succeeded, JobStatusCode.Executing, JobStatusCode.Succeeded);
        Assert.Equal(2, ChaosProbes.CountingInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "A provider error before the completion commit is retried and the attempt lands once")]
    public async Task Provider_error_before_complete_commit_is_retried_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // A dropped connection on the write, not a crash: the same attempt repeats the write and lands,
        // so nothing waits for a lease to lapse, the handler ran once, and no recovery event exists.
        _faults.ThrowProviderErrorBeforeCompleteOnce();
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        ChaosSpecHelpers.AssertSingleFinished(events, ExecutionStatusCode.Succeeded, JobStatusCode.Executing, JobStatusCode.Succeeded);
        Assert.DoesNotContain(events, e => e.ExecutionStatus == ExecutionStatusCode.Orphaned);
    }

    [Fact(DisplayName = "A complete after-commit failure is reconciled by the repeat itself and is not rerun")]
    public async Task Sql_transient_failure_after_complete_commit_is_not_rerun()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. The write commits and its answer is lost on the way back. The repeat resubmits the same
        // compare-and-swap, finds the row already terminal, and reports that as a settled write rather
        // than an anomaly, which is the reconciliation the Retried flag exists for.
        _faults.ThrowAfterCompleteOnce();
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));

        // --- 2. A retry finds nothing to claim; the handler ran exactly once.
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        ChaosSpecHelpers.AssertSingleFinished(events, ExecutionStatusCode.Succeeded, JobStatusCode.Executing, JobStatusCode.Succeeded);
    }
}

[ConformanceSpec(
    "chaos.clock-skew-init",
    "Worker initialization enforces DB/app clock skew",
    Area = "Chaos",
    Contract = "Worker initialization fails when DB/app clocks differ beyond the fail threshold unless AllowClockSkew is set."
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
            o.RegisterSystemJobs = false;
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
        // Named from TestId, which makes it unique: TestNamespace already fills the 64-char limit with the id at
        // its end, so a cut-and-suffix form drops the id and reuses one namespace, and its workers, across runs.
        var allowedNamespace = ActaTestNames.CreateNamespace(GetType(), "allowed", TestId);
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
            o.RegisterSystemJobs = false;
            o.AllowClockSkew = allowClockSkew;
        });
        var faults = services.AddStoreFaultInjection();
        faults.SkewGetUtcNowBy(TimeSpan.FromSeconds(30));
        return services.BuildServiceProvider(validateScopes: true);
    }
}
