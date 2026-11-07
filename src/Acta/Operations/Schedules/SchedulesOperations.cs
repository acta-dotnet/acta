using System.Data.Common;

namespace Acta;

/// <summary>The Schedules operations: <see cref="ISchedules"/> for applications, plus the engine's calls.</summary>
internal sealed class SchedulesOperations(
    GetScheduleHandler getSchedule,
    PauseScheduleHandler pauseSchedule,
    ResumeScheduleHandler resumeSchedule,
    TriggerScheduleHandler triggerSchedule,
    ListSchedulesHandler listSchedules,
    PreviewScheduleHandler previewSchedule,
    GetScheduleTargetHandler getScheduleTarget,
    RegisterSchedulesHandler registerSchedules,
    ListScheduleStatesHandler listScheduleStates,
    ListDueSchedulesHandler listDueSchedules,
    GetScheduleHorizonHandler getScheduleHorizon,
    AdmitScheduleFiringsHandler admitScheduleFirings
) : ISchedules
{
    public ValueTask<GetScheduleResponse?> GetAsync(ScheduleLookup schedule, CancellationToken ct = default) =>
        getSchedule.HandleAsync(new GetScheduleQuery(schedule), ct);

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

    public ValueTask<TriggerScheduleResponse> TriggerAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => triggerSchedule.HandleAsync(new TriggerScheduleCommand(schedule, reasonMessage, actor), ct);

    public ValueTask<ListSchedulesResponse> ListAsync(ListSchedulesQuery query, CancellationToken ct = default) =>
        listSchedules.HandleAsync(query, ct);

    public ValueTask<PreviewScheduleResponse?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default) =>
        previewSchedule.HandleAsync(new PreviewScheduleQuery(schedule, count), ct);

    /// <summary>The live schedule a path names, as the verbs resolve it; null when the path names nothing.</summary>
    internal ValueTask<GetScheduleTargetResponse?> GetScheduleTargetAsync(ScheduleLookup schedule, CancellationToken ct) =>
        getScheduleTarget.HandleAsync(new GetScheduleTargetQuery(schedule), ct);

    /// <summary>
    /// Registers every definition's recurring slot job and its schedules in one set-based call, answering each slot.
    /// Every deploy re-registers every slot, so this runs against slots a worker may be executing: it
    /// re-asserts the declaration on an idle slot, overwriting an operator reprioritize, and leaves an Executing,
    /// held, or finished slot as it stands.
    /// </summary>
    internal Task<RegisterSchedulesResponse> RegisterSchedulesAsync(RegisterSchedulesCommand command, CancellationToken ct) =>
        registerSchedules.HandleAsync(command, ct);

    /// <summary>
    /// The namespace's persisted (non-orphaned) per-schedule cursors. Drives startup misfire-aware reconciliation and the
    /// slot-pause decision for definitions that dropped every schedule.
    /// </summary>
    internal Task<ListScheduleStatesResponse> ListScheduleStatesAsync(int namespaceId, CancellationToken ct) =>
        listScheduleStates.HandleAsync(new ListScheduleStatesQuery(namespaceId), ct);

    /// <summary>The same read inside <paramref name="transaction"/>, when one is given, so it sees what that transaction wrote.</summary>
    internal Task<ListScheduleStatesResponse> ListScheduleStatesAsync(int namespaceId, DbTransaction? transaction, CancellationToken ct) =>
        listScheduleStates.HandleAsync(new ListScheduleStatesQuery(namespaceId, transaction), ct);

    /// <summary>Every member of each slot with a schedule due for evaluation; the admitter's read.</summary>
    internal Task<ListDueSchedulesResponse> ListDueSchedulesAsync(int namespaceId, DateTime nowUtc, int take, CancellationToken ct) =>
        listDueSchedules.HandleAsync(new ListDueSchedulesQuery(namespaceId, nowUtc, take), ct);

    /// <summary>The namespace's earliest next evaluation, or null, and the database's clock; how long the admitter may sleep.</summary>
    internal Task<GetScheduleHorizonResponse> GetScheduleHorizonAsync(int namespaceId, CancellationToken ct) =>
        getScheduleHorizon.HandleAsync(new GetScheduleHorizonQuery(namespaceId), ct);

    /// <summary>Applies an admission plan atomically; a stale group answers unapplied and the admitter replans it.</summary>
    internal Task<AdmitScheduleFiringsResponse> AdmitScheduleFiringsAsync(AdmitScheduleFiringsCommand command, CancellationToken ct) =>
        admitScheduleFirings.HandleAsync(command, ct);
}
