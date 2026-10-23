using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Retention;

/// <summary>
/// Conformance for durable skip records: retention leaves them for the projector to disposition.
/// </summary>
[ConformanceSpec(
    "alerts-skip.retention",
    "Projector skip variables survive the alert window",
    Area = "Retention",
    Contract = "Purge preserves unresolved sys.alerts skip records and the projector cursor regardless of their age."
)]
public sealed class AlertSkipVariableRetentionSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int NoEventPurgeDays = 100_000;
    private const int NoAlertPurgeDays = 100_000;
    private const int NoWorkerPurgeSeconds = 100_000_000;

    // The projector's slot owns the skip records.
    protected override bool RegisterSystemJobs => true;

    [Fact(DisplayName = "Skip variables and the projector cursor survive past the alert window")]
    public async Task Aged_skip_variables_and_the_cursor_are_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var projector = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "sys.alerts", ct);

        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, ns, projector, eventId: 4141, ct);
        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, ns, projector, eventId: 4142, ct);
        await AlertTestOps.RewindAlertsCursorAsync(Services, TestNamespace, ns, projector, cursorEventId: 4142, ct);

        await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(4141), ct));
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(4142), ct));

        // A future cutoff puts every checkpoint past the alert window.
        await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, -1, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(4141), ct));
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(4142), ct));
        Assert.Equal(1, await CountVariableAsync(projector, AlertsJob.CursorVariableName, ct));
    }

    [Fact(DisplayName = "A skip-named variable on another job is left alone whatever its age")]
    public async Task Skip_named_variable_outside_the_projector_slot_is_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var projector = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "sys.alerts", ct);
        var other = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "sys.retention", ct);

        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, ns, projector, eventId: 5150, ct);
        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, ns, other, eventId: 5150, ct);

        await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, -1, NoWorkerPurgeSeconds, 1000, 50, ct);

        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(5150), ct));
        Assert.Equal(1, await CountVariableAsync(other, AlertTestOps.SkipVariableName(5150), ct));
    }
}
