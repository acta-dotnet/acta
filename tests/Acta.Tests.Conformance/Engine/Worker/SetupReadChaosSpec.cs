using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The reads an attempt needs between its claim and its handler are repeated the way the writes are, so a failing read
/// cannot leave the claimed row Executing under a lease the heartbeat keeps renewing.
/// </summary>
[ConformanceSpec(
    "runtime.setup-read-repeat",
    "A failing setup read after the claim is repeated",
    Area = "Runtime",
    Contract = "A read that fails between the claim and the handler is repeated, and the attempt completes."
)]
public sealed class SetupReadChaosSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
    }

    [Fact(DisplayName = "A clock read that fails once after the claim is repeated, and the recurring fire completes")]
    public async Task Failing_setup_read_is_repeated()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);

        // The clock is read after the claim and before the handler; a fault there must not escape to the
        // worker loop with the slot left Executing under a lease the heartbeat keeps renewing.
        _faults.ThrowGetUtcNowOnce();
        await AlertTestOps.FireSlotUntilAsync(
            Services,
            Runtime,
            slotId,
            () => RecurringPingHandler.TriggersFor(TestNamespace).Count,
            1,
            ct
        );

        var slot = await Jobs.GetAsync(JobLookup.ById(slotId), ct);
        Assert.NotNull(slot);
        Assert.Equal(JobStatusCode.Ready, slot!.Status);
        Assert.Null(slot.LeasedByWorkerId);
    }
}
