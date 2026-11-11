using System.Globalization;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Engine.Scheduling;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>Two permanent definitions bound to the namespace's catalog by name: one sourced by a calendar, one filtered by an availability calendar.</summary>
public sealed class PublishCalendarManifest : IJobManifest
{
    public const string DaysJobName = "catalog-days";
    public const string OfficeJobName = "catalog-office";
    public const string ScheduleName = "bound";
    public const string DaysCalendar = "spec.catalog-days";
    public const string HoursCalendar = "spec.catalog-hours";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Descriptor(
                DaysJobName,
                new ScheduleDescriptor(
                    JobName: DaysJobName,
                    ScheduleName: ScheduleName,
                    Expression: DaysCalendar,
                    TimeZoneId: null,
                    MisfireStrategy: MisfireStrategyCode.CatchUpAll,
                    ExpressionKind: ScheduleExpressionKindCode.Calendar,
                    Description: null,
                    Environments: []
                )
            ),
            Descriptor(
                OfficeJobName,
                new ScheduleDescriptor(
                    JobName: OfficeJobName,
                    ScheduleName: ScheduleName,
                    Expression: "* * * * *",
                    TimeZoneId: null,
                    MisfireStrategy: MisfireStrategyCode.CatchUpAll,
                    ExpressionKind: ScheduleExpressionKindCode.Cron,
                    Description: null,
                    Environments: [],
                    AvailabilityCalendarName: HoursCalendar
                )
            ),
        ]);

    private static JobDescriptor Descriptor(string jobName, ScheduleDescriptor schedule) =>
        new(
            JobName: jobName,
            HandlerType: typeof(SlotHandler),
            MethodName: nameof(SlotHandler.Run),
            InputType: typeof(NoInput),
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            ResultPayloadFormat: null,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 1,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: AlertProfileCode.None,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: static async (_, _, ctx, ct) =>
            {
                await SlotHandler.Run(ctx, ct);
                return new JobHandlerOutput(false, null);
            },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeResult: null
        )
        {
            Schedules = [schedule],
            CreateDefaultInput = static () => new NoInput(),
            SerializeInput = null,
        };
}

/// <summary>
/// The catalog as a schedule's source, by name: the admitter resolves the name to the family's current published
/// version and fires its instants; a publication retimes every schedule that names the family from the new version
/// atomically and audits the move; an undecided overdue occurrence refuses the publication unless the
/// caller discards it, audited as a skipped range; a stale expected version is a conflict, and none is no check;
/// publishing the current version again changes nothing; a preview shows the candidate beside the current version.
/// </summary>
[ConformanceSpec(
    "calendar.publish",
    "A catalog calendar sources a schedule and a publication retimes every bound one",
    Area = "Scheduling",
    Contract = "A schedule follows the catalog's current version by name, and a publication retimes every bound schedule atomically, refusing or discarding an overdue backlog."
)]
public sealed class PublishCalendarSpec : ActaRuntimeTestBase<ConformanceFixture, PublishCalendarManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    private DateTime Start { get; set; }

    private ICalendars Calendars => Services.GetRequiredService<IActaOperations>().Calendars;

    private CalendarLookup Days => new(TestNamespace, PublishCalendarManifest.DaysCalendar);

    private CalendarLookup Hours => new(TestNamespace, PublishCalendarManifest.HoursCalendar);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = FakeClock.AtRealMinute();
        Start = Clock.UtcNow;
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    // The catalog is published before the worker binds to it: three instants a minute apart, and one window over the
    // second and third minute; the first draft creates the namespace, which no worker has run in yet.
    protected override async ValueTask BeforeWorkerStartAsync(CancellationToken ct)
    {
        await PublishAsync(Days, CalendarRoleCode.Occurrence, Instants(1, 2, 3), ct);
        await PublishAsync(Hours, CalendarRoleCode.Availability, Window(2, 4), ct);
    }

    [Fact(DisplayName = "A schedule resolves the catalog's current version by name and fires its instants")]
    public async Task A_schedule_resolves_the_current_version_and_admission_fires_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await PauseAsync(PublishCalendarManifest.OfficeJobName, ct);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.DaysJobName, ct);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(Start.AddMinutes(1), schedule.NextOccurrenceAtUtc);

        Clock.AdvanceTo(Start.AddMinutes(1).AddSeconds(30));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(Start.AddMinutes(2), Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
        var dependent = Assert.Single(
            (await Calendars.ListDependentsAsync(new ListCalendarDependentsQuery(Days), ct)).Items,
            d => d.JobName == PublishCalendarManifest.DaysJobName
        );
        Assert.Equal(CalendarRoleCode.Occurrence, dependent.Binding);
    }

    [Fact(DisplayName = "A publication with no expected version retimes every bound schedule from the new version and audits the retime")]
    public async Task A_publication_retimes_the_bound_schedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.DaysJobName, ct);
        var before = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Clock.AdvanceTo(Start.AddSeconds(30));

        var published = await PublishAsync(Days, CalendarRoleCode.Occurrence, Instants(10, 11), ct);

        Assert.Equal(2, published.Version);
        Assert.Contains(
            $"{TestNamespace}/{PublishCalendarManifest.DaysJobName}/{PublishCalendarManifest.ScheduleName}",
            published.RetimedSchedules
        );
        var after = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        // The fingerprint covers the name, not the version it resolves to.
        Assert.Equal(before.Config.TimingFingerprint, after.Config.TimingFingerprint);
        Assert.Equal(Start.AddMinutes(10), after.NextOccurrenceAtUtc);
        Assert.Equal(Start.AddMinutes(10), after.NextEvaluationAtUtc);
        var retimed = Assert.Single(await EventsAsync(slotId, EventCode.ScheduleRetimed, ct));
        Assert.Equal(("1", "2"), (EventDetail(retimed, "fromVersion"), EventDetail(retimed, "toVersion")));
        var versions = (await Calendars.ListVersionsAsync(new ListCalendarVersionsQuery(Days), ct)).Items;
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.Equal([CalendarStatusCode.Published, CalendarStatusCode.Superseded], versions.Select(v => v.Status));
    }

    // A newer build can store a code this one cannot read while both serve the namespace.
    [Theory(DisplayName = "A publication leaves a bound schedule this build cannot read as it is")]
    [InlineData("schedule_status_code = 250")]
    [InlineData("misfire_strategy_code = 250")]
    [InlineData("full_behavior_code = 250")]
    public async Task A_publication_leaves_an_unreadable_schedule_alone(string unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.DaysJobName, ct);
        await Db.ExecuteRawAsync($"UPDATE {{schema}}.schedules SET {unknown} WHERE job_id = @p_id", ct, ("@p_id", slotId));
        var before = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Clock.AdvanceTo(Start.AddSeconds(30));

        var published = await PublishAsync(Days, CalendarRoleCode.Occurrence, Instants(10, 11), ct);

        Assert.Equal(2, published.Version);
        Assert.Empty(published.RetimedSchedules);
        var after = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(before.NextOccurrenceAtUtc, after.NextOccurrenceAtUtc);
        Assert.Equal(before.Cursor.Version, after.Cursor.Version);
        Assert.Empty(await EventsAsync(slotId, EventCode.ScheduleRetimed, ct));
    }

    [Fact(DisplayName = "An overdue occurrence refuses a publication until the caller discards it as a skipped range")]
    public async Task An_overdue_occurrence_refuses_or_is_discarded()
    {
        var ct = TestContext.Current.CancellationToken;
        await PauseAsync(PublishCalendarManifest.OfficeJobName, ct);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.DaysJobName, ct);
        // The first two instants pass with no admission pass: the schedule holds an undecided overdue occurrence.
        Clock.AdvanceTo(Start.AddMinutes(2).AddSeconds(30));
        var draft = await Calendars.SaveDraftAsync(Days, CalendarRoleCode.Occurrence, Instants(20), 1, ct: ct);
        Assert.Equal(ControlAction.Applied, draft.Action);

        var refused = await Calendars.PublishAsync(Days, 2, expectedCurrentVersion: 1, OverdueDecisionCode.Refuse, ct: ct);

        Assert.Equal(ControlAction.Rejected, refused.Action);
        Assert.Contains(
            $"{TestNamespace}/{PublishCalendarManifest.DaysJobName}/{PublishCalendarManifest.ScheduleName}",
            refused.OverdueSchedules
        );
        Assert.Equal(Start.AddMinutes(1), Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
        Assert.Equal(CalendarStatusCode.Draft, (await Calendars.GetAsync(Days, 2, ct))!.ShownStatus);

        var discarded = await Calendars.PublishAsync(Days, 2, expectedCurrentVersion: 1, OverdueDecisionCode.Discard, ct: ct);

        Assert.Equal(ControlAction.Applied, discarded.Action);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(Start.AddMinutes(20), schedule.NextOccurrenceAtUtc);
        var skipped = Assert.Single(await EventsAsync(slotId, EventCode.ScheduleSkipped, ct));
        Assert.Equal(Iso(Start.AddMinutes(1)), EventDetail(skipped, "from"));
        Assert.Equal(Iso(Start.AddMinutes(2)), EventDetail(skipped, "through"));
        Assert.Equal("retimed", EventDetail(skipped, "reason"));
    }

    [Fact(DisplayName = "An apply whose plan misses a schedule naming the family rejects, and the publication plans it again")]
    public async Task A_schedule_the_plan_never_read_rejects_the_apply()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.DaysJobName, ct);
        Clock.AdvanceTo(Start.AddSeconds(30));
        var candidate = (await Calendars.SaveDraftAsync(Days, CalendarRoleCode.Occurrence, Instants(10, 11), 1, ct: ct)).Version!.Value;

        // A plan read before a deploy bound the schedule to the family: the bound schedule is not in it.
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var stale = await Db.ExecuteOneAsync(
            new StoreCommand("PublishCalendar"),
            cmd =>
            {
                cmd.Parameters.Add(
                    dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, Runtime.RegisteredNamespaceIds[TestNamespace])
                );
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, Days.CalendarName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedCurrentVersion, 1));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CandidateVersion, (int)candidate));
                cmd.AddEventFields(dialect, null, null, null);
                dialect.BindPublishCalendar(cmd, []);
            },
            DbProjectionResolver.Resolve<PublishCalendarRow>(),
            ct
        );
        Assert.Equal(ControlAction.Rejected, stale.Action);
        Assert.Equal(Start.AddMinutes(1), Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);

        var published = await Calendars.PublishAsync(Days, candidate, expectedCurrentVersion: 1, ct: ct);
        Assert.Equal(ControlAction.Applied, published.Action);
        Assert.Equal(Start.AddMinutes(10), Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
    }

    [Fact(DisplayName = "A publication is a conflict on a stale expected version and rejected for a version that is no draft")]
    public async Task A_publication_fences_on_the_versions_the_caller_saw()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(
            ControlAction.Applied,
            (await Calendars.SaveDraftAsync(Days, CalendarRoleCode.Occurrence, Instants(30), 1, ct: ct)).Action
        );

        var stale = await Calendars.PublishAsync(Days, 2, expectedCurrentVersion: 0, ct: ct);
        var missing = await Calendars.PublishAsync(Days, 9, expectedCurrentVersion: 1, ct: ct);
        var published = await Calendars.PublishAsync(Days, 2, ct: ct);
        var superseded = await Calendars.PublishAsync(Days, 1, ct: ct);

        Assert.Equal(ControlAction.VersionConflict, stale.Action);
        Assert.Contains("not 0", stale.Reason);
        Assert.Equal(ControlAction.NotFound, missing.Action);
        Assert.Equal(ControlAction.Applied, published.Action);
        Assert.Equal(ControlAction.Rejected, superseded.Action);
        Assert.Contains("is superseded", superseded.Reason, StringComparison.Ordinal);
    }

    [Fact(
        DisplayName = "Publishing the current version again is AlreadyInState and leaves every bound schedule as it was, an overdue one under Refuse included"
    )]
    public async Task Publishing_the_current_version_again_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var slotIds = new[]
        {
            await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.DaysJobName, ct),
            await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.OfficeJobName, ct),
        };
        var before = await SnapshotAsync(slotIds, nsId, ct);
        Clock.AdvanceTo(Start.AddSeconds(30));

        var days = await Calendars.PublishAsync(Days, 1, ct: ct);
        var hours = await Calendars.PublishAsync(Hours, 1, expectedCurrentVersion: 1, ct: ct);

        Assert.Equal((ControlAction.AlreadyInState, 1), (days.Action, days.Version));
        Assert.Equal((ControlAction.AlreadyInState, 1), (hours.Action, hours.Version));
        Assert.Empty(days.RetimedSchedules);
        Assert.Null(days.Reason);
        Assert.Equal(before, await SnapshotAsync(slotIds, nsId, ct));

        // The first two instants pass with no admission pass, so the schedule holds an undecided overdue occurrence; the
        // expected version is stale too.
        Clock.AdvanceTo(Start.AddMinutes(2).AddSeconds(30));
        Assert.Contains(
            (await Calendars.PreviewAsync(Days, 1, ct: ct))!.Schedules,
            s => s.JobName == PublishCalendarManifest.DaysJobName && s.Overdue
        );
        var overdue = await Calendars.PublishAsync(Days, 1, expectedCurrentVersion: 0, OverdueDecisionCode.Refuse, ct: ct);

        Assert.Equal((ControlAction.AlreadyInState, 1), (overdue.Action, overdue.Version));
        Assert.Empty(overdue.OverdueSchedules);
        Assert.Equal(before, await SnapshotAsync(slotIds, nsId, ct));
    }

    [Fact(
        DisplayName = "Two publications of one draft at the same moment publish it once: the other answers AlreadyInState, and the bound schedule is retimed once"
    )]
    public async Task Two_publications_of_one_draft_at_once_publish_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.DaysJobName, ct);
        Clock.AdvanceTo(Start.AddSeconds(30));
        for (var round = 0; round < 10; round++)
        {
            var candidate = (await Calendars.SaveDraftAsync(Days, CalendarRoleCode.Occurrence, Instants(10 + round), ct: ct))
                .Version!
                .Value;

            // Both plans can read the candidate as a draft; the one applied second then finds it published.
            var publications = await CalendarTestOps.RaceAsync(
                () => Calendars.PublishAsync(Days, candidate, ct: ct).AsTask(),
                () => Calendars.PublishAsync(Days, candidate, ct: ct).AsTask()
            );

            Assert.Equal([ControlAction.Applied, ControlAction.AlreadyInState], publications.Select(p => p.Action).Order());
            Assert.All(publications, p => Assert.Equal(candidate, p.Version));
            Assert.Equal(round + 1, await CountEventsAsync(slotId, EventCode.ScheduleRetimed, ct));
            Assert.Equal(round + 2, await CountPublishedAsync(Days.CalendarName, ct));
            Assert.Equal(Start.AddMinutes(10 + round), Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
        }
    }

    [Fact(DisplayName = "A preview shows the candidate beside the current version and the bound schedules with their totals")]
    public async Task A_preview_shows_the_candidate_beside_the_current_version()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(
            ControlAction.Applied,
            (await Calendars.SaveDraftAsync(Days, CalendarRoleCode.Occurrence, Instants(5, 6), 1, ct: ct)).Action
        );

        var preview = await Calendars.PreviewAsync(Days, 2, fromUtc: Start, count: 10, ct: ct);

        Assert.NotNull(preview);
        Assert.Equal(CalendarRoleCode.Occurrence, preview.Role);
        Assert.Equal(1, preview.CurrentVersion);
        Assert.Equal([Start.AddMinutes(5), Start.AddMinutes(6)], preview.OccurrencesUtc);
        Assert.Equal([Start.AddMinutes(1), Start.AddMinutes(2), Start.AddMinutes(3)], preview.CurrentOccurrencesUtc);
        var impact = Assert.Single(preview.Schedules, s => s.JobNamespace == TestNamespace);
        Assert.Equal((PublishCalendarManifest.DaysJobName, false), (impact.JobName, impact.Overdue));
        Assert.Equal(preview.Schedules.Count, preview.SchedulesTotal);
        Assert.Equal(preview.Schedules.Count(s => s.Overdue), preview.OverdueTotal);
    }

    [Fact(DisplayName = "An availability publication moves a cron schedule's next tick into the new window")]
    public async Task An_availability_publication_moves_the_next_tick()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, PublishCalendarManifest.OfficeJobName, ct);
        Assert.Equal(Start.AddMinutes(2), Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
        Clock.AdvanceTo(Start.AddSeconds(30));

        var published = await PublishAsync(Hours, CalendarRoleCode.Availability, Window(6, 8), ct);

        Assert.Equal(2, published.Version);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(Start.AddMinutes(6), schedule.NextOccurrenceAtUtc);
        Assert.Equal(1, await CountEventsAsync(slotId, EventCode.ScheduleRetimed, ct));
    }

    // A deploy step's form: no expected version on either call.
    private async Task<PublishCalendarResponse> PublishAsync(
        CalendarLookup calendar,
        CalendarRoleCode role,
        string definition,
        CancellationToken ct
    )
    {
        var draft = await Calendars.SaveDraftAsync(calendar, role, definition, ct: ct);
        Assert.Equal(ControlAction.Applied, draft.Action);
        var published = await Calendars.PublishAsync(calendar, draft.Version!.Value, ct: ct);
        Assert.Equal(ControlAction.Applied, published.Action);
        return published;
    }

    // How many calendar.published events the family's versions hold.
    private async Task<int> CountPublishedAsync(string calendarName, CancellationToken ct)
    {
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var ids = (await Db.From<CalendarEntity>().Where(c => c.NamespaceId == nsId && c.CalendarName == calendarName).ToListAsync(ct))
            .Select(c => (long)c.Id)
            .ToList();
        return await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Calendar && e.EventCode == EventCode.CalendarPublished && ids.Contains(e.EntityId))
            .CountAsync(ct);
    }

    // The bound schedules' versions and cursors, and how many events their jobs and the namespace's calendars hold.
    private async Task<string> SnapshotAsync(IReadOnlyList<long> slotIds, int namespaceId, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var slotId in slotIds)
        {
            lines.AddRange(
                (await ScheduleTestOps.ReadAsync(Db, slotId, ct)).Select(s =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{slotId}/{s.ScheduleName} v{s.Version}/{s.Cursor.Version} {s.NextOccurrenceAtUtc:O} {s.NextEvaluationAtUtc:O} {s.EvaluationStatus}"
                    )
                )
            );
        }

        var ids = slotIds.ToList();
        var jobEvents = await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && ids.Contains(e.EntityId)).CountAsync(ct);
        var calendarEvents = await Db.From<EventEntity>()
            .Where(e => e.NamespaceId == namespaceId && e.EntityCode == EntityCode.Calendar)
            .CountAsync(ct);
        lines.Add($"{jobEvents} job events, {calendarEvents} calendar events");
        return string.Join("\n", lines);
    }

    private string Instants(params int[] minutes) =>
        "{\"kind\":\"explicit-instants\",\"instants\":["
        + string.Join(
            ",",
            minutes.Select(m => "\"" + Start.AddMinutes(m).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z\"")
        )
        + "]}";

    /// <summary>One window from <paramref name="fromMinute"/> to <paramref name="toMinute"/> after the start, as that date's override in UTC.</summary>
    private string Window(int fromMinute, int toMinute)
    {
        var from = Start.AddMinutes(fromMinute);
        var to = Start.AddMinutes(toMinute);
        return "{\"kind\":\"weekly-availability\",\"timeZone\":\"UTC\",\"dateOverrides\":{\""
            + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + "\":[[\""
            + from.ToString("HH:mm", CultureInfo.InvariantCulture)
            + "\",\""
            + to.ToString("HH:mm", CultureInfo.InvariantCulture)
            + "\"]]}}";
    }

    private async Task PauseAsync(string jobName, CancellationToken ct)
    {
        var lookup = new ScheduleLookup(TestNamespace, jobName, PublishCalendarManifest.ScheduleName);
        Assert.Equal(
            ControlAction.Applied,
            (await Services.GetRequiredService<IActaOperations>().Schedules.PauseAsync(lookup, ct: ct)).Action
        );
    }

    private async Task<IReadOnlyList<EventEntity>> EventsAsync(long jobId, EventCode code, CancellationToken ct) =>
        [
            .. (await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct)).Where(e =>
                e.EventCode == code
            ),
        ];

    private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
