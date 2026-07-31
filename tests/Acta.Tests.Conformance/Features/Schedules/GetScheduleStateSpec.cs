using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// Conformance for <c>GetScheduleState</c>: reads the persisted non-orphaned per-schedule cursors
/// for a namespace. A namespace that contains no live schedule rows returns an empty list. After
/// <c>InitializeAsync</c> the test namespace has live schedule rows from the TestJobs recurring
/// definition, so at least one <c>StoredScheduleState</c> is returned.
/// </summary>
[ConformanceSpec(
    "get-schedule-state.namespace-read",
    "GetScheduleState returns live cursors for the namespace, empty when none exist",
    Area = "Scheduling",
    Contract = "GetScheduleState returns the non-orphaned per-schedule cursors for the given namespace id, or an empty list when none exist.",
    Arrange = "InitializeAsync has seeded the test namespace with the TestJobs recurring definition's live schedule rows.",
    Act = "GetScheduleState runs for a namespace id with no schedule rows and for the seeded namespace.",
    Assert = "The empty namespace returns an empty list and the seeded namespace returns its non-orphaned per-schedule cursors."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetScheduleStateAsync))]
public abstract class GetScheduleStateSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    [Fact(DisplayName = "A namespace id with no live schedule rows returns an empty list")]
    public async Task Returns_empty_for_a_namespace_with_no_schedules()
    {
        var ct = TestContext.Current.CancellationToken;

        // A namespace id that does not exist in this DB has no schedule rows.
        var states = await Services.GetRequiredService<IScheduleStore>().GetScheduleStateAsync(short.MaxValue, ct);

        Assert.Empty(states);
    }

    [Fact(DisplayName = "After InitializeAsync seeds a recurring definition, at least one cursor returns with a non-empty ScheduleName")]
    public async Task Returns_cursors_for_a_namespace_with_live_schedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];

        var states = await Services.GetRequiredService<IScheduleStore>().GetScheduleStateAsync(namespaceId, ct);

        // TestJobs declares at least one recurring [JobSchedule], so InitializeAsync seeds at least one live row.
        Assert.NotEmpty(states);
        foreach (var s in states)
        {
            Assert.False(string.IsNullOrEmpty(s.ScheduleName), "ScheduleName must be non-empty.");
            Assert.True(s.DefinitionId > 0, "DefinitionId must be positive.");
        }
    }
}
