namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="ISchedules"/> staged by schedule name: "missing" reads as absent and "rejected" as a transition the
/// schedule's state forbids; every other name applies.
/// </summary>
internal sealed class FakeSchedules : ISchedules
{
    public FakeSchedules()
    {
        Pause = static (schedule, untilUtc, _, _, _) =>
            ValueTask.FromResult(
                Staged(schedule, static (action, status) => new PauseScheduleResponse(action, status, null, null, null))
                    ?? new PauseScheduleResponse(ControlAction.Applied, null, untilUtc, null, null)
            );
        Resume = static (schedule, _, _, _) =>
            ValueTask.FromResult(
                Staged(schedule, static (action, status) => new ResumeScheduleResponse(action, status, null, null, null))
                    ?? new ResumeScheduleResponse(ControlAction.Applied, null, null, null, null)
            );
        TriggerNow = (schedule, reason, actor, _) => ValueTask.FromResult(Trigger(schedule, reason, actor));
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

    public Func<ScheduleLookup, DateTime?, string?, string?, CancellationToken, ValueTask<PauseScheduleResponse>> Pause { get; set; }

    public Func<ScheduleLookup, string?, string?, CancellationToken, ValueTask<ResumeScheduleResponse>> Resume { get; set; }

    public Func<ScheduleLookup, string?, string?, CancellationToken, ValueTask<TriggerScheduleNowResponse>> TriggerNow { get; set; }

    public Func<ListSchedulesQuery, CancellationToken, ValueTask<ListSchedulesResponse>> List { get; set; }

    public Func<ScheduleLookup, int, CancellationToken, ValueTask<PreviewScheduleResponse?>> Preview { get; set; }

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

    ValueTask<TriggerScheduleNowResponse> ISchedules.TriggerNowAsync(
        ScheduleLookup schedule,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => TriggerNow(schedule, reasonMessage, actor, ct);

    ValueTask<ListSchedulesResponse> ISchedules.ListAsync(ListSchedulesQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<PreviewScheduleResponse?> ISchedules.PreviewAsync(ScheduleLookup schedule, int count, CancellationToken ct) =>
        Preview(schedule, count, ct);

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

    private TriggerScheduleNowResponse Trigger(ScheduleLookup schedule, string? reasonMessage, string? actor)
    {
        if (Staged(schedule, static (action, status) => new TriggerScheduleNowResponse(action, status, null, null, null)) is { } staged)
        {
            return staged;
        }

        TriggerCalls.Add((schedule.ScheduleName, reasonMessage, actor));
        return new TriggerScheduleNowResponse(ControlAction.Applied, ScheduleStatusCode.Active, null, DateTime.UnixEpoch, 1);
    }
}
