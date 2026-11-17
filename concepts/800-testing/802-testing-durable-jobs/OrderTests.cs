// How to test a job that waits for a signal
// Each `RunOnceAsync` drives exactly one attempt, so the suspend on a signal and the resume after its raise are two
// explicit ticks, and the test asserts that the step before the wait ran once across the replay. Run it with
// `dotnet test concepts/800-testing/802-testing-durable-jobs`.
using Acta.Testing;
using Xunit;

namespace Acta.Concepts.TestingDurable;

public sealed class OrderTests(ActaHostFixture acta) : ActaTestBase(acta)
{
    [Fact]
    public async Task Order_reserves_once_waits_for_approval_then_charges()
    {
        var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-1", 99m), ct: Ct);

        // learn: 1. Drive the job to its wait
        // First tick: reserve runs, then the job suspends waiting for approval (re-armed, not done).
        Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(JobStatusCode.Suspended, (await enqueued.GetStatusAsync(Ct))?.Status);
        // learn: end

        // learn: 2. Raise the signal from the test
        // The approval API raises a typed decision; the job moves to Ready.
        var raise = await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(true, "alice"), Ct);
        Assert.Equal(ControlAction.Applied, raise.Action);
        // learn: end

        // learn: 3. Drive it again, and assert that it finished
        // Second tick: the job replays (reserve is NOT re-run), the signal is Set, charge runs, Done.
        Assert.Equal(ActaRunStatus.Completed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);
        // learn: end

        var result = await enqueued.GetResultAsync<OrderResult>(Ct);
        Assert.True(result.Charged);
        Assert.Equal(1, Warehouse.ReservationsFor("o-1")); // reserve ran exactly once across the replay
    }

    [Fact]
    public async Task Rejected_order_completes_without_charging()
    {
        var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-2", 50m), ct: Ct);

        Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));

        await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(false, "bob"), Ct);

        Assert.Equal(ActaRunStatus.Completed, await Host.RunOnceAsync(enqueued, Ct));
        var result = await enqueued.GetResultAsync<OrderResult>(Ct);
        Assert.False(result.Charged);
    }
}
