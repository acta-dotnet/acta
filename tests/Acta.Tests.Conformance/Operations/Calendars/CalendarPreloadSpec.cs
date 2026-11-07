using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>
/// Conformance for the engine's calendar preload: a resolver answers from its own read, so a later or slower preload
/// of the same family never points it at another version.
/// </summary>
[ConformanceSpec(
    "calendar.preload-keeps-its-read",
    "A calendar resolver keeps the versions its own preload read",
    Area = "Scheduling",
    Contract = "A resolver answers the calendar version its own preload read, whatever version a later preload of the same family reads."
)]
public sealed class CalendarPreloadSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string SecondVersion = """
        {"kind":"explicit-instants","timeZone":"UTC","instants":["2027-03-15T16:00:00Z"]}
        """;

    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "A resolver from before a publication still answers the version it read")]
    public async Task A_resolver_keeps_its_own_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));
        var timing = new ScheduleTiming(calendar.CalendarName, ScheduleExpressionKindCode.Calendar, "UTC", null);
        var operations = Services.GetRequiredService<CalendarsOperations>();
        var after = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);
        Assert.Equal(ControlAction.Applied, (await Calendars.PublishAsync(calendar, 0, 1, ct: ct)).Action);
        var before = await operations.PreloadAsync(TestNamespaceId, [calendar.CalendarName], ct);

        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, SecondVersion, 1, ct: ct);
        Assert.Equal(ControlAction.Applied, (await Calendars.PublishAsync(calendar, 1, 2, ct: ct)).Action);
        var later = await operations.PreloadAsync(TestNamespaceId, [calendar.CalendarName], ct);

        Assert.Equal(new DateTime(2027, 3, 15, 16, 0, 0, DateTimeKind.Utc), NextOccurrenceCalculator.Next(later(timing), after));
        Assert.Equal(new DateTime(2027, 1, 15, 16, 0, 0, DateTimeKind.Utc), NextOccurrenceCalculator.Next(before(timing), after));
    }
}
