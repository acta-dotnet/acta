using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>Throws on every attempt, so every firing fails for good at MaxAttempts = 1.</summary>
internal static class FailingPingHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => throw new InvalidOperationException("the firing fails");
}

/// <summary>Two scheduled definitions that always fail, one under each firing failure policy.</summary>
public sealed class FiringFailurePolicyManifest : IJobManifest
{
    public const string PausingJobName = "policy-pause";
    public const string ContinuingJobName = "policy-continue";
    public const string ScheduleName = "every-5-minutes";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Descriptor(PausingJobName, FiringFailurePolicyCode.PauseScheduling),
            Descriptor(ContinuingJobName, FiringFailurePolicyCode.Continue),
        ]);

    private static JobDescriptor Descriptor(string jobName, FiringFailurePolicyCode policy) =>
        new(
            JobName: jobName,
            HandlerType: typeof(FailingPingHandler),
            MethodName: nameof(FailingPingHandler.Run),
            InputType: typeof(NoInput),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            OutputPayloadFormat: null,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 1,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: AlertProfileCode.None,
            FiringFailurePolicy: policy,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: static async (_, _, ctx, ct) =>
            {
                await FailingPingHandler.Run(ctx, ct);
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
                    Expression: "*/5 * * * *",
                    TimeZoneId: null,
                    MisfireStrategy: MisfireStrategyCode.Skip,
                    ExpressionKind: ScheduleExpressionKindCode.Cron,
                    Description: null,
                    Environments: []
                ),
            ],
            CreateDefaultInput = static () => new NoInput(),
            SerializeInput = null,
        };
}

/// <summary>
/// What a firing that failed for good does to its definition's schedules: under PauseScheduling every active schedule
/// is paused with the reason, on the completion that spent the budget and on the reclaim that did, and admits nothing
/// until an operator resumes it; under Continue, the default, the schedules stay scheduled and the next occurrence fires.
/// </summary>
[ConformanceSpec(
    "schedule.firing-failure-policy",
    "A firing that fails for good pauses the schedules under PauseScheduling, or not",
    Area = "Scheduling",
    Contract = "A spent firing budget pauses a PauseScheduling definition's active schedules with the reason, on completion or reclaim, and leaves a Continue one scheduled."
)]
public sealed class FiringFailurePolicySpec : ActaRuntimeTestBase<ConformanceFixture, FiringFailurePolicyManifest>
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

    [Fact(
        DisplayName = "Under PauseScheduling the failed firing pauses the schedule with the reason, and a resume puts it back in front of the admitter"
    )]
    public async Task A_failed_firing_pauses_the_schedules_under_pause_scheduling()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, FiringFailurePolicyManifest.PausingJobName, ct);
        var occurrence = await CursorAsync(slotId, ct);

        await AdmitAtAsync(slotId, occurrence, ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(slotId, ct));

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        Assert.Null(schedule.PausedUntilUtc);
        Assert.Equal("Paused by the firing failure policy: the last firing failed for good.", schedule.PauseMessage);
        Assert.Null(schedule.NextEvaluationAtUtc);
        var paused = await ReadSingleEventAsync(slotId, EventCode.SchedulePaused, ct);
        Assert.Equal("firing-failed", EventDetail(paused, "reason"));
        Assert.Equal(FiringFailurePolicyManifest.ScheduleName, EventDetail(paused, "schedule"));

        // Paused, the schedule admits nothing at its next occurrence; resumed, it is the admitter's again.
        Clock.AdvanceTo(occurrence.AddMinutes(5));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);

        var lookup = new ScheduleLookup(
            TestNamespace,
            FiringFailurePolicyManifest.PausingJobName,
            FiringFailurePolicyManifest.ScheduleName
        );
        Assert.Equal(ControlAction.Applied, (await Schedules.ResumeAsync(lookup, ct: ct)).Action);
        Assert.Null(Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).PauseMessage);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
    }

    [Fact(DisplayName = "Under Continue the failed firing leaves the schedule scheduled, and the next occurrence fires")]
    public async Task A_failed_firing_leaves_the_schedules_scheduled_under_continue()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, FiringFailurePolicyManifest.ContinuingJobName, ct);
        var occurrence = await CursorAsync(slotId, ct);

        await AdmitAtAsync(slotId, occurrence, ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(slotId, ct));

        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
        Assert.Equal(occurrence.AddMinutes(5), schedule.NextEvaluationAtUtc);
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.SchedulePaused, ct));

        await AdmitAtAsync(slotId, occurrence.AddMinutes(5), ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(2, (await ReadJobAsync(slotId, ct)).ExecutionNumber);
    }

    [Fact(DisplayName = "A lost attempt that spends the budget applies the policy on reclaim as a completion would")]
    public async Task A_reclaim_that_spends_the_budget_applies_the_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, FiringFailurePolicyManifest.PausingJobName, ct);

        // The firing's only attempt is in flight on a lease that lapsed.
        await AdmitAtAsync(slotId, await CursorAsync(slotId, ct), ct);
        Assert.Equal(
            1,
            await Db.From<JobStateEntity>()
                .Where(r => r.Id == slotId)
                .UpdateOnlyAsync(
                    () =>
                        new JobStateEntity
                        {
                            Status = JobStatusCode.Executing,
                            LeasedByWorkerId = workerId,
                            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
                        },
                    ct
                )
        );

        await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct);

        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        Assert.Equal("Paused by the firing failure policy: the last firing failed for good.", schedule.PauseMessage);
        Assert.Equal(1, await CountEventsAsync(slotId, EventCode.SchedulePaused, ct));
    }

    private async Task<DateTime> CursorAsync(long slotId, CancellationToken ct)
    {
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.NotNull(schedule.NextOccurrenceAtUtc);
        return schedule.NextOccurrenceAtUtc!.Value;
    }

    private async Task AdmitAtAsync(long slotId, DateTime nowUtc, CancellationToken ct)
    {
        Clock.AdvanceTo(nowUtc);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
    }
}
