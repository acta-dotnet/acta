using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>
/// The job verbs' slot rules: a restart is refused while a firing is outstanding and otherwise returns the slot to
/// Idle, a purge is refused while a live schedule pins the slot, whatever its status, and a retire unpins it, which a
/// purge then decommissions.
/// </summary>
[ConformanceSpec(
    "job.slot-operator-rules",
    "A slot restarts only with no firing outstanding and is purged only unpinned",
    Area = "Scheduling",
    Contract = "RestartAsync rejects a slot with a firing outstanding, and PurgeAsync refuses a slot a live schedule pins, even a cancelled one, until a retire unpins it."
)]
public sealed class SlotOperatorRulesSpec : ActaRuntimeTestBase<ConformanceFixture, RescheduleSlotManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // The clock starts at the real minute: settlement stamps the DB clock, which this clock must not trail.
        var now = DateTime.UtcNow;
        services.AddSingleton<IActaClock>(new FakeClock(new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc)));
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A restart of a slot with a firing outstanding is rejected, and one with none returns the slot to Idle")]
    public async Task A_slot_restarts_only_with_no_firing_outstanding()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct)).Action);

        Assert.Equal(ControlAction.Rejected, (await Jobs.RestartAsync(JobLookup.ById(slotId), ct: ct)).Action);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        var restarted = await Jobs.RestartAsync(JobLookup.ById(slotId), ct: ct);
        Assert.Equal((ControlAction.Applied, JobStatusCode.Idle), (restarted.Action, restarted.Status));
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
    }

    [Fact(DisplayName = "A purge refuses a slot its live schedule pins, even cancelled; after a retire the purge deletes it")]
    public async Task A_slot_is_purged_only_to_decommission_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);

        Assert.Equal(ControlAction.Rejected, (await Jobs.PurgeAsync(JobLookup.ById(slotId), ct: ct)).Action);
        // Cancelled is a status a slot may be purged from, so this refusal is the pin's, not the status's.
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(ControlAction.Rejected, (await Jobs.PurgeAsync(JobLookup.ById(slotId), ct: ct)).Action);

        var definition = await Operations.Definitions.GetAsync(TestNamespace, RescheduleSlotManifest.JobName, ct);
        Assert.NotNull(definition);
        Assert.Equal(
            ControlAction.Applied,
            (await Operations.Definitions.RetireAsync(TestNamespace, RescheduleSlotManifest.JobName, definition!.Version, ct: ct)).Action
        );
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(slotId, ct)).Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Null(await Jobs.GetAsync(JobLookup.ById(slotId), ct));
    }
}
