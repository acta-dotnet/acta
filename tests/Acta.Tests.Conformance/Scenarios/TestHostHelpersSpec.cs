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
    Contract = "IActaTestHost makes a parked timer, bounded wait, or schedule due at once, admits a due schedule in one pass, and refuses a wait with no deadline."
)]
[Deploys(
    "job-parent-try-wait-child",
    "job-parent-try-wait-children",
    "job-sleep-basic",
    "job-try-wait-signal-timeout",
    "job-wait-signal",
    "recurring-ping"
)]
public sealed class TestHostHelpersSpec : ActaTestBase<ConformanceFixture>
{
    private const int MaxTicks = 20;

    [Fact(DisplayName = "The timer helper makes the parked job due")]
    public async Task Timer_helper_makes_the_job_due()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        var sleep = await host.Jobs.EnqueueAsync(TestJobsManifest.JobSleepBasic(), ct: ct);
        Assert.Equal(ActaRunStatus.Rearmed, await host.RunOnceAsync(sleep, ct));
        var timer = Assert.Single(await CheckpointsAsync(host, sleep.JobId, CheckpointKindCode.Timer, ct));
        Assert.Equal(("nap", CheckpointStatusCode.Pending), (timer.CheckpointName, timer.Status));

        // Every helper takes the job as IJobs does: the enqueue's handle, its ref, or a lookup.
        await host.ForceTimerDueAsync(sleep.JobRef, ct: ct);
        await RunUntilAsync(host, sleep, JobStatusCode.Succeeded, ct);
        Assert.Equal(
            CheckpointStatusCode.Consumed,
            Assert.Single(await CheckpointsAsync(host, sleep.JobId, CheckpointKindCode.Timer, ct)).Status
        );
    }

    [Fact(DisplayName = "A helper given a ref no job carries throws instead of driving nothing")]
    public async Task A_ref_no_job_carries_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);
        var missing = JobRef.New();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunOnceAsync(missing, ct));

        Assert.Equal($"Job {missing} does not exist; enqueue it before driving it.", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ForceJobDueAsync(missing, ct));
    }

    [Fact(DisplayName = "The wait-timeout helper expires a bounded wait and rejects an unbounded one")]
    public async Task Wait_timeout_helper_expires_a_bounded_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        var bounded = await host.Jobs.EnqueueAsync(TestJobsManifest.JobTryWaitSignalTimeout(), ct: ct);
        Assert.NotNull((await RunUntilWaitingAsync(host, bounded, "go", ct)).DueAtUtc);

        await host.ForceWaitTimeoutDueAsync(bounded, "go", ct);
        await RunUntilAsync(host, bounded, JobStatusCode.Succeeded, ct);
        Assert.Equal(
            CheckpointStatusCode.Expired,
            Assert.Single(await CheckpointsAsync(host, bounded.JobId, CheckpointKindCode.Signal, ct)).Status
        );

        // An unbounded wait has no expiration to move, so the helper must say so rather than no-op into
        // a test that reads as passing.
        var unbounded = await host.Jobs.EnqueueAsync(TestJobsManifest.JobWaitSignal(), ct: ct);
        await RunUntilWaitingAsync(host, unbounded, "go", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ForceWaitTimeoutDueAsync(unbounded, ct: ct));
    }

    [Fact(DisplayName = "The wait-timeout helper expires a bounded child wait by its latch name")]
    public async Task Wait_timeout_helper_expires_a_child_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        var parent = await host.Jobs.EnqueueAsync(TestJobsManifest.JobParentTryWaitChild(new TryWaitChildStart("job-wait-signal")), ct: ct);
        Assert.Equal(ActaRunStatus.Rearmed, await host.RunOnceAsync(parent, ct));

        // The helper reads the awaited checkpoint by kind, so a child latch stages exactly like a signal does;
        // its name is the framework-owned sys.child key rather than anything the handler chose.
        var latch = Assert.Single(await CheckpointsAsync(host, parent.JobId, CheckpointKindCode.ChildLatch, ct));
        Assert.NotNull(latch.DueAtUtc);

        await host.ForceWaitTimeoutDueAsync(parent, latch.CheckpointName, ct);
        await RunUntilAsync(host, parent, JobStatusCode.Succeeded, ct);

        Assert.True((await host.Jobs.GetResultAsync<ChildWaitReport>(JobLookup.ById(parent.JobId), ct))!.TimedOut);
        Assert.Equal(
            CheckpointStatusCode.Expired,
            Assert.Single(await CheckpointsAsync(host, parent.JobId, CheckpointKindCode.ChildLatch, ct)).Status
        );
    }

    [Fact(DisplayName = "The group wait-timeout helper expires a whole group and rejects a job without one")]
    public async Task Group_wait_timeout_helper_expires_a_group()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        var parent = await host.Jobs.EnqueueAsync(
            TestJobsManifest.JobParentTryWaitChildren(new ChildGroupStart(["job-wait-signal", "job-wait-signal"])),
            ct: ct
        );
        Assert.Equal(ActaRunStatus.Rearmed, await host.RunOnceAsync(parent, ct));

        // A group spends one stored deadline, so the helper takes no wait name; rewinding one latch by
        // hand would leave the deadline in the future and let the second member start over.
        await host.ForceGroupWaitTimeoutDueAsync(parent, ct);
        await RunUntilAsync(host, parent, JobStatusCode.Succeeded, ct);

        var report = (await host.Jobs.GetResultAsync<ChildGroupReport>(JobLookup.ById(parent.JobId), ct))!;
        Assert.True(report.TimedOut);
        Assert.All(report.Children, c => Assert.True(c.TimedOut));

        // A job that never armed a group wait stores no deadline, so the helper must say so rather than
        // no-op into a test that reads as passing.
        var single = await host.Jobs.EnqueueAsync(TestJobsManifest.JobWaitSignal(), ct: ct);
        await RunUntilWaitingAsync(host, single, "go", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ForceGroupWaitTimeoutDueAsync(single, ct));
    }

    /// <summary>
    /// The host runs no admitter, so a scheduled job's slot stays Idle until a test forces its schedule due and
    /// runs one admission pass; the pass is what moves the slot to Ready, and a run settles it back to Idle.
    /// </summary>
    [Fact(DisplayName = "The schedule helpers make a slot's schedule due and admit its firing")]
    public async Task Schedule_helpers_make_the_slot_due_and_admit_it()
    {
        var ct = TestContext.Current.CancellationToken;
        // The admitter's clock stands still, so only the forced schedule comes due: on the real clock, a five-minute
        // boundary inside the fact also admits the manifest's other schedules.
        await using var host = await StartHostAsync(ct, FakeClock.AtRealMinute());
        // The lookup canonicalizes like every schedule verb, so the name's casing does not matter.
        var lookup = new ScheduleLookup(TestNamespace, "recurring-ping", "Every-5-Minutes");
        var slot = (await host.Operations.Schedules.GetAsync(lookup, ct))!.JobRef!.Value;

        Assert.Equal(JobStatusCode.Idle, (await host.Jobs.GetStatusAsync(slot, ct))?.Status);
        Assert.Equal(0, (await host.AdmitOnceAsync(TestNamespace, ct)).Admitted);

        await host.ForceScheduleDueAsync(lookup, ct);
        Assert.Equal(1, (await host.AdmitOnceAsync(TestNamespace, ct)).Admitted);
        Assert.Equal(JobStatusCode.Ready, (await host.Jobs.GetStatusAsync(slot, ct))?.Status);

        Assert.Equal(ActaRunStatus.Completed, await host.RunOnceAsync(slot, ct));
        var settled = (await host.Jobs.GetAsync(slot, ct))!;
        Assert.Equal((JobStatusCode.Idle, 1), (settled.Status, settled.ExecutionNumber));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.ForceScheduleDueAsync(new ScheduleLookup(TestNamespace, "recurring-ping", "no-such-schedule"), ct)
        );
    }

    private static async Task RunUntilAsync(IActaTestHost host, EnqueueJobResponse job, JobStatusCode expected, CancellationToken ct)
    {
        for (var tick = 0; tick < MaxTicks; tick++)
        {
            if ((await host.Jobs.GetStatusAsync(JobLookup.ById(job.JobId), ct))?.Status == expected)
            {
                return;
            }
            await host.RunOnceAsync(job, ct);
        }
        Assert.Equal(expected, (await host.Jobs.GetStatusAsync(JobLookup.ById(job.JobId), ct))?.Status);
    }

    private static async Task<CheckpointEntity> RunUntilWaitingAsync(
        IActaTestHost host,
        EnqueueJobResponse job,
        string signal,
        CancellationToken ct
    )
    {
        for (var tick = 0; ; tick++)
        {
            var waiting = (await CheckpointsAsync(host, job.JobId, CheckpointKindCode.Signal, ct)).SingleOrDefault(c =>
                c.CheckpointName == signal
            );
            if (waiting is { Status: CheckpointStatusCode.Pending } || tick == MaxTicks)
            {
                Assert.True(waiting is { Status: CheckpointStatusCode.Pending }, $"Job {job.JobId} never waited for signal '{signal}'.");
                return waiting;
            }
            await host.RunOnceAsync(job, ct);
        }
    }

    private static async Task<IReadOnlyList<CheckpointEntity>> CheckpointsAsync(
        IActaTestHost host,
        long jobId,
        CheckpointKindCode kind,
        CancellationToken ct
    ) =>
        await host
            .Services.GetRequiredService<IDbSession>()
            .From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == kind)
            .ToListAsync(ct);

    private Task<IActaTestHost> StartHostAsync(CancellationToken ct, IActaClock? clock = null) =>
        ActaTestHost.StartAsync(
            (j, schema) =>
            {
                Fixture.ApplyProvider(j, schema);
                j.Run<JobsOf<TestHostHelpersSpec>>(TestNamespace, ownerTeam: "test", description: GetType().FullName);
                j.Reference<TestJobsManifest>(TestNamespace);
            },
            new ActaTestHostOptions
            {
                Schema = Schema.SchemaName,
                ConfigureServices = services =>
                {
                    services.Configure<JobsOptions>(o => o.RunSystemJobs = false);
                    if (clock is not null)
                    {
                        services.AddSingleton(clock);
                    }
                },
            },
            ct
        );
}
