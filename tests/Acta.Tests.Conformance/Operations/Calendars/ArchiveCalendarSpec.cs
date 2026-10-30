using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>
/// Conformance for archiving a calendar family's current version: only the expected current version archives, only
/// while no live schedule of an active definition names the family, and the archive is audited.
/// </summary>
[ConformanceSpec(
    "calendar.archive",
    "A calendar family's current version archives when nothing live depends on it",
    Area = "Scheduling",
    Contract = "Archive retires the expected current version and records calendar.archived, and is Rejected while a live schedule binds the family."
)]
public sealed class ArchiveCalendarSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "Archiving a family with no current version, or an unknown family, is NotFound")]
    public async Task Archiving_without_a_current_version_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);

        var draftOnly = await Calendars.ArchiveAsync(calendar, 1, ct: ct);
        var unknown = await Calendars.ArchiveAsync(new CalendarLookup(TestNamespace, Unique("no-such-calendar")), 1, ct: ct);

        Assert.Equal((ControlAction.NotFound, null, 0), (draftOnly.Action, draftOnly.Version, draftOnly.DependentCount));
        Assert.Equal(ControlAction.NotFound, unknown.Action);
        Assert.Equal(CalendarStatusCode.Draft, (await Calendars.GetAsync(calendar, ct: ct))!.Versions.Single().Status);
    }

    [Fact(DisplayName = "Archiving the current version applies, leaves the family without one, and records calendar.archived")]
    public async Task Archiving_the_current_version_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);

        var archived = await Calendars.ArchiveAsync(calendar, 1, "retired", "op-1", ct);

        Assert.Equal((ControlAction.Applied, 1, 0), (archived.Action, archived.Version, archived.DependentCount));
        var read = await Calendars.GetAsync(calendar, ct: ct);
        Assert.Null(read!.CurrentVersion);
        Assert.Equal(CalendarStatusCode.Archived, read.Versions.Single().Status);
        var row = await Db.From<CalendarEntity>()
            .Where(c => c.NamespaceId == TestNamespaceId && c.CalendarName == calendar.CalendarName)
            .SingleOrDefaultAsync(ct);
        var ev = Assert.Single(
            await Db.From<EventEntity>()
                .Where(e => e.NamespaceId == TestNamespaceId && e.EventCode == EventCode.CalendarArchived)
                .ToListAsync(ct)
        );
        Assert.Equal((EntityCode.Calendar, (long)row!.Id), (ev.EntityCode, ev.EntityId));
        Assert.Equal("retired", ev.EventMessage);
        Assert.Equal(
            (calendar.CalendarName, "1", "op-1"),
            (EventDetail(ev, "calendar"), EventDetail(ev, "version"), EventDetail(ev, "actor"))
        );
    }

    [Fact(
        DisplayName = "A stale expected version, or a live schedule naming the family, rejects the archive until the schedule is orphaned"
    )]
    public async Task A_stale_version_or_a_live_dependent_rejects_the_archive()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);
        var defId = await CalendarTestOps.BindAvailabilityAsync(
            Services,
            TestNamespaceId,
            Unique("support-sweep"),
            calendar.CalendarName,
            ["tick"],
            ct
        );

        var stale = await Calendars.ArchiveAsync(calendar, 2, ct: ct);
        var bound = await Calendars.ArchiveAsync(calendar, 1, ct: ct);
        await CalendarTestOps.RegisterAsync(Services, TestNamespaceId, defId, Unique("other-hours"), ["other"], ct);
        var orphaned = await Calendars.ArchiveAsync(calendar, 1, ct: ct);

        Assert.Equal((ControlAction.Rejected, 1), (stale.Action, stale.Version));
        Assert.Equal((ControlAction.Rejected, 1, 1), (bound.Action, bound.Version, bound.DependentCount));
        Assert.Equal((ControlAction.Applied, 0), (orphaned.Action, orphaned.DependentCount));
        Assert.Equal(
            1,
            await Db.From<EventEntity>()
                .Where(e => e.NamespaceId == TestNamespaceId && e.EventCode == EventCode.CalendarArchived)
                .CountAsync(ct)
        );
    }
}
