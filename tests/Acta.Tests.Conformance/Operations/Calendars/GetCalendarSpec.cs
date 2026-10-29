using System.Text.Json;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>
/// Conformance for reading one calendar family: every version newest first, the shown version's definition, and every
/// schedule bound to the family through the catalog, orphans included.
/// </summary>
[ConformanceSpec(
    "calendar.get",
    "A calendar family reads with its versions, a definition, and its dependents",
    Area = "Scheduling",
    Contract = "Get answers every version newest first, the requested, current, or latest definition, and every catalog-bound schedule, or null."
)]
public sealed class GetCalendarSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "Get lists the versions newest first and shows the latest definition while none is current")]
    public async Task Get_shows_the_latest_definition_without_a_current_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));
        var first = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);
        var second = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 1, ct: ct);

        var read = await Calendars.GetAsync(calendar, ct: ct);

        Assert.NotNull(read);
        Assert.Equal(
            (TestNamespace, calendar.CalendarName, CalendarRoleCode.Occurrence),
            (read.JobNamespace, read.CalendarName, read.Role)
        );
        Assert.Null(read.CurrentVersion);
        Assert.Equal([2, 1], read.Versions.Select(v => v.Version));
        Assert.All(read.Versions, v => Assert.Equal(CalendarStatusCode.Draft, v.Status));
        Assert.Equal([second.ContentHash, first.ContentHash], read.Versions.Select(v => v.ContentHash));
        Assert.Equal(2, read.ShownVersion);
        Assert.Equal("explicit-instants", Kind(read.Definition));
        Assert.Empty(read.Dependents);
    }

    [Fact(DisplayName = "Get shows the current version by default and a requested one on demand; a version or family it lacks is null")]
    public async Task Get_shows_the_current_or_the_requested_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 1, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);

        var current = await Calendars.GetAsync(calendar, ct: ct);
        var requested = await Calendars.GetAsync(calendar, 2, ct);

        Assert.Equal((1, 1), (current!.CurrentVersion, current.ShownVersion));
        Assert.Equal(CalendarStatusCode.Published, current.Versions.Single(v => v.Version == 1).Status);
        Assert.NotNull(current.Versions.Single(v => v.Version == 1).PublishedAtUtc);
        Assert.Equal("weekly-availability", Kind(current.Definition));
        Assert.Equal((1, 2), (requested!.CurrentVersion, requested.ShownVersion));
        Assert.Null(await Calendars.GetAsync(calendar, 3, ct));
        Assert.Null(await Calendars.GetAsync(new CalendarLookup(TestNamespace, Unique("no-such-calendar")), ct: ct));
    }

    [Fact(DisplayName = "Get lists every schedule bound through the catalog with its pinned version, an orphan with its status")]
    public async Task Get_lists_the_catalog_bound_schedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        var jobName = Unique("support-sweep");
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);
        var defId = await CalendarTestOps.BindAvailabilityAsync(
            Services,
            TestNamespaceId,
            jobName,
            calendar.CalendarName,
            [
                ("evening", CalendarOriginCode.Catalog, 1),
                ("morning", CalendarOriginCode.Catalog, 1),
                ("code", CalendarOriginCode.Registered, 1),
            ],
            ct
        );
        await CalendarTestOps.RegisterAsync(
            Services,
            TestNamespaceId,
            defId,
            calendar.CalendarName,
            [("morning", CalendarOriginCode.Catalog, 1), ("code", CalendarOriginCode.Registered, 1)],
            ct
        );

        var read = await Calendars.GetAsync(calendar, ct: ct);

        Assert.Equal(
            [
                new GetCalendarDependent(
                    TestNamespace,
                    jobName,
                    "evening",
                    CalendarRoleCode.Availability,
                    ScheduleStatusCode.Orphaned,
                    1,
                    ScheduledDeliveryCode.PerOccurrence
                ),
                new GetCalendarDependent(
                    TestNamespace,
                    jobName,
                    "morning",
                    CalendarRoleCode.Availability,
                    ScheduleStatusCode.Active,
                    1,
                    ScheduledDeliveryCode.PerOccurrence
                ),
            ],
            read!.Dependents
        );
        // The namespace's list also carries the shared families other specs leave in sys; this test's id keeps to its own.
        var listed = await Calendars.ListAsync(new ListCalendarsQuery(TestNamespace, NameSearch: TestId), ct);
        Assert.Equal(1, Assert.Single(listed.Items).DependentCount);
    }

    private static string? Kind(string definition)
    {
        using var document = JsonDocument.Parse(definition);
        return document.RootElement.GetProperty("kind").GetString();
    }
}
