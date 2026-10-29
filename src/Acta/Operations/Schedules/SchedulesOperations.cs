namespace Acta;

/// <summary>The Schedules operations: <see cref="ISchedules"/> for applications, plus the engine's calls.</summary>
internal sealed class SchedulesOperations(
    PauseScheduleHandler pauseSchedule,
    ResumeScheduleHandler resumeSchedule,
    TriggerScheduleNowHandler triggerScheduleNow,
    ListSchedulesHandler listSchedules,
    PreviewScheduleHandler previewSchedule,
    RegisterSchedulesHandler registerSchedules,
    GetScheduleStateHandler getScheduleState,
    GetLiveSchedulesHandler getLiveSchedules,
    GetDueSchedulesHandler getDueSchedules,
    GetScheduleHorizonHandler getScheduleHorizon,
    AdmitFiringsHandler admitFirings
) : ISchedules
{
    public ValueTask<PauseScheduleResponse> PauseAsync(
        ScheduleLookup schedule,
        DateTime? untilUtc = null,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => pauseSchedule.HandleAsync(new PauseScheduleCommand(schedule, untilUtc, reasonMessage, actor), ct);

    public ValueTask<ResumeScheduleResponse> ResumeAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => resumeSchedule.HandleAsync(new ResumeScheduleCommand(schedule, reasonMessage, actor), ct);

    public ValueTask<TriggerScheduleNowResponse> TriggerNowAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => triggerScheduleNow.HandleAsync(new TriggerScheduleNowCommand(schedule, reasonMessage, actor), ct);

    public ValueTask<ListSchedulesResponse> ListAsync(ListSchedulesQuery query, CancellationToken ct = default) =>
        listSchedules.HandleAsync(query, ct);

    public ValueTask<PreviewScheduleResponse?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default) =>
        previewSchedule.HandleAsync(new PreviewScheduleQuery(schedule, count), ct);

    /// <summary>
    /// Registers every definition's recurring slot job and its schedules in one set-based call, answering each slot.
    /// Every host re-registers every slot at startup, so this runs against slots another host may be executing: it
    /// re-asserts the declaration on an idle slot, overwriting an operator reprioritize, and leaves an Executing,
    /// held, or finished slot as it stands.
    /// </summary>
    internal Task<RegisterSchedulesResponse> RegisterSchedulesAsync(RegisterSchedulesCommand command, CancellationToken ct) =>
        registerSchedules.HandleAsync(command, ct);

    /// <summary>
    /// The namespace's persisted (non-orphaned) per-schedule cursors. Drives startup misfire-aware reconciliation and the
    /// slot-pause decision for definitions that dropped every schedule.
    /// </summary>
    internal Task<GetScheduleStateResponse> GetScheduleStateAsync(int namespaceId, CancellationToken ct) =>
        getScheduleState.HandleAsync(new GetScheduleStateQuery(namespaceId), ct);

    /// <summary>
    /// The live (non-orphaned) schedules of one recurring slot. Read once per recurring fire for the due set and
    /// the recompute.
    /// </summary>
    internal Task<GetLiveSchedulesResponse> GetLiveSchedulesAsync(long jobId, CancellationToken ct) =>
        getLiveSchedules.HandleAsync(new GetLiveSchedulesQuery(jobId), ct);

    /// <summary>Every member of each slot with a schedule due for evaluation; the admitter's read.</summary>
    internal Task<GetDueSchedulesResponse> GetDueSchedulesAsync(int namespaceId, DateTime nowUtc, int take, CancellationToken ct) =>
        getDueSchedules.HandleAsync(new GetDueSchedulesQuery(namespaceId, nowUtc, take), ct);

    /// <summary>The namespace's earliest next evaluation, or null; how long the admitter may sleep.</summary>
    internal async Task<DateTime?> GetScheduleHorizonAsync(int namespaceId, CancellationToken ct) =>
        (await getScheduleHorizon.HandleAsync(new GetScheduleHorizonQuery(namespaceId), ct)).NextEvaluationAtUtc;

    /// <summary>Applies an admission plan atomically; a stale group answers unapplied and the admitter replans it.</summary>
    internal Task<AdmitFiringsResponse> AdmitFiringsAsync(AdmitFiringsCommand command, CancellationToken ct) =>
        admitFirings.HandleAsync(command, ct);
}
