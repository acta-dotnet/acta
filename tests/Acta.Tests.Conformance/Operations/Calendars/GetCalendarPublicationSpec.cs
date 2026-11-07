using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Conformance for the read a publication plans from: the family's current version and the version asked for, with their definitions.</summary>
[ConformanceSpec(
    "calendar.publication-read",
    "A publication reads the current version and its candidate with their definitions",
    Area = "Scheduling",
    Contract = "The read answers the family's Published version and the version asked for with their definitions, in a namespace or in sys, and nothing for an unknown family."
)]
public sealed class GetCalendarPublicationSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "The current version and the asked-for draft read back with their definitions; an unknown family reads nothing")]
    public async Task The_current_and_the_candidate_read_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);
        await Calendars.SaveDraftAsync(
            calendar,
            CalendarRoleCode.Occurrence,
            CalendarTestOps.Revised(CalendarTestOps.OccurrenceDefinition, 2),
            1,
            ct: ct
        );
        await Calendars.SaveDraftAsync(
            calendar,
            CalendarRoleCode.Occurrence,
            CalendarTestOps.Revised(CalendarTestOps.OccurrenceDefinition, 3),
            2,
            ct: ct
        );
        var handler = Services.GetRequiredService<GetCalendarPublicationHandler>();

        var (current, candidate) = await handler.HandleAsync(new GetCalendarPublicationQuery(TestNamespace, calendar.CalendarName, 3), ct);

        Assert.NotNull(current);
        Assert.NotNull(candidate);
        Assert.Equal((1, CalendarStatusCode.Published), (current.Version, current.Status));
        Assert.Equal((3, CalendarStatusCode.Draft), (candidate.Version, candidate.Status));
        Assert.All([current, candidate], r => Assert.Equal((TestNamespaceId, CalendarRoleCode.Occurrence), (r.NamespaceId, r.Role)));
        Assert.Equal((await Calendars.GetAsync(calendar, 3, ct))!.Definition, Encoding.UTF8.GetString(candidate.Definition.Span));
        var unknown = await handler.HandleAsync(new GetCalendarPublicationQuery(TestNamespace, Unique("no-such"), 1), ct);
        Assert.Null(unknown.Current);
        Assert.Null(unknown.Candidate);
    }

    [Fact(DisplayName = "A shared family reads back from sys with its namespace id")]
    public async Task A_shared_family_reads_back_from_sys()
    {
        var ct = TestContext.Current.CancellationToken;
        var shared = new CalendarLookup("sys", Unique("holidays"));
        await Calendars.SaveDraftAsync(shared, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);

        var (current, candidate) = await Services
            .GetRequiredService<GetCalendarPublicationHandler>()
            .HandleAsync(new GetCalendarPublicationQuery("sys", shared.CalendarName, 1), ct);

        Assert.Null(current);
        Assert.NotNull(candidate);
        Assert.Equal(
            (NameSyntax.SystemNamespaceId, 1, CalendarStatusCode.Draft),
            (candidate.NamespaceId, candidate.Version, candidate.Status)
        );
    }
}
