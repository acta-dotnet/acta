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
public sealed class ListCurrentCalendarsSpec : ActaStorageTestBase<ConformanceFixture>
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

        var current = await Services.GetRequiredService<CalendarsOperations>().ListCurrentCalendarsAsync(TestNamespaceId, ct);

        // The shared families in sys are listed beside the namespace's own, so the assertion keeps to this spec's names.
        Assert.Equal(
            [new ListCurrentCalendarsItem(names[0], CalendarRoleCode.Availability, 1)],
            current.Calendars.Where(c => names.Contains(c.CalendarName))
        );
    }

    [Fact(DisplayName = "A name published in the namespace's catalog and in the shared one resolves to neither and says so")]
    public async Task A_name_both_catalogs_publish_resolves_to_neither()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("twin-days");
        var shared = Unique("twin-days-shared");
        await Calendars.SaveDraftAsync(
            new(TestNamespace, name),
            CalendarRoleCode.Occurrence,
            CalendarTestOps.OccurrenceDefinition,
            0,
            ct: ct
        );
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, name, 1, ct);
        await Calendars.SaveDraftAsync(new("sys", shared), CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);
        await CalendarTestOps.PublishAsync(Db, SystemNamespaceId, shared, 1, ct);
        // A draft save refuses the other scope's name, but two first saves at the same moment both land; the rename
        // leaves the rows that race leaves.
        await Db.From<CalendarEntity>()
            .Where(c => c.NamespaceId == SystemNamespaceId && c.CalendarName == shared)
            .UpdateOnlyAsync(() => new CalendarEntity { CalendarName = name }, ct);

        var source = await Services
            .GetRequiredService<CalendarsOperations>()
            .SourceAsync(TestNamespaceId, new ScheduleTiming(name, ScheduleExpressionKindCode.Calendar, "UTC", null), ct);

        Assert.Null(source.Calendar);
        Assert.Contains("in both the namespace's catalog and the shared one", Assert.Single(source.Problems()), StringComparison.Ordinal);
    }

    private const int SystemNamespaceId = 1;
}
