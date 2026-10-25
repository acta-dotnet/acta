using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for <c>ScheduleAsync</c>: one job held until the database clock plus a delay or until an instant, the
/// schedule's time winning over one set in <c>configure</c>, and the call forms binding their own overloads.
/// </summary>
[ConformanceSpec(
    "jobs.schedule",
    "ScheduleAsync holds one job until its delay or instant",
    Area = "Enqueue",
    Contract = "ScheduleAsync enqueues one job held until db_now plus the delay or until the given instant, which wins over a time set in configure."
)]
public sealed class ScheduleSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    private static readonly DateTimeOffset Instant = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "A delayed schedule holds the job until the database clock plus the delay")]
    public async Task Delayed_schedule_holds_the_job_until_db_now_plus_the_delay()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = Services.GetRequiredService<IActaClock>();

        var before = await clock.GetUtcNowAsync(ct);
        var handle = await Jobs.ScheduleAsync(new AddNumbers(1, 1), TimeSpan.FromSeconds(60), ct: ct);
        var after = await clock.GetUtcNowAsync(ct);

        Assert.Equal(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(TestNamespace, ct));
        var job = await Jobs.GetAsync(handle, ct);
        Assert.Equal(JobStatusCode.Ready, job!.Status);
        // One second of slack on each side absorbs SQL Server's datetime2(3) column against the clock read.
        Assert.InRange(job.NextRunAtUtc!.Value, before.AddSeconds(59), after.AddSeconds(61));
    }

    [Fact(DisplayName = "A timed schedule persists the instant, and it wins over a time set in configure")]
    public async Task Timed_schedule_persists_the_instant_over_configure()
    {
        var ct = TestContext.Current.CancellationToken;

        var plain = await Jobs.ScheduleAsync(new AddNumbers(2, 2), Instant, ct: ct);
        var configured = await Jobs.ScheduleAsync(new AddNumbers(3, 3), Instant, o => o.Delayed(TimeSpan.FromHours(1)), ct);

        Assert.Equal(Instant.UtcDateTime, (await Jobs.GetAsync(plain, ct))!.NextRunAtUtc);
        Assert.Equal(Instant.UtcDateTime, (await Jobs.GetAsync(configured, ct))!.NextRunAtUtc);
    }

    [Fact(DisplayName = "A no-input call schedules its own job, not the call as an input")]
    public async Task No_input_call_schedules_its_own_job()
    {
        var ct = TestContext.Current.CancellationToken;

        var handle = await Jobs.ScheduleAsync(TestJobsManifest.Cancellable(), Instant, ct: ct);

        var job = await Jobs.GetAsync(handle, ct);
        Assert.Equal("cancellable", job!.JobName);
        Assert.Equal(Instant.UtcDateTime, job.NextRunAtUtc);
    }

    [Fact(DisplayName = "A result call's schedule answers a typed handle")]
    public async Task Result_call_schedule_answers_a_typed_handle()
    {
        var ct = TestContext.Current.CancellationToken;

        JobHandle<AddNumbersResult> handle = await Jobs.ScheduleAsync(
            TestJobsManifest.AddNumbers(new AddNumbers(2, 3)),
            TimeSpan.Zero,
            ct: ct
        );

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(handle, ct));
        Assert.Equal(5, (await handle.WaitAsync(ct: ct)).ValueOrThrow().Sum);
    }
}
