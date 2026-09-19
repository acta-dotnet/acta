using System.Collections.Immutable;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Services.Locks;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;
using LockRow = Acta.Relational.Entities.Lock;
using RateSpec = Acta.RateLimitSpec;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Per-definition rate-limit spec. The meter is a GCRA bucket row <c>{ns}.rate.{key}</c> whose
/// instant is the next theoretical arrival time; a request that arrives early books a reservation row
/// <c>{ns}.rate.{key}.{job_id}</c> and the attempt re-arms at exactly that instant, so a backlog
/// drains at the rate with one re-arm per job and no re-race. Neither row is a hold: nothing extends
/// or releases them, and the expiry sweep collects them once their instant is past.
/// </summary>
[ConformanceSpec(
    "rate-limit.meter",
    "A definition's rate limit admits at the rate and books every early job a turn",
    Area = "Concurrency",
    Contract = "A rate key admits its burst at once and then one per interval, booking each early job exactly one re-arm.",
    Arrange = "Definitions declaring a rate, alone and sharing a key, plus meters driven through the lock store.",
    Act = "Real handlers drain a backlog through the rate, and store-level requests spend and stage turns.",
    Assert = "Admissions stay inside the rate, each denied job re-arms once at its reserved instant, and one key means one bucket."
)]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.ReserveRateAsync))]
public abstract class RateLimitSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Burst = 10;
    private const int IntervalMilliseconds = 100;

    [Fact(DisplayName = "A fresh meter admits its burst at once and then one per interval")]
    public async Task A_fresh_meter_admits_its_burst_at_once_and_then_one_per_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-burst"));

        for (var i = 0; i < Burst; i++)
        {
            var admitted = await ReserveAsync(bucket, jobId: 1000 + i, ct);
            Assert.True(admitted.Admitted, $"request {i} of the burst was denied");
        }

        // The burst is spent, so the next request is the first to wait, and it waits one interval.
        var denied = await ReserveAsync(bucket, jobId: 1099, ct);
        Assert.False(denied.Admitted);
        Assert.True(
            denied.ResumeAtUtc > DateTime.UtcNow.AddMilliseconds(-IntervalMilliseconds),
            $"the reserved turn {denied.ResumeAtUtc:O} is not ahead of now"
        );
    }

    [Fact(DisplayName = "A booked turn is handed back unchanged until it arrives, and the meter stays put")]
    public async Task A_booked_turn_is_handed_back_unchanged_until_it_arrives()
    {
        const long jobId = 1200;
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-booked"));

        for (var i = 0; i < Burst; i++)
        {
            await ReserveAsync(bucket, jobId: 1300 + i, ct);
        }

        var booked = await ReserveAsync(bucket, jobId, ct);
        Assert.False(booked.Admitted);
        var meterAfterBooking = await MeterAsync(bucket, ct);

        // A second ask before the turn arrives - a worker that restarted, or a claim that ran early -
        // gets the same instant and must not charge the meter again.
        var again = await ReserveAsync(bucket, jobId, ct);
        Assert.False(again.Admitted);
        Assert.Equal(booked.ResumeAtUtc, again.ResumeAtUtc);
        Assert.Equal(meterAfterBooking, await MeterAsync(bucket, ct));
    }

    [Fact(DisplayName = "A turn that has arrived admits once and is spent")]
    public async Task A_turn_that_has_arrived_admits_once_and_is_spent()
    {
        const long jobId = 1400;
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-staged"));
        var reservation = $"{bucket}.{jobId}";

        // The meter is parked minutes ahead, so an admission below can only come from the turn.
        Assert.NotNull(await Locks.TryAcquireAsync(bucket, TimeSpan.FromMinutes(5), 1401, ct));

        // The state a worker that died holding a booked turn leaves behind: a reservation row whose
        // instant has already passed. That also makes it ordinary garbage for any concurrent lock
        // sweep in this shared database, so a stage collected inside the round trip is staged again.
        RateReservation admitted;
        DateTime? meterBefore;
        var attempt = 0;
        do
        {
            await StageDueTurnAsync(reservation, jobId, ct);
            meterBefore = await MeterAsync(bucket, ct);
            admitted = await ReserveAsync(bucket, jobId, ct);
        } while (!admitted.Admitted && ++attempt < 5);

        Assert.True(admitted.Admitted, "the staged turn was not honoured");
        Assert.Equal(meterBefore, await MeterAsync(bucket, ct));
        Assert.Null(await ReadLockAsync(reservation, ct));
    }

    [Fact(DisplayName = "An unspent turn is collected by the lock expiry sweep")]
    public async Task An_unspent_turn_is_collected_by_the_lock_expiry_sweep()
    {
        const long jobId = 1500;
        var ct = TestContext.Current.CancellationToken;
        var reservation = $"{Bucket(TestKey("rl-swept"))}.{jobId}";

        // A cancelled or reclaimed job leaves its turn behind; it is past, so it is ordinary garbage.
        Assert.NotNull(await Locks.TryAcquireAsync(reservation, TimeSpan.FromSeconds(-1), jobId, ct));

        await RetentionTestOps.PurgeUntilAsync(
            Services,
            NamespaceId,
            eventsRetentionDays: 3650,
            alertRetentionDays: 3650,
            workerRetentionSeconds: int.MaxValue,
            batchSize: 500,
            maxIterations: 20,
            async () => await ReadLockAsync(reservation, ct) is null,
            ct
        );

        Assert.Null(await ReadLockAsync(reservation, ct));
    }

    [Fact(DisplayName = "A backlog drains at the declared rate with one re-arm per denied job")]
    public async Task A_backlog_drains_at_the_declared_rate_with_one_re_arm_per_denied_job()
    {
        const int jobs = 30;
        const int workers = 3;
        var ct = TestContext.Current.CancellationToken;

        RateLimitProbes.Reset(TestNamespace);
        var ids = new List<long>(jobs);
        for (var i = 0; i < jobs; i++)
        {
            var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "rate-limited-probe", JobPayload.None), ct);
            ids.Add(enqueued.JobId);
        }

        await DriveUntilCompletedAsync(jobs, workers, ct);

        var admitted = RateLimitProbes.AdmittedAt(TestNamespace);
        Assert.Equal(jobs, admitted.Count);

        // The safety property, stated over the window the drain actually took: a meter of R per second
        // with a burst of N admits at most R*T + N in any T seconds. The instants are stamped by the
        // handler, a scheduling hop after the meter admitted it and on the host clock rather than the
        // database's, so the window is read with a tolerance for that hop in either direction.
        const double clockHopSeconds = 0.25;
        var window = (admitted[^1] - admitted[0]).TotalSeconds;
        Assert.True(
            admitted.Count <= (Burst * (window + clockHopSeconds)) + Burst,
            $"{admitted.Count} admissions in {window:F3}s exceeds the declared 10/s plus a burst of {Burst}"
        );

        // The liveness half: the meter really throttled. Everything past the burst waits its interval,
        // so the drain cannot be faster than that however many workers push on it.
        var floorSeconds = ((jobs - Burst) * IntervalMilliseconds / 1000.0) - clockHopSeconds;
        Assert.True(window >= floorSeconds, $"the drain took {window:F3}s, faster than the {floorSeconds:F3}s the rate allows");

        // Re-arms scale with the size of the backlog, not with how long it waits: each denied job books
        // a turn and comes back for it, where a design that re-raced the meter would bounce every
        // waiting job on every poll and land in the hundreds. Only the ceiling is asserted, and loosely.
        // There is no floor because this suite shares a loaded box: when the drivers claim more slowly
        // than the rate admits, the meter is already caught up by the time a job asks and nothing is
        // booked at all. The booking itself is pinned deterministically by the store-level facts above
        // and by the rate-denial fact below.
        var rearms = 0;
        foreach (var id in ids)
        {
            rearms += await CountRateLimitedAsync(id, ct);
        }
        Assert.True(rearms <= jobs * 3, $"{rearms} re-arms for {jobs} jobs reads as a re-race rather than one booking each");
    }

    [Fact(DisplayName = "Two definitions on one rate key meter from one bucket")]
    public async Task Two_definitions_on_one_rate_key_meter_from_one_bucket()
    {
        var ct = TestContext.Current.CancellationToken;

        // Stage the shared meter minutes ahead. Neither definition has spent anything of its own, so
        // each is denied only if it really reads this meter rather than one under its own name.
        Assert.NotNull(await Locks.TryAcquireAsync(Bucket(RateLimitProbes.SharedKey), TimeSpan.FromMinutes(5), 1700, ct));

        foreach (var name in new[] { "rate-shared-left", "rate-shared-right" })
        {
            var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, name, JobPayload.None), ct);

            Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(TestNamespace, enqueued.JobId, ct));
            Assert.Equal(
                JobEventReasonCode.JobRateLimited,
                (await ReadLatestEventAsync(enqueued.JobId, EventCode.JobRescheduled, ct)).ReasonCode
            );

            // Each waiting job books its turn on the shared meter, under the shared key.
            Assert.NotNull(await ReadLockAsync($"{Bucket(RateLimitProbes.SharedKey)}.{enqueued.JobId}", ct));
            Assert.Null(await ReadLockAsync(Bucket(name), ct));
        }
    }

    [Fact(DisplayName = "Definitions that share a rate key must declare the same rate")]
    public async Task Definitions_that_share_a_rate_key_must_declare_the_same_rate()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new DefinitionsService(Services.GetRequiredService<IDefinitionStore>());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RegisterAsync(
                NamespaceId,
                DateTime.UtcNow,
                [Descriptor("rl-mismatch-left", "10/s"), Descriptor("rl-mismatch-right", "5/s")],
                [],
                ct
            )
        );

        Assert.Contains("rl-mismatch-left", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rl-mismatch-right", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A rate denial hands the concurrency slot back and takes one again on the turn")]
    public async Task A_rate_denial_hands_the_concurrency_slot_back_and_takes_one_again_on_the_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket("rate-and-slot-probe");
        var slotPrefix = $"{NamespaceId}.sem.rate-and-slot-probe";

        // Stage the meter well ahead of now rather than spending its burst, so the denial is a fact
        // about admission and not about how fast this machine got from the spend to the claim.
        Assert.NotNull(await Locks.TryAcquireAsync(bucket, TimeSpan.FromMinutes(5), ownerJobId: 1600, ct));

        RateLimitProbes.Reset(TestNamespace);
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "rate-and-slot-probe", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(TestNamespace, enqueued.JobId, ct));
        var bounce = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobRescheduled, ct);
        Assert.Equal(JobEventReasonCode.JobRateLimited, bounce.ReasonCode);
        Assert.Equal(0, (await ReadJobAsync(enqueued.JobId, ct)).FailureCount);
        Assert.NotNull(await ReadLockAsync($"{bucket}.{enqueued.JobId}", ct));

        // The slot was taken before the meter was asked, so a denial must leave no slot row behind.
        Assert.Null(await ReadLockAsync($"{slotPrefix}.0", ct));
        Assert.Null(await ReadLockAsync($"{slotPrefix}.1", ct));

        // Bring the booked turn forward and the job with it: the meter is still minutes ahead, so an
        // admission here can only come from the reservation, and the slot has to be taken again.
        var reservation = $"{bucket}.{enqueued.JobId}";
        Assert.Equal(
            1,
            await Db.From<LockRow>()
                .Where(l => l.LockKey == reservation)
                .UpdateOnlyAsync(() => new LockRow { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) }, ct)
        );
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, ct: ct)).Action);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, enqueued.JobId, ct));
        Assert.Single(RateLimitProbes.AdmittedAt(TestNamespace));
        Assert.Null(await ReadLockAsync($"{slotPrefix}.0", ct));
    }

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private ILockStore Locks => Services.GetRequiredService<ILockStore>();

    // The composition the runner uses: namespace id, the rate discriminator, and the canonical key.
    private string Bucket(string key) => $"{NamespaceId}.rate.{IdentifierSyntax.NormalizeLowerInvariant(key)}";

    private Task<RateReservation> ReserveAsync(string bucket, long jobId, CancellationToken ct) =>
        Locks.ReserveRateAsync(bucket, jobId, IntervalMilliseconds, Burst, ct);

    /// <summary>The meter's stored arrival time, which every charged request moves and nothing else does.</summary>
    private async Task<DateTime?> MeterAsync(string bucket, CancellationToken ct) => (await ReadLockAsync(bucket, ct))?.ExpiresAtUtc;

    /// <summary>Puts a reservation row at an instant already past, whether or not one is there.</summary>
    private async Task StageDueTurnAsync(string reservation, long jobId, CancellationToken ct)
    {
        if (await Locks.TryAcquireAsync(reservation, TimeSpan.FromSeconds(-1), jobId, ct) is null)
        {
            var affected = await Db.From<LockRow>()
                .Where(l => l.LockKey == reservation)
                .UpdateOnlyAsync(() => new LockRow { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) }, ct);
            Assert.Equal(1, affected);
        }
    }

    private Task<LockRow?> ReadLockAsync(string lockKey, CancellationToken ct) =>
        Db.From<LockRow>().Where(l => l.LockKey == lockKey).SingleOrDefaultAsync(ct);

    // Counted on job.rescheduled alone: one bounce also stamps the reason onto its
    // job.execution-finished row, so an unfiltered count of the reason doubles every re-arm.
    private async Task<int> CountRateLimitedAsync(long jobId, CancellationToken ct) =>
        (
            await Db.From<JobEvent>()
                .Where(e =>
                    e.JobId == jobId && e.EventCode == EventCode.JobRescheduled && e.ReasonCode == JobEventReasonCode.JobRateLimited
                )
                .ToListAsync(ct)
        ).Count;

    private static JobDescriptor Descriptor(string name, string rateLimit) =>
        new(
            JobName: name,
            HandlerType: typeof(RateLimitSpec<TFixture>),
            MethodName: "N/A",
            InputType: typeof(string),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            InvocationKind: JobInvocationKind.Task,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 3,
            AuditLevel: JobAuditLevelCode.Audit,
            AlertProfile: AlertProfileCode.OnFailure,
            Invoker: static (_, _, _, _) => ValueTask.FromResult(new JobHandlerInvocationResult(false, null)),
            DeserializeInput: static (_, _) => string.Empty,
            SerializeOutput: null
        )
        {
            RateLimit = rateLimit,
            RateKey = "rl-mismatch-key",
        };

    private async Task DriveUntilCompletedAsync(int jobs, int workers, CancellationToken ct)
    {
        var completed = 0;
        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        await Task.WhenAll(
            Enumerable
                .Range(0, workers)
                .Select(async _ =>
                {
                    while (Volatile.Read(ref completed) < jobs && DateTime.UtcNow < deadline)
                    {
                        if (await Runtime.RunOnceAsync(TestNamespace, ct) == RunOnceOutcome.Completed)
                        {
                            Interlocked.Increment(ref completed);
                        }
                        else
                        {
                            await Task.Delay(25, ct);
                        }
                    }
                })
        );

        Assert.Equal(jobs, completed);
    }
}
