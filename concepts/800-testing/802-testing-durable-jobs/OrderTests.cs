// Test a durable multi-step job with Acta.Testing. Each RunOnceAsync drives exactly one
// attempt, so a signal suspend and its resume after a raise are separate explicit ticks.
//
//   dotnet test concepts/800-testing/802-testing-durable-jobs

using Acta.Testing.Hosting;
using Xunit;

namespace Acta.Concepts.TestingDurable;

public sealed class OrderTests(ActaHostFixture acta) : ActaTestBase(acta)
{
    [Fact]
    public async Task Order_reserves_once_waits_for_approval_then_charges()
    {
        OrderJob.ReserveCount = 0;
        var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-1", 99m), ct: Ct);

        // First tick: reserve runs, then the job suspends waiting for approval (re-armed, not done).
        Assert.Equal(ActaRunOutcome.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(JobStatusCode.Suspended, await Jobs.GetStatusAsync(enqueued, Ct));

        // The approval API raises a typed decision; the job moves to Ready.
        var raise = await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(true, "alice"), ct: Ct);
        Assert.Equal(ControlAction.Applied, raise.Action);

        // Second tick: the job replays (reserve is NOT re-run), the signal is Set, charge runs, Done.
        Assert.Equal(ActaRunOutcome.Completed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, Ct));

        var result = await Jobs.GetResultAsync<OrderResult>(enqueued, Ct);
        Assert.True(result!.Charged);
        Assert.Equal(1, OrderJob.ReserveCount); // reserve ran exactly once across the replay
    }

    [Fact]
    public async Task Rejected_order_completes_without_charging()
    {
        OrderJob.ReserveCount = 0;
        var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-2", 50m), ct: Ct);

        Assert.Equal(ActaRunOutcome.Rearmed, await Host.RunOnceAsync(enqueued, Ct));

        await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(false, "bob"), ct: Ct);

        Assert.Equal(ActaRunOutcome.Completed, await Host.RunOnceAsync(enqueued, Ct));
        var result = await Jobs.GetResultAsync<OrderResult>(enqueued, Ct);
        Assert.False(result!.Charged);
    }
}
