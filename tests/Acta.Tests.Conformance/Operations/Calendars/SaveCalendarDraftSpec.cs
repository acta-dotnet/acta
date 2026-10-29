using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>
/// Conformance for saving a catalog calendar draft: versions number from 1 under the caller's expected latest version, a
/// family keeps its first role, a definition that does not compile writes nothing, and each save is audited against the
/// calendar version.
/// </summary>
[ConformanceSpec(
    "calendar.save-draft",
    "A calendar draft saves as the family's next version under its expected latest",
    Area = "Scheduling",
    Contract = "A draft saves as the next version only against the family's latest version and role, writes nothing otherwise, and records calendar.draft-saved."
)]
public sealed class SaveCalendarDraftSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "A first draft saves as version 1, a Draft, and records calendar.draft-saved against the calendar")]
    public async Task A_first_draft_saves_as_version_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("office-hours");

        var saved = await Calendars.SaveDraftAsync(
            new CalendarLookup(TestNamespace, name),
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            "first cut",
            "op-1",
            ct
        );

        Assert.Equal(ControlAction.Applied, saved.Action);
        Assert.Equal(1, saved.Version);
        Assert.Empty(saved.Problems);
        var row = Assert.Single(
            await Db.From<CalendarEntity>().Where(c => c.NamespaceId == TestNamespaceId && c.CalendarName == name).ToListAsync(ct)
        );
        Assert.Equal((1, CalendarStatusCode.Draft, CalendarRoleCode.Availability), (row.Version, row.Status, row.Role));
        Assert.Equal(JobPayloadFormat.Json.Id, row.DefinitionFormatId);
        Assert.Equal(saved.ContentHash, row.ContentHash);
        Assert.Null(row.PublishedAtUtc);

        var ev = Assert.Single(
            await Db.From<EventEntity>()
                .Where(e => e.NamespaceId == TestNamespaceId && e.EventCode == EventCode.CalendarDraftSaved)
                .ToListAsync(ct)
        );
        Assert.Equal((EntityCode.Calendar, (long)row.Id), (ev.EntityCode, ev.EntityId));
        Assert.Equal("first cut", ev.EventMessage);
        Assert.Equal(name, EventDetail(ev, "calendar"));
        Assert.Equal("1", EventDetail(ev, "version"));
        Assert.Equal("availability", EventDetail(ev, "role"));
        Assert.Equal("op-1", EventDetail(ev, "actor"));
    }

    [Fact(DisplayName = "A draft against the latest version saves the next one; a stale expected version is a conflict naming the latest")]
    public async Task A_draft_saves_only_against_the_latest_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);

        var second = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 1, ct: ct);
        var stale = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 1, ct: ct);

        Assert.Equal((ControlAction.Applied, 2), (second.Action, second.Version));
        Assert.Equal((ControlAction.VersionConflict, 2), (stale.Action, stale.Version));
        Assert.Null(stale.ContentHash);
        Assert.Contains("latest version is 2", Assert.Single(stale.Problems), StringComparison.Ordinal);
        Assert.Equal(2, await CountAsync(calendar.CalendarName, ct));
    }

    [Fact(DisplayName = "A draft in the other role is Rejected naming the family's role and writes nothing")]
    public async Task A_draft_in_the_other_role_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);

        var other = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 1, ct: ct);

        Assert.Equal(ControlAction.Rejected, other.Action);
        Assert.Contains("role is availability", Assert.Single(other.Problems), StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(calendar.CalendarName, ct));
    }

    [Fact(DisplayName = "A definition that does not compile is Rejected with its problems and writes nothing")]
    public async Task A_definition_that_does_not_compile_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("broken"));

        var rejected = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, "[]", 0, ct: ct);

        Assert.Equal(ControlAction.Rejected, rejected.Action);
        Assert.Null(rejected.Version);
        Assert.NotEmpty(rejected.Problems);
        Assert.Equal(0, await CountAsync(calendar.CalendarName, ct));
    }

    [Fact(DisplayName = "A shared calendar and a namespace's cannot share a name, in either order")]
    public async Task A_name_belongs_to_one_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("holidays");
        var shared = new CalendarLookup("sys", name);
        var local = new CalendarLookup(TestNamespace, name);
        Assert.Equal(
            ControlAction.Applied,
            (
                await Calendars.SaveDraftAsync(shared, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct)
            ).Action
        );

        var shadowing = await Calendars.SaveDraftAsync(
            local,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );

        Assert.Equal(ControlAction.Rejected, shadowing.Action);
        Assert.Contains("shared calendar in sys", Assert.Single(shadowing.Problems), StringComparison.Ordinal);
        var other = Unique("cutoffs");
        Assert.Equal(
            ControlAction.Applied,
            (
                await Calendars.SaveDraftAsync(
                    new CalendarLookup(TestNamespace, other),
                    CalendarRoleCode.Occurrence,
                    CalendarTestOps.OccurrenceDefinition,
                    0,
                    ct: ct
                )
            ).Action
        );
        var taken = await Calendars.SaveDraftAsync(
            new CalendarLookup("sys", other),
            CalendarRoleCode.Occurrence,
            CalendarTestOps.OccurrenceDefinition,
            0,
            ct: ct
        );
        Assert.Equal(ControlAction.Rejected, taken.Action);
        Assert.Contains($"namespace '{TestNamespace}'", Assert.Single(taken.Problems), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A first draft creates a namespace no worker has run in, and a role other than Occurrence or Availability throws")]
    public async Task A_first_draft_creates_its_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var fresh = Unique("no-worker-yet");

        var created = await Calendars.SaveDraftAsync(
            new CalendarLookup(fresh, "pay-days"),
            CalendarRoleCode.Occurrence,
            CalendarTestOps.OccurrenceDefinition,
            0,
            ct: ct
        );

        Assert.Equal((ControlAction.Applied, 1), (created.Action, created.Version));
        Assert.NotNull(await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == fresh).SingleOrDefaultAsync(ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Calendars.SaveDraftAsync(
                new CalendarLookup(TestNamespace, "pay-days"),
                CalendarRoleCode.Unspecified,
                CalendarTestOps.OccurrenceDefinition,
                0,
                ct: ct
            )
        );
    }

    private async Task<int> CountAsync(string calendarName, CancellationToken ct) =>
        await Db.From<CalendarEntity>().Where(c => c.NamespaceId == TestNamespaceId && c.CalendarName == calendarName).CountAsync(ct);
}
