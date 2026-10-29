using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Conformance for a calendar preview: a version's windows beside the current version's, and nothing for a version the family lacks.</summary>
[ConformanceSpec(
    "calendar.preview",
    "A calendar version previews beside the current one",
    Area = "Scheduling",
    Contract = "A preview forecasts a version's next instants or windows beside the current version's over the same horizon, and a version the family lacks previews as null."
)]
public sealed class PreviewCalendarSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "A draft previews its windows beside the current version's; a version the family lacks previews as null")]
    public async Task A_draft_previews_beside_the_current_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);
        var mornings = """{"kind":"weekly-availability","timeZone":"UTC","weekly":[{"days":["Monday"],"windows":[["08:00","12:00"]]}]}""";
        Assert.Equal(
            ControlAction.Applied,
            (await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, mornings, 1, ct: ct)).Action
        );
        var from = new DateTime(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);

        var preview = await Calendars.PreviewAsync(calendar, 2, from, 3, ct: ct);

        Assert.NotNull(preview);
        Assert.Equal((CalendarRoleCode.Availability, 2, 1), (preview.Role, preview.Version, preview.CurrentVersion));
        Assert.Equal(3, preview.Windows.Count);
        Assert.Equal(new DateTime(2027, 1, 4, 8, 0, 0, DateTimeKind.Utc), preview.Windows[0].StartUtc);
        Assert.Equal(new DateTime(2027, 1, 11, 8, 0, 0, DateTimeKind.Utc), preview.Windows[1].StartUtc);
        Assert.Equal(3, preview.CurrentWindows.Count);
        Assert.Empty(preview.OccurrencesUtc);
        Assert.Empty(preview.Schedules);
        Assert.Null(await Calendars.PreviewAsync(calendar, 3, from, ct: ct));
    }
}
