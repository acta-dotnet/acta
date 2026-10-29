using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>Every-minute schedules under the catch-up and expiration policies, one definition per fact.</summary>
public sealed class MisfirePolicyManifest : IJobManifest
{
    public const string BacklogSlotJobName = "backlog-slot";
    public const string BacklogRootsJobName = "backlog-roots";
    public const string ExpireDiscardJobName = "expire-discard";
    public const string ExpirePauseJobName = "expire-pause";
    public const string ScheduleName = "every-minute";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Descriptor(BacklogSlotJobName, ScheduledDeliveryCode.Permanent, 1, null, ScheduleExpireActionCode.Unspecified),
            Descriptor(BacklogRootsJobName, ScheduledDeliveryCode.PerOccurrence, 2, null, ScheduleExpireActionCode.Unspecified),
            Descriptor(ExpireDiscardJobName, ScheduledDeliveryCode.Permanent, 1, 120, ScheduleExpireActionCode.Discard),
            Descriptor(ExpirePauseJobName, ScheduledDeliveryCode.Permanent, 1, 120, ScheduleExpireActionCode.Pause),
        ]);

    private static JobDescriptor Descriptor(
        string jobName,
        ScheduledDeliveryCode delivery,
        int maxOutstanding,
        int? expireAfterSeconds,
        ScheduleExpireActionCode expireAction
    ) =>
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
            ScheduledDelivery: delivery,
            Invoker: static async (_, _, ctx, ct) =>
            {
                await SlotHandler.Run(ctx, ct);
                return new JobHandlerOutput(false, null);
            },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeOutput: null
        )
        {
            Schedules =
            [
                new ScheduleDescriptor(
                    JobName: jobName,
                    ScheduleName: ScheduleName,
                    Expression: "* * * * *",
                    TimeZoneId: null,
                    MisfireStrategy: MisfireStrategyCode.CatchUpAll,
                    ExpressionKind: ScheduleExpressionKindCode.Cron,
                    Description: null,
                    Environments: [],
                    MaxOutstanding: maxOutstanding,
                    FullBehavior: ScheduleFullBehaviorCode.Wait,
                    ExpireAfterSeconds: expireAfterSeconds,
                    ExpireAction: expireAction
                ),
            ],
            CreateDefaultInput = static () => new NoInput(),
            SerializeInput = null,
        };
}

/// <summary>
/// CatchUpAll drains a backlog one occurrence per settlement, on a slot and across a per-occurrence schedule's
/// capacity; expiration decides the occurrences older than the age without work, audited with the reason, and under
/// Pause stops the schedule for an operator, whose resume expires what is stale by then once more without pausing again.
/// </summary>
[ConformanceSpec(
    "schedule.misfire-policies",
    "CatchUpAll drains a backlog in order, expiration discards or pauses stale work",
    Area = "Scheduling",
    Contract = "CatchUpAll admits a backlog one occurrence per settlement within capacity, and an occurrence older than the expiry is skipped as expired, pausing under Pause."
)]
public sealed class MisfirePolicySpec : ActaRuntimeTestBase<ConformanceFixture, MisfirePolicyManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // The clock starts at the real minute: settlement stamps the DB clock, which this clock must not trail.
        var now = DateTime.UtcNow;
        Clock = new FakeClock(new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc));
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A slot under CatchUpAll fires each missed occurrence in order, one per settlement, and then waits for the next")]
    public async Task A_slot_drains_its_backlog_one_firing_per_settlement()
    {
        var ct = TestContext.Current.CancellationToken;
        await IsolateAsync(MisfirePolicyManifest.BacklogSlotJobName, ct);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, MisfirePolicyManifest.BacklogSlotJobName, ct);
        var first = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc!.Value;
        Clock.AdvanceTo(first.AddMinutes(2).AddSeconds(30));

        for (var i = 0; i < 3; i++)
        {
            await Runtime.AdmitOnceAsync(TestNamespace, ct);
            Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
            Assert.Equal(first.AddMinutes(i), (await OccurrenceAsync(slotId, ct)));
            // The rest of the backlog is due at once, so the next pass comes straight back, finds the slot busy, and
            // parks the schedule until the firing settles; after the last missed occurrence the next one is ahead.
            var admitted = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
            Assert.Equal(first.AddMinutes(i + 1), admitted.NextOccurrenceAtUtc);
            Assert.Equal(first.AddMinutes(i + 1), admitted.NextEvaluationAtUtc);
            var parked = await Runtime.AdmitOnceAsync(TestNamespace, ct);
            Assert.Equal((0, 0), (parked.Admitted, parked.Stale));
            Assert.Equal(
                i < 2 ? EvaluationStatusCode.WaitingForCapacity : EvaluationStatusCode.Scheduled,
                Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).EvaluationStatus
            );
            Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        }

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(first.AddMinutes(3), schedule.NextOccurrenceAtUtc);
        Assert.Equal(EvaluationStatusCode.Scheduled, schedule.EvaluationStatus);
        Assert.Equal(3, await CountEventsAsync(slotId, EventCode.ScheduleAdmitted, ct));
    }

    [Fact(
        DisplayName = "A per-occurrence schedule under CatchUpAll admits one root per pass up to its capacity, then waits for a settlement"
    )]
    public async Task A_per_occurrence_schedule_drains_its_backlog_within_capacity()
    {
        var ct = TestContext.Current.CancellationToken;
        await IsolateAsync(MisfirePolicyManifest.BacklogRootsJobName, ct);
        var (schedule, state) = await ScheduleAsync(MisfirePolicyManifest.BacklogRootsJobName, ct);
        var first = state.NextOccurrenceAtUtc!.Value;
        Clock.AdvanceTo(first.AddMinutes(2).AddSeconds(30));

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        var roots = await RootsAsync(schedule.Id, ct);
        Assert.Equal(2, roots.Count);
        Assert.Equal(first, await OccurrenceAsync(roots[0].Id, ct));
        Assert.Equal(first.AddMinutes(1), await OccurrenceAsync(roots[1].Id, ct));

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(2, (await RootsAsync(schedule.Id, ct)).Count);
        Assert.Equal(
            EvaluationStatusCode.WaitingForCapacity,
            (await ScheduleAsync(MisfirePolicyManifest.BacklogRootsJobName, ct)).State.EvaluationStatus
        );

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(roots[0].Id, ct));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        roots = await RootsAsync(schedule.Id, ct);
        Assert.Equal(3, roots.Count);
        Assert.Equal(first.AddMinutes(2), await OccurrenceAsync(roots[2].Id, ct));
        (_, state) = await ScheduleAsync(MisfirePolicyManifest.BacklogRootsJobName, ct);
        Assert.Equal(2, state.OutstandingCount);
        Assert.Equal(first.AddMinutes(3), state.NextOccurrenceAtUtc);
    }

    [Fact(DisplayName = "Under Discard the occurrences older than the expiry are skipped with the reason expired and the rest fire")]
    public async Task Expiration_under_discard_skips_the_stale_backlog()
    {
        var ct = TestContext.Current.CancellationToken;
        await IsolateAsync(MisfirePolicyManifest.ExpireDiscardJobName, ct);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, MisfirePolicyManifest.ExpireDiscardJobName, ct);
        var first = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc!.Value;
        Clock.AdvanceTo(first.AddMinutes(5));

        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        // Six occurrences were due; the four older than two minutes expired and the first fresh one fired.
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(first.AddMinutes(4), await OccurrenceAsync(slotId, ct));
        var expired = Assert.Single(await ExpiredEventsAsync(slotId, ct));
        Assert.Equal(Iso(first), EventDetail(expired, "from"));
        Assert.Equal(Iso(first.AddMinutes(3)), EventDetail(expired, "through"));
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
        Assert.Equal(first.AddMinutes(5), schedule.NextOccurrenceAtUtc);
    }

    [Fact(
        DisplayName = "Under Pause an expiry pauses the schedule with the reason, and a resume expires what is stale by then without pausing again"
    )]
    public async Task Expiration_under_pause_stops_the_schedule_until_a_resume()
    {
        var ct = TestContext.Current.CancellationToken;
        await IsolateAsync(MisfirePolicyManifest.ExpirePauseJobName, ct);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, MisfirePolicyManifest.ExpirePauseJobName, ct);
        var first = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc!.Value;
        Clock.AdvanceTo(first.AddMinutes(5));

        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        Assert.Equal(first.AddMinutes(4), await OccurrenceAsync(slotId, ct));
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        Assert.Equal("Paused by the expiration policy: an occurrence expired unadmitted.", schedule.PauseMessage);
        Assert.Null(schedule.NextEvaluationAtUtc);
        Assert.Equal("expired", EventDetail(await ReadSingleEventAsync(slotId, EventCode.SchedulePaused, ct), "reason"));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));

        // Resumed four minutes later, the schedule expires the part of the backlog that is stale by then, fires the
        // next fresh occurrence, and stays Active.
        Clock.AdvanceTo(first.AddMinutes(9));
        var lookup = new ScheduleLookup(TestNamespace, MisfirePolicyManifest.ExpirePauseJobName, MisfirePolicyManifest.ScheduleName);
        Assert.Equal(ControlAction.Applied, (await Schedules.ResumeAsync(lookup, ct: ct)).Action);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        Assert.Equal(first.AddMinutes(8), await OccurrenceAsync(slotId, ct));
        Assert.Equal(2, (await ExpiredEventsAsync(slotId, ct)).Count);
        schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
        Assert.Equal(1, await CountEventsAsync(slotId, EventCode.SchedulePaused, ct));
    }

    /// <summary>Pauses every other definition's schedule, so a pass over the shared namespace decides this fact's alone.</summary>
    private async Task IsolateAsync(string jobName, CancellationToken ct)
    {
        foreach (
            var other in new[]
            {
                MisfirePolicyManifest.BacklogSlotJobName,
                MisfirePolicyManifest.BacklogRootsJobName,
                MisfirePolicyManifest.ExpireDiscardJobName,
                MisfirePolicyManifest.ExpirePauseJobName,
            }
        )
        {
            if (other != jobName)
            {
                var lookup = new ScheduleLookup(TestNamespace, other, MisfirePolicyManifest.ScheduleName);
                Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(lookup, ct: ct)).Action);
            }
        }
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

    private async Task<IReadOnlyList<EventEntity>> ExpiredEventsAsync(long jobId, CancellationToken ct) =>
        [
            .. (await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct)).Where(e =>
                e.EventCode == EventCode.ScheduleSkipped && EventDetail(e, "reason") == "expired"
            ),
        ];

    private async Task<(ScheduleEntity Config, ScheduleStateEntity State)> ScheduleAsync(string jobName, CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definition = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == jobName)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(definition);
        var config = await Db.From<ScheduleEntity>()
            .Where(s => s.DefinitionId == definition.Id && s.ScheduleName == MisfirePolicyManifest.ScheduleName)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(config);
        var state = await Db.From<ScheduleStateEntity>().Where(s => s.Id == config.Id).SingleOrDefaultAsync(ct);
        Assert.NotNull(state);
        return (config, state);
    }

    private async Task<IReadOnlyList<JobEntity>> RootsAsync(int scheduleId, CancellationToken ct) =>
        [.. (await Db.From<JobEntity>().Where(j => j.ScheduleId == scheduleId).ToListAsync(ct)).OrderBy(j => j.Id)];

    private static string Iso(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
}
