using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

[ConformanceSpec(
    "testing.test-host-helpers",
    "The test host's fast-forward helpers make only the named job's wait due",
    Area = "Testing",
    Contract = "IActaTestHost makes a parked timer, step retry, bounded wait, child wait, or group wait due at once, and refuses a wait that has no deadline to move.",
    Arrange = "An ActaTestHost is started for TestJobsManifest in an isolated namespace.",
    Act = "Jobs park on a timer, a step retry, a bounded signal wait, a child wait, and a group wait, and the host's Force helpers make each due.",
    Assert = "Each job runs on to Succeeded with its slot settled, and the helpers throw for an unbounded wait and for a job without a group."
)]
public abstract class TestHostHelpersSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private const int MaxTicks = 20;

    [Fact(DisplayName = "The timer and step-retry helpers make the parked job due")]
    public async Task Timer_and_step_retry_helpers_make_the_job_due()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        var sleep = await host.Jobs.EnqueueAsync(TestJobsManifest.JobSleepBasic, ct: ct);
        Assert.Equal(ActaRunOutcome.Rearmed, await host.RunOnceAsync(sleep, ct));
        var timer = Assert.Single(await CheckpointsAsync(host, sleep.JobId, JobCheckpointKindCode.Timer, ct));
        Assert.Equal(("nap", JobCheckpointStatusCode.Pending), (timer.Name, timer.Status));

        await host.ForceTimerDueAsync(sleep.JobId, ct: ct);
        await RunUntilAsync(host, sleep, JobStatusCode.Succeeded, ct);
        Assert.Equal(
            JobCheckpointStatusCode.Consumed,
            Assert.Single(await CheckpointsAsync(host, sleep.JobId, JobCheckpointKindCode.Timer, ct)).Status
        );

        var step = await host.Jobs.EnqueueAsync(TestJobsManifest.JobStepRetry, ct: ct);
        Assert.Equal(ActaRunOutcome.Rearmed, await host.RunOnceAsync(step, ct));
        Assert.Equal(JobStepStatusCode.Pending, (await StepAsync(host, step.JobId, "flaky", ct)).Status);

        await host.ForceStepRetryDueAsync(step.JobId, "flaky", ct);
        await RunUntilAsync(host, step, JobStatusCode.Succeeded, ct);
        Assert.Equal(JobStepStatusCode.Succeeded, (await StepAsync(host, step.JobId, "flaky", ct)).Status);
    }

    [Fact(DisplayName = "The wait-timeout helper expires a bounded wait and rejects an unbounded one")]
    public async Task Wait_timeout_helper_expires_a_bounded_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        var bounded = await host.Jobs.EnqueueAsync(TestJobsManifest.JobTryWaitSignalTimeout, new NoInput(), ct: ct);
        Assert.NotNull((await RunUntilWaitingAsync(host, bounded, "go", ct)).DueAtUtc);

        await host.ForceWaitTimeoutDueAsync(bounded.JobId, "go", ct);
        await RunUntilAsync(host, bounded, JobStatusCode.Succeeded, ct);
        Assert.Equal(
            JobCheckpointStatusCode.Expired,
            Assert.Single(await CheckpointsAsync(host, bounded.JobId, JobCheckpointKindCode.Signal, ct)).Status
        );

        // An unbounded wait has no expiration to move, so the helper must say so rather than no-op into
        // a test that reads as passing.
        var unbounded = await host.Jobs.EnqueueAsync(TestJobsManifest.JobWaitSignal, ct: ct);
        await RunUntilWaitingAsync(host, unbounded, "go", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ForceWaitTimeoutDueAsync(unbounded.JobId, ct: ct));
    }

    [Fact(DisplayName = "The wait-timeout helper expires a bounded child wait by its latch name")]
    public async Task Wait_timeout_helper_expires_a_child_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        var parent = await host.Jobs.EnqueueAsync(TestJobsManifest.JobParentTryWaitChild, new TryWaitChildStart("job-wait-signal"), ct: ct);
        Assert.Equal(ActaRunOutcome.Rearmed, await host.RunOnceAsync(parent, ct));

        // The helper reads the awaited slot by kind, so a child latch stages exactly like a signal does;
        // its name is the framework-owned sys.child key rather than anything the handler chose.
        var latch = Assert.Single(await CheckpointsAsync(host, parent.JobId, JobCheckpointKindCode.ChildLatch, ct));
        Assert.NotNull(latch.DueAtUtc);

        await host.ForceWaitTimeoutDueAsync(parent.JobId, latch.Name, ct);
        await RunUntilAsync(host, parent, JobStatusCode.Succeeded, ct);

        Assert.True((await host.Jobs.GetResultAsync<ChildWaitReport>(JobLookup.ById(parent.JobId), ct))!.TimedOut);
        Assert.Equal(
            JobCheckpointStatusCode.Expired,
            Assert.Single(await CheckpointsAsync(host, parent.JobId, JobCheckpointKindCode.ChildLatch, ct)).Status
        );
    }

    [Fact(DisplayName = "The group wait-timeout helper expires a whole group and rejects a job without one")]
    public async Task Group_wait_timeout_helper_expires_a_group()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        var parent = await host.Jobs.EnqueueAsync(
            TestJobsManifest.JobParentTryWaitChildren,
            new ChildGroupStart(["job-wait-signal", "job-wait-signal"]),
            ct: ct
        );
        Assert.Equal(ActaRunOutcome.Rearmed, await host.RunOnceAsync(parent, ct));

        // A group spends one stored deadline, so the helper takes no wait name; rewinding one latch by
        // hand would leave the deadline in the future and let the second member start over.
        await host.ForceGroupWaitTimeoutDueAsync(parent.JobId, ct);
        await RunUntilAsync(host, parent, JobStatusCode.Succeeded, ct);

        var report = (await host.Jobs.GetResultAsync<ChildGroupReport>(JobLookup.ById(parent.JobId), ct))!;
        Assert.True(report.TimedOut);
        Assert.All(report.Children, c => Assert.True(c.TimedOut));

        // A job that never armed a group wait stores no deadline, so the helper must say so rather than
        // no-op into a test that reads as passing.
        var single = await host.Jobs.EnqueueAsync(TestJobsManifest.JobWaitSignal, ct: ct);
        await RunUntilWaitingAsync(host, single, "go", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ForceGroupWaitTimeoutDueAsync(single.JobId, ct));
    }

    private static async Task RunUntilAsync(IActaTestHost host, JobEnqueueOutcome job, JobStatusCode expected, CancellationToken ct)
    {
        for (var tick = 0; tick < MaxTicks; tick++)
        {
            if (await host.Jobs.GetStatusAsync(JobLookup.ById(job.JobId), ct) == expected)
            {
                return;
            }
            await host.RunOnceAsync(job, ct);
        }
        Assert.Equal(expected, await host.Jobs.GetStatusAsync(JobLookup.ById(job.JobId), ct));
    }

    private static async Task<JobCheckpoint> RunUntilWaitingAsync(
        IActaTestHost host,
        JobEnqueueOutcome job,
        string signal,
        CancellationToken ct
    )
    {
        for (var tick = 0; ; tick++)
        {
            var waiting = (await CheckpointsAsync(host, job.JobId, JobCheckpointKindCode.Signal, ct)).SingleOrDefault(c =>
                c.Name == signal
            );
            if (waiting is { Status: JobCheckpointStatusCode.Pending } || tick == MaxTicks)
            {
                Assert.True(waiting is { Status: JobCheckpointStatusCode.Pending }, $"Job {job.JobId} never waited for signal '{signal}'.");
                return waiting;
            }
            await host.RunOnceAsync(job, ct);
        }
    }

    private static async Task<IReadOnlyList<JobCheckpoint>> CheckpointsAsync(
        IActaTestHost host,
        long jobId,
        JobCheckpointKindCode kind,
        CancellationToken ct
    ) =>
        await host
            .Services.GetRequiredService<IDbSession>()
            .From<JobCheckpoint>()
            .Where(c => c.JobId == jobId && c.Kind == kind)
            .ToListAsync(ct);

    private static async Task<JobStep> StepAsync(IActaTestHost host, long jobId, string name, CancellationToken ct) =>
        Assert.IsType<JobStep>(
            await host
                .Services.GetRequiredService<IDbSession>()
                .From<JobStep>()
                .Where(s => s.JobId == jobId && s.Name == name)
                .SingleOrDefaultAsync(ct)
        );

    private Task<IActaTestHost> StartHostAsync(CancellationToken ct) =>
        ActaTestHost.StartAsync(
            (j, schema) =>
            {
                Fixture.ApplyProvider(j, schema);
                j.Run<TestJobsManifest>(TestNamespace, ownerTeam: "test", description: GetType().FullName);
            },
            new ActaTestHostOptions
            {
                Schema = Schema.SchemaName,
                ConfigureServices = services => services.Configure<JobsOptions>(o => o.RegisterSystemJobs = false),
            },
            ct
        );
}
