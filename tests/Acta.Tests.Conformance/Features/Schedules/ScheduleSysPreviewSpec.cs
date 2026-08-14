using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>Preview resolves a sys.-prefixed system schedule through the lookup-permissive canonicalizer, so operators can preview framework schedules.</summary>
[ConformanceSpec(
    "schedule.sys-preview",
    "Preview resolves a sys. schedule through the lookup-permissive canonicalizer",
    Area = "Scheduling",
    Contract = "Schedule preview resolves a sys.-prefixed system schedule name through the lookup-permissive canonicalizer rather than the write-validating one.",
    Arrange = "The runtime registers the framework sys. jobs and their schedules.",
    Act = "Preview is requested for a sys.-prefixed system schedule.",
    Assert = "Preview returns occurrences rather than throwing the reserved-name ArgumentException."
)]
public abstract class ScheduleSysPreviewSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    protected override bool RunAsWorker => true;

    protected override bool RegisterSystemJobs => true;

    [Fact(DisplayName = "Preview on a sys. system schedule returns occurrences instead of a reserved-name error")]
    public async Task Preview_on_sys_schedule_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        // sys.retention is one of the framework jobs auto-registered into the worker's own namespace
        // (not the seeded "sys" namespace); its recurring slot carries the job name as deduplication key
        // and its sole schedule is named "default" (confirmed in FrameworkJobRegistrationSpec / RetentionJob).
        var lookup = new ScheduleLookup(JobLookup.ByDeduplicationKey(TestNamespace, "sys.retention"), "default");
        var preview = await Operations.Schedules.PreviewAsync(lookup, 3, ct);
        Assert.NotNull(preview);
    }
}
