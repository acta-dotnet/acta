using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Locks;

/// <summary>
/// Conformance for <c>reserve_rate</c>, the GCRA meter behind a definition's rate limit, on a slow meter
/// (one turn a minute) so every instant is exact. The meter under real handlers, its sweep, and its
/// fast-rate windows are specified in <c>RateLimitSpec</c>.
/// </summary>
[ConformanceSpec(
    "reserve-rate.meter",
    "A rate meter admits its burst and books every later job an exact turn",
    Area = "Locks",
    Contract = "ReserveRate admits an idle meter's burst, books each later job a turn one interval apart, and honours a booked turn once inside its window."
)]
public sealed class ReserveRateSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const int IntervalMilliseconds = 60_000;
    private const int Burst = 3;
    private const int GraceSeconds = RuntimeJobContext.RateReservationGraceSeconds;

    [Fact(DisplayName = "An idle meter admits exactly its burst back to back and books the next job one interval out")]
    public async Task Idle_meter_admits_its_burst_then_books_a_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = TestKey("rate.burst");

        for (var job = 100; job < 100 + Burst; job++)
        {
            var admitted = await ReserveAsync(bucket, job, ct);
            Assert.True(admitted.Admitted, $"job {job} of the burst was not admitted");
            Assert.Equal(0, admitted.WaitMilliseconds);
        }
        var booked = await ReserveAsync(bucket, 103, ct);

        Assert.False(booked.Admitted);
        Assert.InRange(booked.WaitMilliseconds, 1, IntervalMilliseconds);
        var reservation = await ReadOneAsync($"{bucket}.103", ct);
        Assert.Equal(booked.ResumeAtUtc, reservation.ExpiresAtUtc.AddSeconds(-GraceSeconds));
        Assert.Equal(103L, reservation.JobId);

        // Neither row is a hold, so neither carries a token anything could extend or release.
        Assert.Equal(Guid.Empty, reservation.HoldToken);
        Assert.Equal(Guid.Empty, (await ReadAsync(bucket, ct))?.HoldToken);
    }

    [Fact(DisplayName = "An early job asking again gets the same turn without a charge, so the next job queues one interval behind it")]
    public async Task Early_job_is_not_charged_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = TestKey("rate.early");
        await DrainBurstAsync(bucket, ct);
        var booked = await ReserveAsync(bucket, 103, ct);
        var meter = await MeterAsync(bucket, ct);

        var again = await ReserveAsync(bucket, 103, ct);

        Assert.False(again.Admitted);
        Assert.Equal(booked.ResumeAtUtc, again.ResumeAtUtc);
        Assert.InRange(again.WaitMilliseconds, 1, booked.WaitMilliseconds);
        Assert.Equal(meter, await MeterAsync(bucket, ct));

        var next = await ReserveAsync(bucket, 104, ct);
        Assert.False(next.Admitted);
        Assert.Equal(booked.ResumeAtUtc.AddMilliseconds(IntervalMilliseconds), next.ResumeAtUtc);
    }

    [Fact(DisplayName = "A booked turn returned up to one interval late is admitted once, spent, and never moves the meter")]
    public async Task Late_turn_inside_the_window_is_admitted_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = TestKey("rate.late");
        await DrainBurstAsync(bucket, ct);
        // Thirty seconds is far past the one-second floor, but a slow meter's window is its whole interval.
        var turn = await StageTurnAsync(bucket, 110, TimeSpan.FromSeconds(-30), ct);
        var meter = await MeterAsync(bucket, ct);

        var admitted = await ReserveAsync(bucket, 110, ct);

        Assert.Equal(new ReserveRateResponse(true, turn, 0), admitted);
        Assert.Equal(meter, await MeterAsync(bucket, ct));
        Assert.Null(await ReadAsync($"{bucket}.110", ct));
        Assert.False((await ReserveAsync(bucket, 110, ct)).Admitted);
    }

    [Fact(DisplayName = "A booked turn returned later than its window is spent and the job is metered afresh")]
    public async Task Stale_turn_is_re_metered()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = TestKey("rate.stale");
        await DrainBurstAsync(bucket, ct);
        var stale = await StageTurnAsync(bucket, 120, TimeSpan.FromMinutes(-5), ct);
        var meter = Assert.NotNull(await MeterAsync(bucket, ct));

        var remetered = await ReserveAsync(bucket, 120, ct);

        Assert.False(remetered.Admitted);
        Assert.True(remetered.ResumeAtUtc > stale, $"the new turn {remetered.ResumeAtUtc:O} is not after the stale {stale:O}");
        Assert.Equal(meter.AddMilliseconds(IntervalMilliseconds), await MeterAsync(bucket, ct));
        Assert.Equal(remetered.ResumeAtUtc, (await ReadAsync($"{bucket}.120", ct))?.ExpiresAtUtc.AddSeconds(-GraceSeconds));
    }

    [Fact(DisplayName = "A meter idle past its arrival time admits a whole burst again")]
    public async Task Idle_meter_refills_its_burst()
    {
        var ct = TestContext.Current.CancellationToken;
        var bucket = TestKey("rate.idle");
        await DrainBurstAsync(bucket, ct);
        Assert.False((await ReserveAsync(bucket, 103, ct)).Admitted);

        // Back-dated as an hour of idleness would leave it; the expiry sweep may also take it, which is the same meter.
        var idle = DateTime.UtcNow.AddHours(-1);
        await Db.From<Acta.LockEntity>()
            .Where(l => l.LockKey == bucket)
            .UpdateOnlyAsync(() => new Acta.LockEntity { ExpiresAtUtc = idle }, ct);

        for (var job = 200; job < 200 + Burst; job++)
        {
            Assert.True((await ReserveAsync(bucket, job, ct)).Admitted, $"job {job} of the refilled burst was not admitted");
        }
        Assert.False((await ReserveAsync(bucket, 200 + Burst, ct)).Admitted);
    }

    private Task<ReserveRateResponse> ReserveAsync(string bucket, long jobId, CancellationToken ct) =>
        Services.GetRequiredService<ILocksOperations>().ReserveRateAsync(bucket, jobId, IntervalMilliseconds, Burst, GraceSeconds, ct);

    private async Task DrainBurstAsync(string bucket, CancellationToken ct)
    {
        for (var job = 100; job < 100 + Burst; job++)
        {
            Assert.True((await ReserveAsync(bucket, job, ct)).Admitted);
        }
    }

    /// <summary>
    /// Books <paramref name="jobId"/> a turn <paramref name="age"/> from the database's now, in the stored form
    /// (the turn plus the grace), and returns the turn as stored.
    /// </summary>
    private async Task<DateTime> StageTurnAsync(string bucket, long jobId, TimeSpan age, CancellationToken ct)
    {
        var key = $"{bucket}.{jobId}";
        Assert.NotNull(
            (await Services.GetRequiredService<ILocksOperations>().AcquireLockAsync(key, TimeSpan.FromMinutes(10), jobId, ct)).Token
        );
        var expires = (await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct)).Add(age).AddSeconds(GraceSeconds);
        Assert.Equal(
            1,
            await Db.From<Acta.LockEntity>()
                .Where(l => l.LockKey == key)
                .UpdateOnlyAsync(() => new Acta.LockEntity { ExpiresAtUtc = expires }, ct)
        );
        return (await ReadOneAsync(key, ct)).ExpiresAtUtc.AddSeconds(-GraceSeconds);
    }

    /// <summary>The bucket's stored arrival time, which every charge moves one interval and nothing else moves.</summary>
    private async Task<DateTime?> MeterAsync(string bucket, CancellationToken ct) => (await ReadAsync(bucket, ct))?.ExpiresAtUtc;

    private Task<Acta.LockEntity?> ReadAsync(string key, CancellationToken ct) =>
        Db.From<Acta.LockEntity>().Where(l => l.LockKey == key).SingleOrDefaultAsync(ct);

    private async Task<Acta.LockEntity> ReadOneAsync(string key, CancellationToken ct) =>
        Assert.Single(await Db.From<Acta.LockEntity>().Where(l => l.LockKey == key).ToListAsync(ct));
}
