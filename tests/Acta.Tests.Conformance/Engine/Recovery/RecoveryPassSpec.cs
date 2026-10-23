using System.Diagnostics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Recovery;

/// <summary>
/// Crash recovery the way production reaches it: a worker's heartbeat call takes the namespace's recovery pass when it
/// is due, moving the due time one interval on, and that worker runs <see cref="RecoveryPass"/>. What this pins is the
/// gate - one taker per due pass, none before the interval - and the composition of one pass: it reclaims the
/// namespace's lease-expired jobs, backstops a child latch whose raise was lost to a crash, and publishes the two
/// wakeups that tell workers the reclaimed rows and the released parent are claimable now.
/// </summary>
[ConformanceSpec(
    "execution.recovery-pass",
    "The heartbeat takes each due recovery pass once",
    Area = "Recovery",
    Contract = "A heartbeat takes the namespace's recovery pass once per interval, and one pass reclaims lapsed leases, re-raises lost latches, and wakes workers."
)]
public sealed class RecoveryPassSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Claim with a negative TTL so the lease lands in the past, deterministically simulating a worker
    // that claimed then died long enough ago for its lease to lapse.
    private const int LeaseTtlSeconds = -5;

    private const int IntervalSeconds = 180;

    private readonly RecordingWakeup _wakeups = new();

    private int TestNamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Registered ahead of UseActa's TryAddSingleton, so the wakeups the pass publishes are read
        // from the same transport the runtime resolves.
        services.AddSingleton<IWorkerWakeup>(_wakeups);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A heartbeat takes a due pass and moves the next one an interval out; the next call does not take it")]
    public async Task A_due_pass_is_taken_once_per_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, TestNamespaceId, ct);
        await MakeDueAsync(ct);

        Assert.True((await BeatAsync(workerId, IntervalSeconds, ct)).RecoveryClaimed);
        var due = await RecoveryDueAsync(ct);
        Assert.NotNull(due);
        Assert.True(due > DateTime.UtcNow.AddSeconds(IntervalSeconds - 60), "the due time moved one interval out");

        Assert.False((await BeatAsync(workerId, IntervalSeconds, ct)).RecoveryClaimed);
        Assert.Equal(due, await RecoveryDueAsync(ct));
    }

    [Fact(DisplayName = "Two heartbeats at once take a due pass once")]
    public async Task Two_heartbeats_take_a_due_pass_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, TestNamespaceId, ct);
        await MakeDueAsync(ct);

        var answers = await Task.WhenAll(BeatAsync(workerId, IntervalSeconds, ct), BeatAsync(workerId, IntervalSeconds, ct));

        Assert.Single(answers, a => a.RecoveryClaimed);
    }

    [Fact(DisplayName = "A heartbeat from a worker that runs no recovery never takes the pass")]
    public async Task A_heartbeat_without_an_interval_takes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, TestNamespaceId, ct);
        await MakeDueAsync(ct);

        Assert.False((await BeatAsync(workerId, recoveryIntervalSeconds: null, ct)).RecoveryClaimed);
        Assert.Null(await RecoveryDueAsync(ct));
    }

    [Fact(
        DisplayName = "One recovery pass reclaims the stranded job, raises the latch its crashed child never set, and wakes both channels"
    )]
    public async Task One_pass_reclaims_releases_and_wakes()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, TestNamespaceId, ct);

        var stranded = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        Assert.Single(await Execution.ClaimJobAsync(TestNamespaceId, workerId, LeaseTtlSeconds, stranded, ct));

        // A parent Suspended on a child that landed terminal with its latch never set: the raise lost to
        // a crash between the two writes, which only the pass's backstop can repair.
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).ToListAsync(ct));
        await SetTerminalWithoutRaiseAsync(child.Id, ct);

        var before = _wakeups.Published.Count;
        await RunPassesUntilSettledAsync(stranded.JobId, parent.JobId, ct);

        var reclaimed = await ReadJobAsync(stranded.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.Null(reclaimed.LeasedByWorkerId);
        Assert.Equal(1, reclaimed.FailureCount);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Set, Assert.Single(await ReadSignalsAsync(parent.JobId, ct)).Status);

        var published = _wakeups.Published.Skip(before).Select(c => c.Kind).ToList();
        Assert.Contains(WorkerWakeupChannelKind.WorkerNamespace, published);
        Assert.Contains(WorkerWakeupChannelKind.AllWorkerNamespaces, published);
    }

    /// <summary>
    /// Runs passes until both repairs have landed. <c>reclaim_stuck_jobs</c> reads its stuck set with READPAST, so
    /// one pass can skip a row another spec momentarily holds in the shared schema - the same reason
    /// <see cref="RecoverySweep"/> exists, one level up.
    /// </summary>
    private async Task RunPassesUntilSettledAsync(long strandedId, long parentId, CancellationToken ct)
    {
        var pass = Services.GetRequiredService<RecoveryPass>();
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            await pass.RunAsync(TestNamespaceId, TestNamespace, ct);

            var settled =
                (await ReadJobAsync(strandedId, ct)).Status == JobStatusCode.Ready
                && (await ReadJobAsync(parentId, ct)).Status == JobStatusCode.Ready;
            if (settled || Stopwatch.GetElapsedTime(started) >= SpecWaits.Converge)
            {
                return;
            }
        }
    }

    private Task<ExtendWorkerLeasesResponse> BeatAsync(int workerId, int? recoveryIntervalSeconds, CancellationToken ct) =>
        Services
            .GetRequiredService<WorkersOperations>()
            .ExtendWorkerLeasesAsync(new ExtendWorkerLeasesCommand(workerId, 30, false, [], [], recoveryIntervalSeconds), ct);

    private Task MakeDueAsync(CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.namespaces SET recovery_due_at_utc = NULL WHERE namespace_id = @p_id",
            ct,
            ("@p_id", TestNamespaceId)
        );

    private async Task<DateTime?> RecoveryDueAsync(CancellationToken ct) =>
        (await Db.From<NamespaceEntity>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct))!.RecoveryDueAtUtc;

    /// <summary>Lands the child terminal the way a crash does: the status moves, the latch is never set.</summary>
    private Task SetTerminalWithoutRaiseAsync(long jobId, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Cancelled),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );
}
