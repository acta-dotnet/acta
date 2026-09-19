using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Unit pins for rate admission in <see cref="JobExecution.RunAsync"/>, driven by
/// <see cref="JobExecutionHarness"/>'s scripted meter: which bucket and which meter shape the runner
/// asks for, that an admitted job runs its handler, and that a denied one skips the handler, gives
/// back the concurrency slot it had just taken, and re-arms at exactly the reserved instant.
/// </summary>
public sealed class JobExecutionRateAdmissionTests
{
    private static readonly DateTime ReservedTurn = new(2026, 9, 19, 12, 0, 30, 250, DateTimeKind.Utc);

    [Fact]
    public async Task No_declared_rate_asks_the_meter_for_nothing()
    {
        var harness = new JobExecutionHarness();

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceOutcome.Completed, outcome);
        Assert.Empty(harness.RateRequests);
    }

    [Fact]
    public async Task A_rate_with_no_key_meters_on_the_definition_name()
    {
        var harness = new JobExecutionHarness(rateLimit: "10/s");

        var outcome = await harness.RunAsync();

        var request = Assert.Single(harness.RateRequests);
        Assert.Equal(RunOnceOutcome.Completed, outcome);
        Assert.True(harness.HandlerRan);
        Assert.Equal("1.rate.harness-job", request.BucketKey);
        Assert.Equal(100, request.IntervalMilliseconds);
        Assert.Equal(10, request.Burst);
    }

    [Fact]
    public async Task A_declared_rate_key_names_the_meter_instead()
    {
        var harness = new JobExecutionHarness(rateLimit: "600/m", rateKey: "stripe");

        await harness.RunAsync();

        // A per-minute rate meters with one second's worth of burst, not the whole minute's count.
        var request = Assert.Single(harness.RateRequests);
        Assert.Equal("1.rate.stripe", request.BucketKey);
        Assert.Equal(100, request.IntervalMilliseconds);
        Assert.Equal(10, request.Burst);
    }

    [Fact]
    public async Task A_denied_rate_re_arms_at_the_reserved_instant_budget_neutral()
    {
        var harness = new JobExecutionHarness(rateLimit: "10/s", rateResumeAtUtc: ReservedTurn);

        var outcome = await harness.RunAsync();

        var completion = harness.Completion;
        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.False(harness.HandlerRan);
        Assert.Equal(ExecutionOutcome.Rescheduled, completion.Outcome);
        Assert.Equal(JobEventReasonCode.JobRateLimited, completion.JobEventReasonCode);

        // The reserved turn verbatim, not a delay: the meter booked a place in its queue, so any
        // recomputation here would land the job somewhere other than where the bucket expects it.
        Assert.Equal(ReservedTurn, completion.RescheduleResumeAtUtc);
        Assert.Null(completion.RescheduleDelaySeconds);
        Assert.Null(completion.FailureCount);
    }

    [Fact]
    public async Task A_denied_rate_gives_back_the_concurrency_slot_it_just_took()
    {
        // Both gates declared: the slot is taken first, so a rate denial must hand it straight back
        // rather than hold it for the whole wait.
        var harness = new JobExecutionHarness(
            concurrencyKey: "customer-1",
            concurrencyLimit: 4,
            rateLimit: "10/s",
            rateResumeAtUtc: ReservedTurn
        );

        await harness.RunAsync();

        Assert.Single(harness.SlotRequests);
        Assert.Equal(1, harness.SlotReleases);
    }

    [Fact]
    public async Task A_booked_turn_gets_a_fixed_grace_that_no_worker_setting_moves()
    {
        // The turn is decoded as expiry minus grace, so a grace taken from the lease TTL would shift
        // every booked turn on a fleet-wide heartbeat change; the constant keeps old bookings honest.
        var harness = new JobExecutionHarness(rateLimit: "10/s");

        await harness.RunAsync();

        Assert.Equal(RuntimeJobContext.RateReservationGraceSeconds, Assert.Single(harness.RateRequests).GraceSeconds);
        Assert.Equal(900, RuntimeJobContext.RateReservationGraceSeconds);
    }

    [Fact]
    public async Task A_provider_error_on_the_reservation_bounces_and_gives_the_slot_back()
    {
        var harness = new JobExecutionHarness(concurrencyKey: "customer-1", concurrencyLimit: 4, rateLimit: "10/s", rateThrows: true);

        var outcome = await harness.RunAsync();

        // The meter could not answer, so the attempt bounces with the fixed delay rather than a
        // reserved instant, the slot it took goes back, and nothing is charged; a reservation the
        // meter may have booked without answering is honoured when the job returns.
        var completion = harness.Completion;
        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.False(harness.HandlerRan);
        Assert.Equal(JobEventReasonCode.JobConcurrencyKeyHeld, completion.JobEventReasonCode);
        Assert.Equal(new JobsOptions().ConcurrencyKeyBounceDelaySeconds, completion.RescheduleDelaySeconds);
        Assert.Null(completion.RescheduleResumeAtUtc);
        Assert.Null(completion.FailureCount);
        Assert.Equal(1, harness.SlotReleases);
    }

    [Fact]
    public async Task A_bounced_concurrency_slot_never_spends_a_rate_turn()
    {
        // The slot is the outer gate, so a job that cannot run must not move the meter: spending a
        // turn here would meter attempts that never execute.
        var harness = new JobExecutionHarness(concurrencyKey: "customer-1", slotGranted: false, rateLimit: "10/s");

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.Empty(harness.RateRequests);
        Assert.Equal(JobEventReasonCode.JobConcurrencyKeyHeld, harness.Completion.JobEventReasonCode);
    }
}
