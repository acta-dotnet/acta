using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Conformance for the engine's read of a namespace's current calendars: only Published versions are current.</summary>
[ConformanceSpec(
    "calendar.current",
    "A namespace's current calendars are its families' Published versions",
    Area = "Scheduling",
    Contract = "The current calendars are each family's Published version by name with its role, and a family of drafts or an archived one has none."
)]
public sealed class GetCurrentCalendarsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "Only a family's Published version is current; drafts and an archived version are not")]
    public async Task Only_published_versions_are_current()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] names = [Unique("a-hours"), Unique("b-days"), Unique("c-days")];
        await Calendars.SaveDraftAsync(
            new(TestNamespace, names[0]),
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );
        await Calendars.SaveDraftAsync(
            new(TestNamespace, names[0]),
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            1,
            ct: ct
        );
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, names[0], 1, ct);
        await Calendars.SaveDraftAsync(
            new(TestNamespace, names[1]),
            CalendarRoleCode.Occurrence,
            CalendarTestOps.OccurrenceDefinition,
            0,
            ct: ct
        );
        await Calendars.SaveDraftAsync(
            new(TestNamespace, names[2]),
            CalendarRoleCode.Occurrence,
            CalendarTestOps.OccurrenceDefinition,
            0,
            ct: ct
        );
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, names[2], 1, ct);
        await Calendars.ArchiveAsync(new(TestNamespace, names[2]), 1, ct: ct);

        var current = await Services.GetRequiredService<CalendarsOperations>().GetCurrentCalendarsAsync(TestNamespaceId, ct);

        // The shared families in sys are listed beside the namespace's own, so the assertion keeps to this spec's names.
        Assert.Equal(
            [new GetCurrentCalendarsItem(names[0], CalendarRoleCode.Availability, 1)],
            current.Calendars.Where(c => names.Contains(c.CalendarName))
        );
    }
}
