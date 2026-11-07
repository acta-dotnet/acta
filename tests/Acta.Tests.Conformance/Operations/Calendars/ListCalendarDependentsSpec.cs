using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Conformance for ListCalendarDependents: the schedules that name a calendar family page by path, orphans included.</summary>
[ConformanceSpec(
    "calendar.dependents",
    "ListCalendarDependents pages the schedules naming a family by path",
    Area = "Scheduling",
    Contract = "ListCalendarDependents pages the schedules naming a family by namespace, job, schedule, and binding, orphans included, and nothing for an unknown family."
)]
public sealed class ListCalendarDependentsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "ListCalendarDependents pages every schedule that names the family, an orphan with its status")]
    public async Task Dependents_page_by_path()
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
            ["evening", "morning"],
            ct
        );
        await CalendarTestOps.RegisterAsync(Services, TestNamespaceId, defId, calendar.CalendarName, ["morning"], ct);

        var page = await Calendars.ListDependentsAsync(new ListCalendarDependentsQuery(calendar, PageSize: 1, IncludeTotal: true), ct);
        var rest = await Calendars.ListDependentsAsync(new ListCalendarDependentsQuery(calendar, PageSize: 1, Cursor: page.NextCursor), ct);

        Assert.Equal(2, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal(
            [
                new ListCalendarDependentsItem(
                    TestNamespace,
                    jobName,
                    "evening",
                    CalendarRoleCode.Availability,
                    ScheduleStatusCode.Orphaned,
                    ScheduledDeliveryCode.PerOccurrence
                ),
                new ListCalendarDependentsItem(
                    TestNamespace,
                    jobName,
                    "morning",
                    CalendarRoleCode.Availability,
                    ScheduleStatusCode.Active,
                    ScheduledDeliveryCode.PerOccurrence
                ),
            ],
            page.Items.Concat(rest.Items)
        );
        Assert.Null(rest.NextCursor);
        // The namespace's list also carries the shared families other specs leave in sys; this test's id keeps to its own.
        var listed = await Calendars.ListAsync(new ListCalendarsQuery(TestNamespace, NameContains: TestId), ct);
        Assert.Equal(1, Assert.Single(listed.Items).DependentCount);
    }

    [Fact(DisplayName = "ListCalendarDependents answers an empty page for a name the catalog never saved, though a schedule names it")]
    public async Task Dependents_of_an_unknown_family_are_an_empty_page()
    {
        var ct = TestContext.Current.CancellationToken;
        // A schedule row may name a calendar the catalog never saved: no family holds that name, so it has no dependents.
        var unsaved = Unique("unsaved");
        await CalendarTestOps.BindAvailabilityAsync(Services, TestNamespaceId, Unique("other-sweep"), unsaved, ["nightly"], ct);

        var unknown = await Calendars.ListDependentsAsync(
            new ListCalendarDependentsQuery(new CalendarLookup(TestNamespace, unsaved), IncludeTotal: true),
            ct
        );

        Assert.Empty(unknown.Items);
        Assert.False(unknown.HasMore);
        Assert.Equal(0, unknown.TotalCount);
    }
}
