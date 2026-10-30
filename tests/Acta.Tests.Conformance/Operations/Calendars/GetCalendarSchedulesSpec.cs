using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Conformance for the read a publication retimes from: every schedule that names the family, across namespaces for a shared one.</summary>
[ConformanceSpec(
    "calendar.bound-schedules",
    "A family's bound schedules read back, across namespaces when shared",
    Area = "Scheduling",
    Contract = "The schedules naming a family read back with timing and cursor, the namespace's own for a local family and every namespace's when shared."
)]
public sealed class GetCalendarSchedulesSpec : ActaStorageTestBase<ConformanceFixture>
{
    [Fact(DisplayName = "A shared family's schedules read back from every namespace; a local family's from its own")]
    public async Task Bound_schedules_follow_the_family_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var shared = Unique("holidays");
        var local = Unique("cutoffs");
        var otherNamespaceId = await Seeder.SeedJobNamespaceAsync(Unique("other-ns"), ct: ct);
        await CalendarTestOps.BindAvailabilityAsync(Services, TestNamespaceId, Unique("own"), shared, ["daily"], ct);
        await CalendarTestOps.BindAvailabilityAsync(Services, otherNamespaceId, Unique("theirs"), shared, ["daily"], ct);
        await CalendarTestOps.BindAvailabilityAsync(Services, TestNamespaceId, Unique("local"), local, ["daily"], ct);
        var handler = Services.GetRequiredService<GetCalendarSchedulesHandler>();

        var everywhere = (await handler.HandleAsync(new GetCalendarSchedulesQuery(NameSyntax.SystemNamespaceId, shared), ct)).Schedules;
        var own = (await handler.HandleAsync(new GetCalendarSchedulesQuery(TestNamespaceId, shared), ct)).Schedules;
        var localOnly = (await handler.HandleAsync(new GetCalendarSchedulesQuery(TestNamespaceId, local), ct)).Schedules;

        Assert.Equal(2, everywhere.Count);
        Assert.Equal([TestNamespaceId, otherNamespaceId], everywhere.Select(s => s.NamespaceId).Order());
        Assert.All(everywhere, s => Assert.Equal(shared, s.AvailabilityCalendarName));
        Assert.Equal(TestNamespaceId, Assert.Single(own).NamespaceId);
        var bound = Assert.Single(localOnly);
        Assert.Equal(TestNamespace, bound.NamespaceName);
        Assert.NotNull(bound.NextOccurrenceAtUtc);
        Assert.Equal(bound.Timing().AvailabilityCalendarName, local);
    }
}
