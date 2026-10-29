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
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 1, ct: ct);
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 2, ct: ct);
        var handler = Services.GetRequiredService<GetCalendarPublicationHandler>();

        var rows = (await handler.HandleAsync(new GetCalendarPublicationQuery(TestNamespace, calendar.CalendarName, 3), ct)).Rows;

        Assert.Equal([1, 3], rows.Select(r => r.Version));
        Assert.Equal([CalendarStatusCode.Published, CalendarStatusCode.Draft], rows.Select(r => r.Status));
        Assert.All(rows, r => Assert.Equal((TestNamespaceId, CalendarRoleCode.Occurrence), (r.NamespaceId, r.Role)));
        Assert.Equal((await Calendars.GetAsync(calendar, 3, ct))!.Definition, Encoding.UTF8.GetString(rows[1].Definition.Span));
        Assert.Empty((await handler.HandleAsync(new GetCalendarPublicationQuery(TestNamespace, Unique("no-such"), 1), ct)).Rows);
    }

    [Fact(DisplayName = "A shared family reads back from sys with its namespace id")]
    public async Task A_shared_family_reads_back_from_sys()
    {
        var ct = TestContext.Current.CancellationToken;
        var shared = new CalendarLookup("sys", Unique("holidays"));
        await Calendars.SaveDraftAsync(shared, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);

        var rows = (
            await Services
                .GetRequiredService<GetCalendarPublicationHandler>()
                .HandleAsync(new GetCalendarPublicationQuery("sys", shared.CalendarName, 1), ct)
        ).Rows;

        var row = Assert.Single(rows);
        Assert.Equal((NameSyntax.SystemNamespaceId, 1, CalendarStatusCode.Draft), (row.NamespaceId, row.Version, row.Status));
    }
}
