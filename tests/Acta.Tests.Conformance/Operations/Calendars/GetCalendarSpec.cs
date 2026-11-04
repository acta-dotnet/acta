using System.Text.Json;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Conformance for reading one calendar family's head: its current and latest versions and one version's definition.</summary>
[ConformanceSpec(
    "calendar.get",
    "A calendar family reads its current and latest versions and one definition",
    Area = "Scheduling",
    Contract = "Get answers the role, the current and latest versions, and the requested, current, or latest version's status and definition, or null."
)]
public sealed class GetCalendarSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "Get shows the latest definition while none is current")]
    public async Task Get_shows_the_latest_definition_without_a_current_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 1, ct: ct);

        var read = await Calendars.GetAsync(calendar, ct: ct);

        Assert.NotNull(read);
        Assert.Equal(
            (TestNamespace, calendar.CalendarName, CalendarRoleCode.Occurrence),
            (read.JobNamespace, read.CalendarName, read.Role)
        );
        Assert.Null(read.CurrentVersion);
        Assert.Equal((2, 2, CalendarStatusCode.Draft), (read.LatestVersion, read.ShownVersion, read.ShownStatus));
        Assert.Equal("explicit-instants", Kind(read.Definition));
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

        Assert.Equal((1, 2, 1), (current!.CurrentVersion, current.LatestVersion, current.ShownVersion));
        Assert.Equal(CalendarStatusCode.Published, current.ShownStatus);
        Assert.Equal("weekly-availability", Kind(current.Definition));
        Assert.Equal((1, 2, CalendarStatusCode.Draft), (requested!.CurrentVersion, requested.ShownVersion, requested.ShownStatus));
        Assert.Null(await Calendars.GetAsync(calendar, 0, ct));
        Assert.Null(await Calendars.GetAsync(calendar, 3, ct));
        Assert.Null(await Calendars.GetAsync(new CalendarLookup(TestNamespace, Unique("no-such-calendar")), ct: ct));
    }

    private static string? Kind(string definition)
    {
        using var document = JsonDocument.Parse(definition);
        return document.RootElement.GetProperty("kind").GetString();
    }
}
