using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>
/// Conformance for listing calendar families: one item per family by name, its current and latest versions, the live
/// schedules that bind it through the catalog, a namespace's list with the shared families in <c>sys</c> or a list across
/// every namespace, the role and name filters, and keyset paging. Other specs leave shared families behind, so every read
/// here keeps to this test's own names.
/// </summary>
[ConformanceSpec(
    "calendar.list",
    "Calendar families list by name with their current and latest versions",
    Area = "Scheduling",
    Contract = "The list answers one item per family by name with its versions and live bound schedules, in one namespace with sys or in all, filtered and paged."
)]
public sealed class ListCalendarsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "A family of drafts lists once, with its latest version and no current version")]
    public async Task A_family_of_drafts_lists_without_a_current_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 1, ct: ct);

        var item = Assert.Single(await OwnAsync(new ListCalendarsQuery(TestNamespace), ct));

        Assert.Equal(
            (TestNamespace, calendar.CalendarName, CalendarRoleCode.Availability),
            (item.JobNamespace, item.CalendarName, item.Role)
        );
        Assert.Null(item.CurrentVersion);
        Assert.Null(item.PublishedAtUtc);
        Assert.Equal((2, CalendarStatusCode.Draft, 0), (item.LatestVersion, item.LatestStatus, item.DependentCount));
    }

    [Fact(DisplayName = "A published family lists its current version and counts its live catalog-bound schedules")]
    public async Task A_published_family_lists_its_current_version_and_dependents()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 1, ct: ct);
        await CalendarTestOps.BindAvailabilityAsync(
            Services,
            TestNamespaceId,
            Unique("support-sweep"),
            calendar.CalendarName,
            [("catalog", CalendarOriginCode.Catalog, 1), ("code", CalendarOriginCode.Registered, 1)],
            ct
        );

        var item = Assert.Single(await OwnAsync(new ListCalendarsQuery(TestNamespace), ct));

        Assert.Equal(1, item.CurrentVersion);
        Assert.NotNull(item.PublishedAtUtc);
        Assert.Equal((2, CalendarStatusCode.Draft), (item.LatestVersion, item.LatestStatus));
        Assert.Equal(1, item.DependentCount);
    }

    [Fact(DisplayName = "The list filters by role and name fragment, counts the filter on request, and pages by name")]
    public async Task The_list_filters_and_pages_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] names = [Unique("a-hours"), Unique("b-days"), Unique("c-hours")];
        await Calendars.SaveDraftAsync(
            new(TestNamespace, names[0]),
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );
        await Calendars.SaveDraftAsync(
            new(TestNamespace, names[1]),
            CalendarRoleCode.Occurrence,
            CalendarTestOps.OccurrenceDefinition,
            0,
            ct: ct
        );
        await Calendars.SaveDraftAsync(
            new(TestNamespace, names[2]),
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );

        var availability = await OwnAsync(new ListCalendarsQuery(TestNamespace, Role: CalendarRoleCode.Availability), ct);
        var days = await OwnAsync(new ListCalendarsQuery(TestNamespace, NameSearch: "b-DAYS-" + TestId), ct);
        // The test's id is in every name it saves and in no other test's, so the search pages over this test's rows alone.
        var first = await Calendars.ListAsync(
            new ListCalendarsQuery(TestNamespace, NameSearch: TestId, PageSize: 2, IncludeTotal: true),
            ct
        );
        var second = await Calendars.ListAsync(
            new ListCalendarsQuery(TestNamespace, NameSearch: TestId, PageSize: 2, Cursor: first.NextCursor),
            ct
        );

        Assert.Equal([names[0], names[2]], availability.Select(i => i.CalendarName));
        Assert.Equal([names[1]], days.Select(i => i.CalendarName));
        Assert.Equal([names[0], names[1]], first.Items.Select(i => i.CalendarName));
        Assert.True(first.HasMore);
        Assert.Equal(3, first.TotalCount);
        Assert.Equal([names[2]], second.Items.Select(i => i.CalendarName));
        Assert.False(second.HasMore);
    }

    [Fact(DisplayName = "A namespace's list adds the shared families, and a list without a namespace spans every namespace")]
    public async Task A_namespace_lists_with_the_shared_families_and_no_namespace_lists_them_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var elsewhere = Unique("elsewhere");
        var hours = Unique("hours");
        var closures = Unique("closures");
        await Calendars.SaveDraftAsync(
            new(TestNamespace, hours),
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );
        await Calendars.SaveDraftAsync(
            new(elsewhere, hours),
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );
        await Calendars.SaveDraftAsync(new("sys", closures), CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);

        var scoped = await Calendars.ListAsync(new ListCalendarsQuery(TestNamespace, NameSearch: TestId), ct);
        var shared = await Calendars.ListAsync(new ListCalendarsQuery("sys", NameSearch: TestId), ct);
        var everywhere = await Calendars.ListAsync(new ListCalendarsQuery(NameSearch: TestId, IncludeTotal: true), ct);
        // Two families share a name, so a page of one must break the tie by namespace and lose neither.
        var paged = new List<ListCalendarsItem>();
        string? cursor = null;
        do
        {
            var page = await Calendars.ListAsync(new ListCalendarsQuery(NameSearch: TestId, PageSize: 1, Cursor: cursor), ct);
            paged.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal([("sys", closures), (TestNamespace, hours)], scoped.Items.Select(i => (i.JobNamespace, i.CalendarName)));
        Assert.Equal([("sys", closures)], shared.Items.Select(i => (i.JobNamespace, i.CalendarName)));
        Assert.Equal(3, everywhere.TotalCount);
        Assert.Equal(("sys", closures), (everywhere.Items[0].JobNamespace, everywhere.Items[0].CalendarName));
        Assert.Equal(
            [elsewhere, TestNamespace],
            everywhere.Items.Skip(1).Where(i => i.CalendarName == hours).Select(i => i.JobNamespace).Order(StringComparer.Ordinal)
        );
        Assert.Equal(everywhere.Items.Select(i => (i.JobNamespace, i.CalendarName)), paged.Select(i => (i.JobNamespace, i.CalendarName)));
    }

    /// <summary>
    /// The list's items this test saved. Shared families other specs left in <c>sys</c> list beside them and outlive a
    /// run on a server, so a query with no search of its own searches by the test's id, which keeps them off the page.
    /// </summary>
    private async Task<IReadOnlyList<ListCalendarsItem>> OwnAsync(ListCalendarsQuery query, CancellationToken ct) =>
        [
            .. (await Calendars.ListAsync(query.NameSearch is null ? query with { NameSearch = TestId } : query, ct)).Items.Where(i =>
                i.CalendarName.EndsWith(TestId, StringComparison.Ordinal)
            ),
        ];
}
