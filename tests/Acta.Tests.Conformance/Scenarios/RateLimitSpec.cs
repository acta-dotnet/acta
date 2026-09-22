using System.Collections.Immutable;
using System.Diagnostics;
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
/// <c>{ns}.rate.{key}.{job_id}</c>; the attempt sleeps a turn inside the next quarter second out in process and
/// re-arms at the exact instant for a farther one, so a backlog drains at the rate with no re-race.
/// A booked turn stays valid for a second past its instant. Neither row is a hold: nothing extends
/// or releases them, and the expiry sweep collects them once their instant is past.
/// </summary>
[ConformanceSpec(
    "rate-limit.meter",
    "A definition's rate limit admits at the rate and books every early job a turn",
    Area = "Concurrency",
    Contract = "A rate key admits its burst at once and then one per interval, booking each early job a turn it waits for or returns to.",
    Arrange = "Definitions declaring a rate, alone and sharing a key, plus meters driven through the lock store.",
    Act = "Real handlers drain a backlog through the rate, and store-level requests spend and stage turns.",
    Assert = "Admissions stay inside the rate, a booked turn is honoured for a second, and one key means one bucket."
)]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.ReserveRateAsync))]
public abstract class RateLimitSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Burst = 10;
    private const int IntervalMilliseconds = 100;

    // The grace a booked turn gets past its instant before the sweep may take it. Production passes a
    // fixed fifteen minutes; a spec picks its own so it can stage a turn at a chosen age.
    private const int GraceSeconds = 30;

    [Fact(DisplayName = "A fresh meter admits its burst at once and then one per interval")]
    public async Task A_fresh_meter_admits_its_burst_at_once_and_then_one_per_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-burst"));

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Burst; i++)
        {
            var admitted = await ReserveAsync(bucket, jobId: 1000 + i, ct);
            Assert.True(admitted.Admitted, $"request {i} of the burst was denied");
        }

        // The burst is spent, so the next request is the first to wait, and it waits one interval. The
        // meter refills one turn per interval, though, so on a slow database (a shared CI server queuing
        // the first reserves of a fresh bucket behind other classes) the burst itself can take longer
        // than an interval and leave a turn free: the eleventh request is then admitted honestly. The
        // claim measured here is the rate: past the burst, the meter admits at most the turns the
        // elapsed time could have refilled, and the first denial books a turn ahead of now.
        // A database whose every round trip outlasts an interval refills a turn per request and never
        // denies, so the loop is bounded by the clock, not by a count.
        var admittedPastBurst = 0;
        RateReservation? denied = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            var next = await ReserveAsync(bucket, jobId: 1099 + admittedPastBurst, ct);
            if (!next.Admitted)
            {
                denied = next;
                break;
            }
            admittedPastBurst++;
        }

        // One interval of slack for the request that lands on the refill boundary.
        var refilled = (int)(clock.Elapsed.TotalMilliseconds / IntervalMilliseconds) + 1;
        Assert.True(
            admittedPastBurst <= refilled,
            $"{admittedPastBurst} request(s) past the burst were admitted, but at most {refilled} turn(s) could have refilled in {clock.Elapsed.TotalMilliseconds:0}ms"
        );
        if (denied is { } booked)
        {
            Assert.True(
                booked.ResumeAtUtc > DateTime.UtcNow.AddMilliseconds(-IntervalMilliseconds),
                $"the reserved turn {booked.ResumeAtUtc:O} is not ahead of now"
            );
        }
    }

    [Fact(DisplayName = "A booked turn is handed back unchanged until it arrives, and the meter stays put")]
    public async Task A_booked_turn_is_handed_back_unchanged_until_it_arrives()
    {
        const long jobId = 1200;
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-booked"));

        // Parked thirty days ahead so the first request has to wait, and so the row stays well clear of
        // any concurrent sweep in this shared database: a charge below must be the only thing that
        // moves it.
        Assert.NotNull(await Locks.TryAcquireAsync(bucket, TimeSpan.FromDays(30), 1201, ct));

        var booked = await ReserveAsync(bucket, jobId, ct);
        Assert.False(booked.Admitted);
        var meterAfterBooking = await MeterAsync(bucket, ct);

        // A second ask before the turn arrives - a worker that restarted, or a claim that ran early -
        // gets the same instant and must not charge the meter again.
        var again = await ReserveAsync(bucket, jobId, ct);
        Assert.False(again.Admitted);
        Assert.Equal(booked.ResumeAtUtc, again.ResumeAtUtc);
        Assert.Equal(meterAfterBooking, await MeterAsync(bucket, ct));

        // The wait is the turn minus the store's own now, so the runner never subtracts a host reading
        // from a database one; parked thirty days ahead, it reads about thirty days, past int.MaxValue,
        // which a backlog on a slow meter can reach and a 32-bit wait would overflow.
        Assert.InRange(booked.WaitMilliseconds, 29L * 86_400_000, 30L * 86_400_000);
        Assert.True(booked.WaitMilliseconds > int.MaxValue);
        Assert.InRange(again.WaitMilliseconds, 1, booked.WaitMilliseconds);
    }

    [Fact(DisplayName = "A turn returned inside the second admits once, is spent, and never moves the meter")]
    public async Task A_turn_returned_on_time_admits_once_and_never_moves_the_meter()
    {
        const long jobId = 1400;
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-ontime"));
        var reservation = $"{bucket}.{jobId}";

        // The meter is parked minutes ahead, so an admission below can only come from the turn. Five
        // intervals late at 100ms, which is how late a claim-path pickup can be after a re-arm at the
        // exact instant: inside the one-second window the turn still counts, so a fast meter is not
        // defeated by the worker's poll floor and jitter. Half a second of the window is left for the
        // round trips between the stage and the reserve, which a shared CI database can stretch past
        // the hundred milliseconds a tighter stage would leave.
        Assert.NotNull(await Locks.TryAcquireAsync(bucket, TimeSpan.FromMinutes(5), 1401, ct));
        var clock = Stopwatch.StartNew();
        await StageTurnAsync(reservation, jobId, TimeSpan.FromMilliseconds(-500), ct);
        var meterBefore = await MeterAsync(bucket, ct);

        var admitted = await ReserveAsync(bucket, jobId, ct);

        // The turn's age is judged on the database clock, which the host clock around two round trips
        // can only overestimate: a host reading past the validity does not prove the database saw a
        // stale turn, but a host reading inside it proves the turn was live. So an admission is always
        // held to its invariants, and a denial is accepted only when the host clock allows staleness.
        if (admitted.Admitted)
        {
            // The meter counted this job when it allocated the turn; charging it again would meter it twice.
            Assert.Equal(0, admitted.WaitMilliseconds);
            Assert.Equal(meterBefore, await MeterAsync(bucket, ct));
            Assert.Null(await ReadLockAsync(reservation, ct));
        }
        else
        {
            Assert.True(
                clock.Elapsed >= TimeSpan.FromMilliseconds(500),
                $"a turn 500ms past its instant was not honoured with {(500 - clock.Elapsed.TotalMilliseconds):0}ms of validity left"
            );
        }
    }

    [Fact(DisplayName = "A turn gone stale is re-metered rather than honoured")]
    public async Task A_turn_gone_stale_is_re_metered_rather_than_honoured()
    {
        const long jobId = 1450;
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-stale"));
        var reservation = $"{bucket}.{jobId}";

        // A turn that went by while every executor was busy: still inside its grace, so the row is
        // there, but further past its instant than the second a turn stays valid for.
        Assert.NotNull(await Locks.TryAcquireAsync(bucket, TimeSpan.FromMinutes(5), 1451, ct));
        await StageTurnAsync(reservation, jobId, TimeSpan.FromSeconds(-5), ct);
        var meterBefore = await MeterAsync(bucket, ct);

        var denied = await ReserveAsync(bucket, jobId, ct);

        // Back through the meter: the stale turn buys nothing, the bucket moves, and the job is booked
        // a fresh instant. Honouring it instead is what would let a queue of overdue jobs start at once.
        Assert.False(denied.Admitted);
        Assert.NotEqual(meterBefore, await MeterAsync(bucket, ct));
        Assert.Equal(denied.ResumeAtUtc, (await ReadLockAsync(reservation, ct))?.ExpiresAtUtc.AddSeconds(-GraceSeconds));
    }

    [Fact(DisplayName = "A backlog of turns gone stale releases at most one burst at once")]
    public async Task A_backlog_of_turns_gone_stale_releases_at_most_one_burst_at_once()
    {
        const int waiting = Burst + 20;
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-catchup"));

        // Every one of them holds a turn that passed while the fleet was busy, on a meter that has
        // since gone idle. Honouring stale turns would admit all of them in the same instant.
        for (var i = 0; i < waiting; i++)
        {
            await StageTurnAsync($"{bucket}.{1460 + i}", 1460 + i, TimeSpan.FromSeconds(-5), ct);
        }

        var admitted = 0;
        var started = DateTime.UtcNow;
        for (var i = 0; i < waiting; i++)
        {
            if ((await ReserveAsync(bucket, 1460 + i, ct)).Admitted)
            {
                admitted++;
            }
        }
        var window = DateTime.UtcNow - started;

        // The R*T + N ceiling, measured over however long these round trips took: an idle meter's
        // burst plus whatever the rate itself earned while they ran, and nothing like all of them.
        var earned = (int)(window.TotalMilliseconds / IntervalMilliseconds);
        Assert.InRange(admitted, Burst, Burst + earned + 1);
    }

    [Fact(DisplayName = "A booked turn outlives the lock expiry sweep until its grace runs out")]
    public async Task A_booked_turn_outlives_the_lock_expiry_sweep_until_its_grace_runs_out()
    {
        const long jobId = 1480;
        var ct = TestContext.Current.CancellationToken;
        var bucket = Bucket(TestKey("rl-grace"));
        var reservation = $"{bucket}.{jobId}";

        // A turn whose instant has already gone by but whose grace has not: the row a job that is
        // late but still alive is coming back for.
        Assert.NotNull(await Locks.TryAcquireAsync(bucket, TimeSpan.FromMinutes(5), 1481, ct));
        await StageTurnAsync(reservation, jobId, TimeSpan.FromSeconds(-1), ct);
        var staged = await ReadLockAsync(reservation, ct);

        await PurgeAsync(ct);

        // Untouched. The sweep reads expires_at_utc, which sits a grace past the turn precisely so a
        // job that is late but still inside its lease keeps the place the meter gave it.
        Assert.Equal(staged?.ExpiresAtUtc, (await ReadLockAsync(reservation, ct))?.ExpiresAtUtc);
    }

    [Fact(DisplayName = "An unspent turn is collected by the lock expiry sweep")]
    public async Task An_unspent_turn_is_collected_by_the_lock_expiry_sweep()
    {
        const long jobId = 1500;
        var ct = TestContext.Current.CancellationToken;
        var reservation = $"{Bucket(TestKey("rl-swept"))}.{jobId}";

        // A cancelled or reclaimed job leaves its turn behind. Past its grace as well as its instant,
        // so no live job can still be coming back for it and it is ordinary garbage.
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
        // with a burst of N allocates at most R*T + N turns in any T seconds, and a turn may be taken
        // up to one interval late, so the window can see one admission more than that: R*T + N + 1. The
        // instants are stamped by the handler, a scheduling hop after the meter admitted it and on the
        // host clock rather than the database's, so the window is read with a tolerance for that hop in
        // either direction.
        const double clockHopSeconds = 0.25;
        var window = (admitted[^1] - admitted[0]).TotalSeconds;
        Assert.True(
            admitted.Count <= (Burst * (window + clockHopSeconds)) + Burst + 1,
            $"{admitted.Count} admissions in {window:F3}s exceeds the declared 10/s plus a burst of {Burst}, plus one late turn"
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
        var service = Services.GetRequiredService<DefinitionsService>();

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

    [Fact(DisplayName = "An override on one participant of a shared meter lands on every other participant")]
    public async Task An_override_on_one_participant_lands_on_every_other_participant()
    {
        var ct = TestContext.Current.CancellationToken;
        var left = await Definitions.GetAsync(TestNamespace, "rate-shared-left", ct);
        Assert.NotNull(left);

        var outcome = await Definitions.UpdateOverridesAsync(
            TestNamespace,
            "rate-shared-left",
            left.Version,
            new JobDefinitionPolicyOverrides(RateLimit: "1/s"),
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, outcome.Action);

        // Nobody asked "rate-shared-right" for anything, yet it carries the same override: it shares
        // the meter "rate-shared-left" just retuned.
        var right = await Definitions.GetAsync(TestNamespace, "rate-shared-right", ct);
        Assert.Equal("1/s", right?.RateLimitOverride);
        Assert.Equal("1/s", right?.RateLimitEffective);

        var leftAfter = await Definitions.GetAsync(TestNamespace, "rate-shared-left", ct);
        Assert.Equal("1/s", leftAfter?.RateLimitOverride);
    }

    [Fact(DisplayName = "Clearing the override on one participant clears it on every other participant")]
    public async Task Clearing_the_override_on_one_participant_clears_it_on_every_other_participant()
    {
        var ct = TestContext.Current.CancellationToken;
        var left = await Definitions.GetAsync(TestNamespace, "rate-shared-left", ct);
        Assert.NotNull(left);
        var set = await Definitions.UpdateOverridesAsync(
            TestNamespace,
            "rate-shared-left",
            left.Version,
            new JobDefinitionPolicyOverrides(RateLimit: "1/s"),
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, set.Action);

        var leftOverridden = await Definitions.GetAsync(TestNamespace, "rate-shared-left", ct);
        Assert.NotNull(leftOverridden);
        var cleared = await Definitions.UpdateOverridesAsync(
            TestNamespace,
            "rate-shared-left",
            leftOverridden.Version,
            new JobDefinitionPolicyOverrides(RateLimit: null),
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, cleared.Action);

        // The clear reached "rate-shared-right" too: the meter falls back to what both of them declare.
        var right = await Definitions.GetAsync(TestNamespace, "rate-shared-right", ct);
        Assert.Null(right?.RateLimitOverride);
        Assert.Equal(RateLimitProbes.SharedRate, right?.RateLimitEffective);
    }

    [Fact(DisplayName = "Overriding a meter's own-name definition, which declares no rate, still lands on the sibling that declares one")]
    public async Task Overriding_the_meters_own_name_definition_lands_on_the_declared_sibling()
    {
        var ct = TestContext.Current.CancellationToken;
        var stripe = await Definitions.GetAsync(TestNamespace, "stripe", ct);
        Assert.NotNull(stripe);

        var outcome = await Definitions.UpdateOverridesAsync(
            TestNamespace,
            "stripe",
            stripe.Version,
            new JobDefinitionPolicyOverrides(RateLimit: "1/s"),
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, outcome.Action);

        // "stripe" declares no rate of its own, yet it names the meter "charge" spends from: the
        // override still reaches "charge", the meter's only declared participant.
        var charge = await Definitions.GetAsync(TestNamespace, "charge", ct);
        Assert.Equal("1/s", charge?.RateLimitOverride);
        Assert.Equal("1/s", charge?.RateLimitEffective);
    }

    [Fact(DisplayName = "Clearing the override on a meter's own-name definition clears it on the declared sibling too")]
    public async Task Clearing_the_override_on_the_meters_own_name_definition_clears_the_declared_sibling_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var stripe = await Definitions.GetAsync(TestNamespace, "stripe", ct);
        Assert.NotNull(stripe);
        var set = await Definitions.UpdateOverridesAsync(
            TestNamespace,
            "stripe",
            stripe.Version,
            new JobDefinitionPolicyOverrides(RateLimit: "1/s"),
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, set.Action);

        var stripeOverridden = await Definitions.GetAsync(TestNamespace, "stripe", ct);
        Assert.NotNull(stripeOverridden);
        var cleared = await Definitions.UpdateOverridesAsync(
            TestNamespace,
            "stripe",
            stripeOverridden.Version,
            new JobDefinitionPolicyOverrides(RateLimit: null),
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, cleared.Action);

        var charge = await Definitions.GetAsync(TestNamespace, "charge", ct);
        Assert.Null(charge?.RateLimitOverride);
        Assert.Equal(RateLimitProbes.ChargeRate, charge?.RateLimitEffective);
    }

    [Fact(DisplayName = "A definition metered on its own name is unaffected by a shared meter's override")]
    public async Task A_definition_on_its_own_meter_is_unaffected_by_a_shared_meters_override()
    {
        var ct = TestContext.Current.CancellationToken;
        var left = await Definitions.GetAsync(TestNamespace, "rate-shared-left", ct);
        Assert.NotNull(left);

        var outcome = await Definitions.UpdateOverridesAsync(
            TestNamespace,
            "rate-shared-left",
            left.Version,
            new JobDefinitionPolicyOverrides(RateLimit: "1/s"),
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, outcome.Action);

        // "rate-limited-probe" meters on its own name, nothing like "shared-meter", so it never sees it.
        var solo = await Definitions.GetAsync(TestNamespace, "rate-limited-probe", ct);
        Assert.Null(solo?.RateLimitOverride);
        Assert.Equal(RateLimitProbes.Rate, solo?.RateLimitEffective);
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

        // Bring the booked turn just past its instant, and the job with it: the meter is still minutes
        // ahead, so an admission here can only come from the turn, and the slot has to be taken again.
        // The runner's grace is a fixed constant, so the row is stamped relative to that.
        var reservation = $"{bucket}.{enqueued.JobId}";
        var justPast = DateTime.UtcNow.AddSeconds(RuntimeJobContext.RateReservationGraceSeconds).AddMilliseconds(-50);
        Assert.Equal(
            1,
            await Db.From<LockRow>().Where(l => l.LockKey == reservation).UpdateOnlyAsync(() => new LockRow { ExpiresAtUtc = justPast }, ct)
        );
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, ct: ct)).Action);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, enqueued.JobId, ct));
        Assert.Single(RateLimitProbes.AdmittedAt(TestNamespace));
        Assert.Null(await ReadLockAsync($"{slotPrefix}.0", ct));
    }

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private ILockStore Locks => Services.GetRequiredService<ILockStore>();

    private IDefinitions Definitions => Operations.Definitions;

    // The composition the runner uses: namespace id, the rate discriminator, and the canonical key.
    private string Bucket(string key) => $"{NamespaceId}.rate.{IdentifierSyntax.NormalizeLowerInvariant(key)}";

    private Task<RateReservation> ReserveAsync(string bucket, long jobId, CancellationToken ct) =>
        Locks.ReserveRateAsync(bucket, jobId, IntervalMilliseconds, Burst, GraceSeconds, ct);

    /// <summary>The meter's stored arrival time, which every charged request moves and nothing else does.</summary>
    private async Task<DateTime?> MeterAsync(string bucket, CancellationToken ct) => (await ReadLockAsync(bucket, ct))?.ExpiresAtUtc;

    /// <summary>
    /// Puts a reservation row whose turn sits <paramref name="age"/> from now, whether or not one is
    /// already there. The row stores the turn plus the grace, which is what the sweep reads, so the
    /// stage goes through the stored form rather than through the acquire's lease arithmetic.
    /// </summary>
    private async Task StageTurnAsync(string reservation, long jobId, TimeSpan age, CancellationToken ct)
    {
        // Created through the store so the row carries the store's shape, then stamped to the exact
        // instant: the acquire's lease is whole seconds and a turn is staged to the millisecond.
        await Locks.TryAcquireAsync(reservation, TimeSpan.FromMinutes(10), jobId, ct);
        var expires = DateTime.UtcNow.Add(age).AddSeconds(GraceSeconds);
        var affected = await Db.From<LockRow>()
            .Where(l => l.LockKey == reservation)
            .UpdateOnlyAsync(() => new LockRow { ExpiresAtUtc = expires }, ct);
        Assert.Equal(1, affected);
    }

    private Task PurgeAsync(CancellationToken ct) =>
        RetentionTestOps.PurgeAsync(
            Services,
            NamespaceId,
            eventsRetentionDays: 3650,
            alertRetentionDays: 3650,
            workerRetentionSeconds: int.MaxValue,
            batchSize: 500,
            maxIterations: 20,
            ct
        );

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
