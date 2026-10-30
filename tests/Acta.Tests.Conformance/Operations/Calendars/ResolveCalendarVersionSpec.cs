using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Conformance for the engine's read of one calendar version: any status reads back, an absent version is null.</summary>
[ConformanceSpec(
    "calendar.version-read",
    "Any calendar version reads back with its definition, whatever its status",
    Area = "Scheduling",
    Contract = "A version reads back with its role, status, and stored definition whatever its status, and a version the family lacks reads as null."
)]
public sealed class ResolveCalendarVersionSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "An archived version reads back with its role, status, and definition; a version the family lacks is null")]
    public async Task Any_version_reads_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);
        await Calendars.ArchiveAsync(calendar, 1, ct: ct);
        var operations = Services.GetRequiredService<CalendarsOperations>();

        var archived = await operations.ResolveCalendarVersionAsync(TestNamespaceId, calendar.CalendarName, 1, ct);
        var missing = await operations.ResolveCalendarVersionAsync(TestNamespaceId, calendar.CalendarName, 2, ct);

        Assert.NotNull(archived);
        Assert.Equal((CalendarRoleCode.Occurrence, CalendarStatusCode.Archived), (archived.Role, archived.Status));
        Assert.Equal((await Calendars.GetAsync(calendar, 1, ct))!.Definition, Encoding.UTF8.GetString(archived.Definition.Span));
        Assert.Null(missing);
    }

    [Fact(DisplayName = "A version in the shared sys catalog resolves from any namespace")]
    public async Task A_shared_version_resolves_from_any_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var shared = new CalendarLookup("sys", Unique("holidays"));
        await Calendars.SaveDraftAsync(shared, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);

        var resolved = await Services
            .GetRequiredService<CalendarsOperations>()
            .ResolveCalendarVersionAsync(TestNamespaceId, shared.CalendarName, 1, ct);

        Assert.NotNull(resolved);
        Assert.Equal(CalendarRoleCode.Availability, resolved.Role);
    }
}
