namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="ISchedules"/> staged by schedule name: "missing" reads as absent and "rejected" as a transition the
/// schedule's state forbids; every other name reads as, and applies to, an active schedule at version 2 whose next
/// occurrence is <see cref="NextOccurrence"/>, leaving it at version 3.
/// </summary>
internal sealed class FakeSchedules : ISchedules
{
    private static readonly DateTime NextOccurrence = new(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The permanent slot every found schedule fires.</summary>
    public static readonly JobRef SlotJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000045"));

    public FakeSchedules()
    {
        Get = static (schedule, _) => ValueTask.FromResult(schedule.ScheduleName == "missing" ? null : Found(schedule));
        Pause = static (schedule, untilUtc, _, _, _) =>
            ValueTask.FromResult(
                Staged(schedule, static (action, status) => new PauseScheduleResponse(action, status, null, null, null))
                    ?? new PauseScheduleResponse(ControlAction.Applied, ScheduleStatusCode.Paused, untilUtc, NextOccurrence, 3)
            );
        Resume = static (schedule, _, _, _) =>
            ValueTask.FromResult(
                Staged(schedule, static (action, status) => new ResumeScheduleResponse(action, status, null, null, null))
                    ?? new ResumeScheduleResponse(ControlAction.Applied, ScheduleStatusCode.Active, null, NextOccurrence, 3)
            );
        Trigger = (schedule, reason, actor, _) => ValueTask.FromResult(Triggered(schedule, reason, actor));
        List = static (_, _) => ValueTask.FromResult(new ListSchedulesResponse([], null, false, 50, null));
        Preview = static (schedule, _, _) =>
            ValueTask.FromResult<PreviewScheduleResponse?>(
                schedule.ScheduleName == "missing"
                    ? null
                    : new PreviewScheduleResponse("0 9 * * *", "UTC", [new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc)])
            );
    }

    /// <summary>Applied triggers; a staged outcome records nothing.</summary>
    public List<(string ScheduleName, string? Note, string? Actor)> TriggerCalls { get; } = [];

    public Func<ScheduleLookup, CancellationToken, ValueTask<GetScheduleResponse?>> Get { get; set; }

    public Func<ScheduleLookup, DateTime?, string?, string?, CancellationToken, ValueTask<PauseScheduleResponse>> Pause { get; set; }

    public Func<ScheduleLookup, string?, string?, CancellationToken, ValueTask<ResumeScheduleResponse>> Resume { get; set; }

    public Func<ScheduleLookup, string?, string?, CancellationToken, ValueTask<TriggerScheduleResponse>> Trigger { get; set; }

    public Func<ListSchedulesQuery, CancellationToken, ValueTask<ListSchedulesResponse>> List { get; set; }

    public Func<ScheduleLookup, int, CancellationToken, ValueTask<PreviewScheduleResponse?>> Preview { get; set; }

    ValueTask<GetScheduleResponse?> ISchedules.GetAsync(ScheduleLookup schedule, CancellationToken ct) => Get(schedule, ct);

    ValueTask<PauseScheduleResponse> ISchedules.PauseAsync(
        ScheduleLookup schedule,
        DateTime? untilUtc,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Pause(schedule, untilUtc, reasonMessage, actor, ct);

    ValueTask<ResumeScheduleResponse> ISchedules.ResumeAsync(
        ScheduleLookup schedule,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Resume(schedule, reasonMessage, actor, ct);

    ValueTask<TriggerScheduleResponse> ISchedules.TriggerAsync(
        ScheduleLookup schedule,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Trigger(schedule, reasonMessage, actor, ct);

    ValueTask<ListSchedulesResponse> ISchedules.ListAsync(ListSchedulesQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<PreviewScheduleResponse?> ISchedules.PreviewAsync(ScheduleLookup schedule, int count, CancellationToken ct) =>
        Preview(schedule, count, ct);

    /// <summary>The schedule a found path names: a daily cron on its definition's permanent slot, job 45.</summary>
    private static GetScheduleResponse Found(ScheduleLookup schedule) =>
        new(
            ScheduleId: 3,
            JobId: 45,
            DefinitionId: 5,
            JobNamespace: schedule.JobNamespace,
            JobName: schedule.JobName,
            ScheduleName: schedule.ScheduleName,
            Origin: ScheduleOriginCode.Definition,
            ExpressionKind: ScheduleExpressionKindCode.Cron,
            Expression: "0 9 * * *",
            TimeZoneId: "UTC",
            MisfireStrategy: MisfireStrategyCode.Skip,
            NextOccurrenceAtUtc: NextOccurrence,
            NextEvaluationAtUtc: NextOccurrence,
            EvaluationStatus: ScheduleEvaluationStatusCode.Scheduled,
            Status: ScheduleStatusCode.Active,
            PausedUntilUtc: null,
            CreatedAtUtc: NextOccurrence.AddDays(-1),
            ModifiedAtUtc: NextOccurrence.AddDays(-1),
            Version: 2,
            JobRef: SlotJobRef,
            MaxOutstanding: 1,
            OutstandingCount: 0,
            FullBehavior: ScheduleFullBehaviorCode.Wait,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            ExpireAfterSeconds: null,
            ExpireAction: null,
            AvailabilityCalendarName: null,
            StartAtUtc: null,
            EndBeforeUtc: null
        );

    /// <summary>The two staged outcomes every schedule verb shares; null means the verb applies, so each verb keeps its
    /// own applied shape.</summary>
    private static T? Staged<T>(ScheduleLookup schedule, Func<ControlAction, ScheduleStatusCode?, T> response)
        where T : class =>
        schedule.ScheduleName switch
        {
            "missing" => response(ControlAction.NotFound, null),
            "rejected" => response(ControlAction.Rejected, ScheduleStatusCode.Paused),
            _ => null,
        };

    private TriggerScheduleResponse Triggered(ScheduleLookup schedule, string? reasonMessage, string? actor)
    {
        if (Staged(schedule, static (action, status) => new TriggerScheduleResponse(action, status, null, null, null, null)) is { } staged)
        {
            return staged;
        }

        TriggerCalls.Add((schedule.ScheduleName, reasonMessage, actor));
        return new TriggerScheduleResponse(ControlAction.Applied, ScheduleStatusCode.Active, null, NextOccurrence, 3, SlotJobRef);
    }
}
