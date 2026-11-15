using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for a signal wait bounded by an instant (<c>ctx.WaitSignalAsync(name, until)</c>): the instant itself is
/// the wait's stored expiration, not a length measured from when the wait armed, so a wait first armed past it times out
/// at once.
/// </summary>
[ConformanceSpec(
    "signals.wait-until",
    "A wait bounded by an instant stores that instant and times out at it",
    Area = "Signals",
    Contract = "WaitSignalAsync(name, until) stores until as the wait's expiration, and a wait first armed at or past it times out at once."
)]
[Deploys("job-wait-signal-until")]
public sealed class SignalDeadlineSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<SignalDeadlineSpec>>
{
    [Fact(DisplayName = "A wait bounded by an instant stores that instant as its expiration")]
    public async Task The_instant_is_the_stored_expiration()
    {
        var ct = TestContext.Current.CancellationToken;
        var until = new DateTimeOffset(
            DateTime.UtcNow.AddMinutes(30).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero
        );
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-until", JobPayload.Json(until)), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var checkpoint = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(CheckpointStatusCode.Pending, checkpoint.Status);
        Assert.Equal(until.UtcDateTime, checkpoint.DueAtUtc);
    }

    [Fact(DisplayName = "A wait first armed past its instant times out at once")]
    public async Task A_wait_armed_past_its_instant_times_out_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-wait-signal-until", JobPayload.Json(DateTimeOffset.UtcNow.AddMinutes(-1))),
            ct
        );

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.True(await Jobs.GetResultAsync<bool>(enqueued, ct));
    }
}
