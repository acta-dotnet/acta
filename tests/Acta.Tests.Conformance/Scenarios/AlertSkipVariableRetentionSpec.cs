using Acta.Runtime.Maintenance;
using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the retention sweep's checkpoint section: <c>sys.alerts</c> records one durable
/// poison-skip variable per unprojectable event on its own recurring slot and never reads it back, so
/// the sweep ages them out on <c>JobsOptions.AlertRetention</c> alongside the alerts they stand in
/// for. The window is driven to a deterministic boundary by passing a wide positive retention or a
/// negative one (cutoff in the future), so no real-time wait is needed.
/// </summary>
[ConformanceSpec(
    "alerts-skip.retention",
    "Aged projector skip variables are pruned on the alert window",
    Area = "Retention",
    Contract = "Purge deletes sys.alerts poison-skip variables past the alert window, leaving the projector cursor and every other slot's variables alone.",
    Arrange = "Two skip variables and the cursor are written on the sys.alerts slot, and one skip-named variable on another system slot.",
    Act = "PurgeExpiredData.Run executes with a wide alert window and then with a cutoff in the future.",
    Assert = "The wide window keeps every variable and the future cutoff deletes only the projector slot's skip variables."
)]
[CoversStoreMethod(typeof(IRetentionStore), nameof(IRetentionStore.PurgeExpiredDataAsync))]
public abstract class AlertSkipVariableRetentionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int NoEventPurgeDays = 100_000;
    private const int NoAlertPurgeDays = 100_000;
    private const int NoWorkerPurgeSeconds = 100_000_000;

    // The projector's slot is the one this sweep prunes, so its slot has to exist in the namespace.
    protected override bool RegisterSystemJobs => true;

    [Fact(DisplayName = "Skip variables past the alert window are deleted while the projector cursor survives")]
    public async Task Aged_skip_variables_are_pruned_and_the_cursor_is_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var projector = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "sys.alerts", ct);

        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, ns, projector, eventId: 4141, ct);
        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, ns, projector, eventId: 4142, ct);
        await AlertTestOps.RewindAlertsCursorAsync(Services, TestNamespace, ns, projector, cursorEventId: 4142, ct);

        // Inside the window nothing goes: these rows are forensics for a defect an operator may still
        // be reading, and they age on the same clock as the alerts they explain.
        await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(4141), ct));
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(4142), ct));

        // A cutoff in the future puts both past the window. The cursor is not a skip row and stays: the
        // projector reads it every pass, and deleting it would replay the whole event stream.
        await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, -1, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(0, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(4141), ct));
        Assert.Equal(0, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(4142), ct));
        Assert.Equal(1, await CountVariableAsync(projector, AlertsJob.CursorVariableName, ct));
    }

    [Fact(DisplayName = "A skip-named variable on another job is left alone whatever its age")]
    public async Task Skip_named_variable_outside_the_projector_slot_is_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var projector = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "sys.alerts", ct);
        var other = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "sys.retention", ct);

        // The prune is scoped to the projector's own slot rather than to the name alone, so a variable
        // any other job happens to name this way is that job's data and not retention's business.
        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, ns, projector, eventId: 5150, ct);
        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, ns, other, eventId: 5150, ct);

        await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, -1, NoWorkerPurgeSeconds, 1000, 50, ct);

        Assert.Equal(0, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(5150), ct));
        Assert.Equal(1, await CountVariableAsync(other, AlertTestOps.SkipVariableName(5150), ct));
    }
}
