using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>
/// Conformance for who a calendar verb records: the operator it was given is its event's <c>actor</c> column, and the
/// details keep only the calendar's facts.
/// </summary>
[ConformanceSpec(
    "calendar.actor",
    "A calendar verb records its operator as the event's actor",
    Area = "Scheduling",
    Contract = "The operator a calendar verb names is its event's actor, byte-identical on every provider, and the event's details name no actor."
)]
public sealed class CalendarControlActorSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string Operator = "Jože Kovač";

    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Theory(DisplayName = "The operator who applied a calendar verb is the event's actor, and the details name no actor")]
    [InlineData("save-draft", EventCode.CalendarDraftSaved)]
    [InlineData("publish", EventCode.CalendarPublished)]
    [InlineData("archive", EventCode.CalendarArchived)]
    public async Task Operator_is_the_events_actor(string verb, EventCode recorded)
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        var role = CalendarRoleCode.Availability;
        var definition = CalendarTestOps.AvailabilityDefinition;
        if (verb != "save-draft")
        {
            await Calendars.SaveDraftAsync(calendar, role, definition, 0, ct: ct);
        }
        if (verb == "archive")
        {
            Assert.Equal(ControlAction.Applied, (await Calendars.PublishAsync(calendar, 1, ct: ct)).Action);
        }

        var action = verb switch
        {
            "save-draft" => (await Calendars.SaveDraftAsync(calendar, role, definition, 0, "why", Operator, ct)).Action,
            "publish" => (await Calendars.PublishAsync(calendar, 1, reasonMessage: "why", actor: Operator, ct: ct)).Action,
            _ => (await Calendars.ArchiveAsync(calendar, 1, "why", Operator, ct)).Action,
        };

        Assert.Equal(ControlAction.Applied, action);
        var evt = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.NamespaceId == TestNamespaceId && e.EventCode == recorded).ToListAsync(ct)
        );
        Assert.Equal(Operator, evt.Actor);
        Assert.Null(EventDetail(evt, "actor"));
        Assert.Equal(calendar.CalendarName, EventDetail(evt, "calendar"));
    }
}
