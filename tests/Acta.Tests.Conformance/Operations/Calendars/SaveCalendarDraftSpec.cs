using System.Globalization;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>
/// Conformance for saving a catalog calendar draft: versions number from 1 under the caller's expected latest version, or
/// the latest with none; a definition the latest version already holds saves nothing; a family keeps its first role; a
/// definition that does not compile throws and writes nothing; and each save is audited against the calendar version.
/// </summary>
[ConformanceSpec(
    "calendar.save-draft",
    "A calendar draft saves as the family's next version under its expected latest",
    Area = "Scheduling",
    Contract = "A draft saves as the next version only against the family's latest and role and with new content, writes nothing otherwise, and records calendar.draft-saved."
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
        Assert.Equal("op-1", ev.Actor);
        Assert.Null(EventDetail(ev, "actor"));
    }

    [Fact(DisplayName = "A draft against the latest version saves the next one; a stale expected version is a conflict naming the latest")]
    public async Task A_draft_saves_only_against_the_latest_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct);

        var second = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, Revision(2), 1, ct: ct);
        var stale = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, Revision(3), 1, ct: ct);

        Assert.Equal((ControlAction.Applied, 2), (second.Action, second.Version));
        Assert.Equal((ControlAction.VersionConflict, 2), (stale.Action, stale.Version));
        Assert.Null(stale.ContentHash);
        Assert.Contains("latest version is 2, not 1", Assert.Single(stale.Problems), StringComparison.Ordinal);
        Assert.Equal(2, await CountAsync(calendar.CalendarName, ct));
    }

    [Fact(
        DisplayName = "A draft with no expected version saves a new family's version 1, then the latest + 1, each audited with its version"
    )]
    public async Task A_draft_with_no_expected_version_saves_against_the_latest()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("pay-days"));

        var first = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, ct: ct);
        var second = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, Revision(2), ct: ct);
        var third = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, Revision(3), ct: ct);

        Assert.Equal(
            [(ControlAction.Applied, 1), (ControlAction.Applied, 2), (ControlAction.Applied, 3)],
            new[] { first, second, third }.Select(s => (s.Action, s.Version!.Value))
        );
        Assert.Equal(3, await CountAsync(calendar.CalendarName, ct));
        Assert.Equal(["1", "2", "3"], (await DraftEventsAsync(calendar.CalendarName, ct)).Select(e => EventDetail(e, "version")));
    }

    [Fact(
        DisplayName = "A draft the latest version already holds is AlreadyInState with that version and writes no row and no event, whatever the expected version"
    )]
    public async Task A_draft_the_latest_version_holds_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        var first = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, ct: ct);
        // The same content in another layout: the save compares the canonical JSON's hash.
        var relaid = CalendarTestOps.AvailabilityDefinition.Replace(",", ", ", StringComparison.Ordinal);

        var unchanged = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, relaid, ct: ct);
        var current = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, relaid, 1, ct: ct);
        var stale = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, relaid, 0, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);
        var published = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, relaid, 7, ct: ct);

        Assert.All([unchanged, current, stale, published], save =>
            Assert.Equal((ControlAction.AlreadyInState, 1, first.ContentHash), (save.Action, save.Version, save.ContentHash))
        );
        Assert.All([unchanged, current, stale, published], save => Assert.Empty(save.Problems));
        Assert.Equal(1, await CountAsync(calendar.CalendarName, ct));
        Assert.Single(await DraftEventsAsync(calendar.CalendarName, ct));
    }

    [Fact(
        DisplayName = "A draft equal to an older version than the latest is a change: a stale expected version is a conflict, and none saves the next version"
    )]
    public async Task A_draft_equal_to_an_older_version_is_a_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        var first = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);
        var revised = CalendarTestOps.Revised(CalendarTestOps.AvailabilityDefinition, 2);
        var second = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, revised, ct: ct);
        Assert.Equal((ControlAction.Applied, 2), (second.Action, second.Version));

        var stale = await Calendars.SaveDraftAsync(
            calendar,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            1,
            ct: ct
        );
        var again = await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, ct: ct);

        Assert.Equal((ControlAction.VersionConflict, 2), (stale.Action, stale.Version));
        Assert.Equal((ControlAction.Applied, 3, first.ContentHash), (again.Action, again.Version, again.ContentHash));
        Assert.Equal(3, await CountAsync(calendar.CalendarName, ct));
    }

    [Fact(DisplayName = "A draft equal to an Archived latest version saves it again as a new draft")]
    public async Task A_draft_equal_to_an_archived_version_saves_a_new_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("office-hours"));
        await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, ct: ct);
        await CalendarTestOps.PublishAsync(Db, TestNamespaceId, calendar.CalendarName, 1, ct);
        Assert.Equal(ControlAction.Applied, (await Calendars.ArchiveAsync(calendar, ct: ct)).Action);

        var revived = await Calendars.SaveDraftAsync(
            calendar,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            ct: ct
        );

        Assert.Equal((ControlAction.Applied, 2), (revived.Action, revived.Version));
        Assert.Equal(CalendarStatusCode.Draft, (await Calendars.GetAsync(calendar, 2, ct))!.ShownStatus);
        Assert.Equal(2, (await DraftEventsAsync(calendar.CalendarName, ct)).Count);
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

    [Fact(DisplayName = "A definition that does not compile throws with every problem by its path, and writes no row and no event")]
    public async Task A_definition_that_does_not_compile_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new CalendarLookup(TestNamespace, Unique("broken"));
        const string broken =
            """{"kind":"weekly-availability","timeZone":"UTC","weekly":[{"days":["Someday"],"windows":[["09:00","25:00"]]}]}""";

        var thrown = await Assert.ThrowsAsync<CalendarDefinitionException>(async () =>
            await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, broken, ct: ct)
        );

        Assert.True(thrown.Problems.Count >= 2, string.Join(" ", thrown.Problems));
        Assert.All(thrown.Problems, problem => Assert.StartsWith("$", problem, StringComparison.Ordinal));
        Assert.Contains(thrown.Problems, problem => problem.StartsWith("$.weekly[0].days[0]", StringComparison.Ordinal));
        Assert.All(thrown.Problems, problem => Assert.Contains(problem, thrown.Message, StringComparison.Ordinal));
        Assert.Equal(0, await CountAsync(calendar.CalendarName, ct));
        Assert.Empty(await DraftEventsAsync(calendar.CalendarName, ct));
    }

    [Fact(DisplayName = "Two first drafts at the same moment in a namespace that does not exist yet both save, and create it once")]
    public async Task Two_first_drafts_in_a_new_namespace_both_save()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 20; round++)
        {
            var fresh = Unique($"new-{round}");
            var saves = await Task.WhenAll(
                Task.Run(() => SaveAsync(new CalendarLookup(fresh, "pay-days")), ct),
                Task.Run(() => SaveAsync(new CalendarLookup(fresh, "cut-offs")), ct)
            );

            Assert.All(saves, save => Assert.Equal((ControlAction.Applied, 1), (save.Action, save.Version)));
            Assert.Equal(1, await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == fresh).CountAsync(ct));
        }

        Task<SaveCalendarDraftResponse> SaveAsync(CalendarLookup calendar) =>
            Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, ct: ct).AsTask();
    }

    [Fact(
        DisplayName = "Two different drafts of one family at the same moment, with no expected version, save as the next two versions, each row and event its own"
    )]
    public async Task Two_different_drafts_of_one_family_at_once_save_as_the_next_two_versions()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 10; round++)
        {
            var calendar = new CalendarLookup(TestNamespace, Unique($"pay-days-{round}"));
            await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, ct: ct);

            var saves = await CalendarTestOps.RaceAsync(
                () => Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, Revision(2), ct: ct).AsTask(),
                () => Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, Revision(3), ct: ct).AsTask()
            );

            Assert.All(saves, save => Assert.Equal(ControlAction.Applied, save.Action));
            Assert.Equal([2, 3], saves.Select(save => save.Version!.Value).Order());
            // Each save's version holds its own definition.
            for (var i = 0; i < saves.Length; i++)
            {
                var stored = await Calendars.GetAsync(calendar, saves[i].Version, ct);
                Assert.Contains($"revision {i + 2}", stored!.Definition, StringComparison.Ordinal);
            }

            var rows = await Db.From<CalendarEntity>()
                .Where(c => c.NamespaceId == TestNamespaceId && c.CalendarName == calendar.CalendarName)
                .ToListAsync(ct);
            var events = await DraftEventsAsync(calendar.CalendarName, ct);
            Assert.Equal(["1", "2", "3"], events.Select(e => EventDetail(e, "version")).Order(StringComparer.Ordinal));
            Assert.All(events, e =>
                Assert.Equal(rows.Single(r => r.Id == e.EntityId).Version.ToString(CultureInfo.InvariantCulture), EventDetail(e, "version"))
            );
        }
    }

    [Fact(
        DisplayName = "Two identical drafts of one family at the same moment save one version, and the other answers AlreadyInState with it"
    )]
    public async Task Two_identical_drafts_of_one_family_at_once_save_one_version()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 10; round++)
        {
            var calendar = new CalendarLookup(TestNamespace, Unique($"pay-days-{round}"));
            await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, ct: ct);

            var saves = await CalendarTestOps.RaceAsync(
                () => Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, Revision(2), ct: ct).AsTask(),
                () => Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, Revision(2), ct: ct).AsTask()
            );

            Assert.Equal([ControlAction.Applied, ControlAction.AlreadyInState], saves.Select(save => save.Action).Order());
            Assert.All(saves, save => Assert.Equal(2, save.Version));
            Assert.Equal(2, await CountAsync(calendar.CalendarName, ct));
            Assert.Equal(2, (await DraftEventsAsync(calendar.CalendarName, ct)).Count);
        }
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

    /// <summary>The family's calendar.draft-saved events, oldest first.</summary>
    private async Task<IReadOnlyList<EventEntity>> DraftEventsAsync(string calendarName, CancellationToken ct)
    {
        var ids = (
            await Db.From<CalendarEntity>().Where(c => c.NamespaceId == TestNamespaceId && c.CalendarName == calendarName).ToListAsync(ct)
        )
            .Select(c => (long)c.Id)
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var events = await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Calendar && e.EventCode == EventCode.CalendarDraftSaved && ids.Contains(e.EntityId))
            .ToListAsync(ct);
        return [.. events.OrderBy(e => e.Id)];
    }

    private static string Revision(int revision) => CalendarTestOps.Revised(CalendarTestOps.OccurrenceDefinition, revision);
}
