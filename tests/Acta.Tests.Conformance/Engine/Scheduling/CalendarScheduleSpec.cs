using System.Globalization;
using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>Two permanent definitions: one fired by a catalog calendar, one by an every-minute cron under an availability calendar.</summary>
public sealed class CalendarScheduleManifest : IJobManifest
{
    public const string CalendarJobName = "calendar-days";
    public const string OfficeJobName = "office-hours";
    public const string ScheduleName = "bound";
    public const string CalendarName = "spec.settlement-days";
    public const string AvailabilityName = "spec.office-hours";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Descriptor(
                CalendarJobName,
                new ScheduleDescriptor(
                    JobName: CalendarJobName,
                    ScheduleName: ScheduleName,
                    Expression: CalendarName,
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
                    AvailabilityCalendarName: AvailabilityName
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
/// A published catalog calendar is a schedule's source and an availability calendar its filter: registration seeds the
/// cursor from the bound source, admission fires the calendar's instants in order and exhausts after the last, an
/// every-minute cron fires only the ticks inside the open window and exhausts when none remain, and preview walks the
/// same source.
/// </summary>
[ConformanceSpec(
    "schedule.calendars",
    "A published calendar sources a schedule, an availability calendar filters one",
    Area = "Scheduling",
    Contract = "A published calendar fires its instants in order and exhausts, an availability calendar omits ticks outside its windows."
)]
public sealed class CalendarScheduleSpec : ActaRuntimeTestBase<ConformanceFixture, CalendarScheduleManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    private DateTime Start { get; set; }

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private ICalendars Calendars => Services.GetRequiredService<IActaOperations>().Calendars;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = FakeClock.AtRealMinute();
        Start = Clock.UtcNow;
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    // The catalog is published before the worker binds to it: three instants a minute apart, and one open window
    // covering the second and third minute only.
    protected override async ValueTask BeforeWorkerStartAsync(CancellationToken ct)
    {
        await PublishAsync(CalendarScheduleManifest.CalendarName, CalendarRoleCode.Occurrence, Instants(1, 2, 3), ct);
        await PublishAsync(CalendarScheduleManifest.AvailabilityName, CalendarRoleCode.Availability, Window(2, 4), ct);
    }

    [Fact(DisplayName = "A calendar schedule fires each of the calendar's instants in order and exhausts after the last")]
    public async Task A_calendar_schedule_fires_its_instants_in_order_and_exhausts()
    {
        var ct = TestContext.Current.CancellationToken;
        await PauseAsync(CalendarScheduleManifest.OfficeJobName, ct);
        var lookup = new ScheduleLookup(TestNamespace, CalendarScheduleManifest.CalendarJobName, CalendarScheduleManifest.ScheduleName);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, CalendarScheduleManifest.CalendarJobName, ct);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleExpressionKindCode.Calendar, schedule.ExpressionKind);
        Assert.Equal(Start.AddMinutes(1), schedule.NextOccurrenceAtUtc);
        var preview = await Schedules.PreviewAsync(lookup, 10, ct);
        Assert.NotNull(preview);
        Assert.Equal([Start.AddMinutes(1), Start.AddMinutes(2), Start.AddMinutes(3)], preview.NextOccurrencesUtc);

        Clock.AdvanceTo(Start.AddMinutes(3).AddSeconds(30));
        for (var i = 1; i <= 3; i++)
        {
            await Runtime.AdmitOnceAsync(TestNamespace, ct);
            Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
            Assert.Equal(Start.AddMinutes(i), await OccurrenceAsync(slotId, ct));
            Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        }

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Null(schedule.NextOccurrenceAtUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Exhausted, schedule.EvaluationStatus);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(3, await CountEventsAsync(slotId, EventCode.ScheduleAdmitted, ct));
        Assert.Empty((await Schedules.PreviewAsync(lookup, 10, ct))!.NextOccurrencesUtc);
    }

    [Fact(DisplayName = "An availability calendar omits the cron ticks outside its window and never shifts them")]
    public async Task An_availability_calendar_omits_the_ticks_outside_its_window()
    {
        var ct = TestContext.Current.CancellationToken;
        await PauseAsync(CalendarScheduleManifest.CalendarJobName, ct);
        var lookup = new ScheduleLookup(TestNamespace, CalendarScheduleManifest.OfficeJobName, CalendarScheduleManifest.ScheduleName);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, CalendarScheduleManifest.OfficeJobName, ct);

        // Registration seeds at the first open tick: the first minute is closed and is never an occurrence.
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(Start.AddMinutes(2), schedule.NextOccurrenceAtUtc);
        var preview = await Schedules.PreviewAsync(lookup, 10, ct);
        Assert.NotNull(preview);
        Assert.Equal([Start.AddMinutes(2), Start.AddMinutes(3)], preview.NextOccurrencesUtc);

        Clock.AdvanceTo(Start.AddMinutes(5));
        for (var i = 2; i <= 3; i++)
        {
            await Runtime.AdmitOnceAsync(TestNamespace, ct);
            Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
            Assert.Equal(Start.AddMinutes(i), await OccurrenceAsync(slotId, ct));
            Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        }

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Null(schedule.NextOccurrenceAtUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Exhausted, schedule.EvaluationStatus);
        Assert.Equal(2, await CountEventsAsync(slotId, EventCode.ScheduleAdmitted, ct));
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleSkipped, ct));
    }

    [Fact(DisplayName = "A version this build cannot read parks the schedule that names it and no other")]
    public async Task A_version_this_build_cannot_read_parks_only_the_schedule_that_names_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendarSlot = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, CalendarScheduleManifest.CalendarJobName, ct);
        var officeSlot = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, CalendarScheduleManifest.OfficeJobName, ct);
        // A second version becomes current, then reads as one a newer build published: a kind this build does not know.
        var family = new CalendarLookup(TestNamespace, CalendarScheduleManifest.CalendarName);
        Assert.Equal(
            ControlAction.Applied,
            (await Calendars.SaveDraftAsync(family, CalendarRoleCode.Occurrence, Instants(1, 2, 4), 1, ct: ct)).Action
        );
        Assert.Equal(ControlAction.Applied, (await Calendars.PublishAsync(family, 1, 2, ct: ct)).Action);
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var foreign = Encoding.UTF8.GetBytes("{\"kind\":\"from-a-newer-build\"}");
        await Db.From<CalendarEntity>()
            .Where(c => c.NamespaceId == namespaceId && c.CalendarName == CalendarScheduleManifest.CalendarName && c.Version == 2)
            .UpdateOnlyAsync(() => new CalendarEntity { Definition = foreign }, ct);

        Clock.AdvanceTo(Start.AddMinutes(2).AddSeconds(30));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        var parked = Assert.Single(await ScheduleTestOps.ReadAsync(Db, calendarSlot, ct));
        Assert.Equal(ScheduleEvaluationStatusCode.EvaluationError, parked.EvaluationStatus);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(calendarSlot, ct)).Status);
        // The pass went on past it: the other schedule's first open tick was admitted.
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(officeSlot, ct)).Status);
        Assert.Equal(Start.AddMinutes(2), await OccurrenceAsync(officeSlot, ct));
    }

    /// <summary>The namespace is shared, so a fact pauses the other definition's schedule.</summary>
    private async Task PauseAsync(string jobName, CancellationToken ct)
    {
        var lookup = new ScheduleLookup(TestNamespace, jobName, CalendarScheduleManifest.ScheduleName);
        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(lookup, ct: ct)).Action);
    }

    /// <summary>Saves the family's first version and publishes it; the first draft creates the namespace, which no worker has run in yet.</summary>
    private async Task PublishAsync(string calendarName, CalendarRoleCode role, string definition, CancellationToken ct)
    {
        var calendar = new CalendarLookup(TestNamespace, calendarName);
        var draft = await Calendars.SaveDraftAsync(calendar, role, definition, 0, ct: ct);
        Assert.Equal(ControlAction.Applied, draft.Action);
        Assert.Equal(ControlAction.Applied, (await Calendars.PublishAsync(calendar, 0, draft.Version!.Value, ct: ct)).Action);
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

    private async Task<DateTime> OccurrenceAsync(long jobId, CancellationToken ct)
    {
        var checkpoint = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Firing)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(checkpoint);
        var firing = FiringEnvelope.FromJson(checkpoint.Value);
        Assert.NotNull(firing);
        return Assert.Single(firing.Contributors).OccurrenceAtUtc;
    }
}
