using System.Globalization;
using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>Two permanent definitions: one fired by a code calendar, one by an every-minute cron under an availability calendar.</summary>
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
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            OutputPayloadFormat: null,
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
            SerializeOutput: null
        )
        {
            Schedules = [schedule],
            CreateDefaultInput = static () => new NoInput(),
            SerializeInput = null,
        };
}

/// <summary>
/// A code calendar is a schedule's source and an availability calendar its filter: registration seeds the cursor from
/// the bound source, admission fires the calendar's instants in order and exhausts after the last, an every-minute cron
/// fires only the ticks inside the open window and exhausts when none remain, and preview walks the same source. A
/// name the process declares resolves there: a catalog family under it moves no schedule and is never read, and a host
/// without the code calendar leaves the schedule to the hosts that have it.
/// </summary>
[ConformanceSpec(
    "schedule.calendars",
    "A registered calendar sources a schedule, an availability calendar filters one",
    Area = "Scheduling",
    Contract = "A registered calendar fires its instants in order and exhausts, an availability calendar omits ticks outside its windows, a declared name shadows the catalog."
)]
public sealed class CalendarScheduleSpec : ActaRuntimeTestBase<ConformanceFixture, CalendarScheduleManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    private DateTime Start { get; set; }

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private ICalendars Calendars => Services.GetRequiredService<IActaOperations>().Calendars;

    private CalendarLookup Family => new(TestNamespace, CalendarScheduleManifest.CalendarName);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // The clock starts at the real minute: settlement stamps the DB clock, which this clock must not trail.
        var now = DateTime.UtcNow;
        Start = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
        Clock = new FakeClock(Start);
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    // Three instants a minute apart, and one open window covering the second and third minute only.
    protected override void ConfigureActa(IActaBuilder acta) =>
        acta.AddCalendar(
                CalendarScheduleManifest.CalendarName,
                new FixedCalendar(Start.AddMinutes(1), Start.AddMinutes(2), Start.AddMinutes(3))
            )
            .AddAvailabilityCalendar(CalendarScheduleManifest.AvailabilityName, new OneWindow(Start.AddMinutes(2), Start.AddMinutes(4)));

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
        Assert.Equal(EvaluationStatusCode.Exhausted, schedule.EvaluationStatus);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(3, await CountEventsAsync(slotId, EventCode.ScheduleAdmitted, ct));
        Assert.Empty((await Schedules.PreviewAsync(lookup, 10, ct))!.NextOccurrencesUtc);
    }

    [Fact(DisplayName = "A catalog family under a name the process declares moves no schedule and is never read")]
    public async Task A_catalog_family_under_a_declared_name_moves_no_schedule_and_is_never_read()
    {
        var ct = TestContext.Current.CancellationToken;
        await PauseAsync(CalendarScheduleManifest.OfficeJobName, ct);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, CalendarScheduleManifest.CalendarJobName, ct);
        // The first instant is overdue and undecided: a publication the schedule followed would refuse it.
        Clock.AdvanceTo(Start.AddMinutes(1).AddSeconds(30));
        var draft = await Calendars.SaveDraftAsync(Family, CalendarRoleCode.Occurrence, Instants(10, 11), 0, ct: ct);
        Assert.Equal(ControlAction.Applied, draft.Action);

        var published = await Calendars.PublishAsync(Family, 0, 1, ct: ct);

        Assert.Equal(ControlAction.Applied, published.Action);
        Assert.Empty(published.RetimedSchedules);
        Assert.Equal(Start.AddMinutes(1), Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleRetimed, ct));

        // A version this build cannot compile, as one published from a newer build is: the code calendar shadows it,
        // so no pass reads it.
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var foreign = Encoding.UTF8.GetBytes("{\"kind\":\"from-a-newer-build\"}");
        await Db.From<CalendarEntity>()
            .Where(c => c.NamespaceId == namespaceId && c.CalendarName == CalendarScheduleManifest.CalendarName)
            .UpdateOnlyAsync(() => new CalendarEntity { Definition = foreign }, ct);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(Start.AddMinutes(1), await OccurrenceAsync(slotId, ct));
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleSkipped, ct));
    }

    [Fact(DisplayName = "A publication from a host without the code calendar leaves the schedule to the hosts that have it")]
    public async Task A_publication_from_a_host_without_the_code_calendar_leaves_the_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        await PauseAsync(CalendarScheduleManifest.OfficeJobName, ct);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, CalendarScheduleManifest.CalendarJobName, ct);
        Clock.AdvanceTo(Start.AddMinutes(1).AddSeconds(30));
        // An operations host without the worker's manifest: there the name resolves to nothing before this publication,
        // and the overdue first instant has no source to walk.
        var services = new ServiceCollection();
        services.AddSingleton<IActaClock>(Clock);
        services.UseActa(j => Fixture.ApplyProvider(j, Schema.SchemaName));
        await using var host = services.BuildServiceProvider();
        var calendars = host.GetRequiredService<IActaOperations>().Calendars;
        var draft = await calendars.SaveDraftAsync(Family, CalendarRoleCode.Occurrence, Instants(10, 11), 0, ct: ct);
        Assert.Equal(ControlAction.Applied, draft.Action);

        var published = await calendars.PublishAsync(Family, 0, 1, OverdueDecisionCode.Discard, ct: ct);

        Assert.Equal(ControlAction.Applied, published.Action);
        Assert.Empty(published.RetimedSchedules);
        Assert.Equal(Start.AddMinutes(1), Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleSkipped, ct));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(Start.AddMinutes(1), await OccurrenceAsync(slotId, ct));
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
        Assert.Equal(EvaluationStatusCode.Exhausted, schedule.EvaluationStatus);
        Assert.Equal(2, await CountEventsAsync(slotId, EventCode.ScheduleAdmitted, ct));
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleSkipped, ct));
    }

    /// <summary>The namespace is shared, so a fact pauses the other definition's schedule.</summary>
    private async Task PauseAsync(string jobName, CancellationToken ct)
    {
        var lookup = new ScheduleLookup(TestNamespace, jobName, CalendarScheduleManifest.ScheduleName);
        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(lookup, ct: ct)).Action);
    }

    private string Instants(params int[] minutes) =>
        "{\"kind\":\"explicit-instants\",\"instants\":["
        + string.Join(
            ",",
            minutes.Select(m => "\"" + Start.AddMinutes(m).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z\"")
        )
        + "]}";

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

    private sealed class FixedCalendar(params DateTime[] instants) : IActaCalendar
    {
        public DateTime? GetNextOccurrence(CalendarContext context)
        {
            foreach (var instant in instants)
            {
                if (instant > context.AfterUtc)
                {
                    return instant;
                }
            }

            return null;
        }
    }

    private sealed class OneWindow(DateTime startUtc, DateTime endUtc) : IActaAvailabilityCalendar
    {
        public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context) =>
            endUtc > context.AtOrAfterUtc ? new CalendarWindow(startUtc, endUtc) : null;
    }
}
