using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Conformance for ListCalendarVersions: a calendar family's versions page newest first.</summary>
[ConformanceSpec(
    "calendar.versions",
    "ListCalendarVersions pages a family's versions newest first",
    Area = "Scheduling",
    Contract = "ListCalendarVersions pages a family's versions newest first with status, content hash, and publication instant, and answers an empty page for an unknown family."
)]
public sealed class ListCalendarVersionsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "ListCalendarVersions pages two versions newest first, the published one with its instant")]
    public async Task Versions_page_newest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        var first = await Calendars.SaveDraftAsync(
            calendar,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );
        var second = await Calendars.SaveDraftAsync(
            calendar,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            1,
            ct: ct
        );
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);

        var page = await Calendars.ListVersionsAsync(new ListCalendarVersionsQuery(calendar, PageSize: 1, IncludeTotal: true), ct);
        var rest = await Calendars.ListVersionsAsync(new ListCalendarVersionsQuery(calendar, PageSize: 1, Cursor: page.NextCursor), ct);

        Assert.Equal(2, page.TotalCount);
        Assert.True(page.HasMore);
        var newest = Assert.Single(page.Items);
        Assert.Equal((2, CalendarStatusCode.Draft, second.ContentHash), (newest.Version, newest.Status, newest.ContentHash));
        Assert.Null(newest.PublishedAtUtc);
        var oldest = Assert.Single(rest.Items);
        Assert.Equal((1, CalendarStatusCode.Published, first.ContentHash), (oldest.Version, oldest.Status, oldest.ContentHash));
        Assert.NotNull(oldest.PublishedAtUtc);
        Assert.Null(rest.NextCursor);
    }

    [Fact(DisplayName = "ListCalendarVersions answers an empty page for an unknown family, and refuses another family's cursor")]
    public async Task Versions_of_an_unknown_family_are_an_empty_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 1, ct: ct);
        var unknown = new CalendarLookup(TestNamespace, Unique("no-such-calendar"));

        var empty = await Calendars.ListVersionsAsync(new ListCalendarVersionsQuery(unknown, IncludeTotal: true), ct);
        var page = await Calendars.ListVersionsAsync(new ListCalendarVersionsQuery(calendar, PageSize: 1), ct);

        Assert.Empty(empty.Items);
        Assert.False(empty.HasMore);
        Assert.Equal(0, empty.TotalCount);
        await Assert.ThrowsAsync<InvalidPageCursorException>(async () =>
            await Calendars.ListVersionsAsync(new ListCalendarVersionsQuery(unknown, Cursor: page.NextCursor), ct)
        );
    }
}
