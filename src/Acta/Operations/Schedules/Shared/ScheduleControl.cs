using System.Data.Common;

namespace Acta;

/// <summary>What the schedule control verbs share.</summary>
internal static class ScheduleControl
{
    /// <summary>An operator's note as the schedule row stores it, cut to that column.</summary>
    public static string? RowReason(string? reasonMessage) => reasonMessage.Truncate(ActaTextLimits.ScheduleChangeMessage);

    /// <summary>A schedule event's details: the operator, and the schedule by name, since a schedule has no numeric id.</summary>
    public static byte[]? Details(string scheduleName, string? actor) =>
        EventDetails.Write(("actor", JobControlActor.Operator(actor)), ("schedule", scheduleName));

    /// <summary>
    /// Binds what pause and resume take after their own arguments: the slot cursor, the note for the schedule row, and
    /// the event's message and details.
    /// </summary>
    public static void BindTail(
        DbCommand cmd,
        ISqlDialect dialect,
        DateTime? jobNextRunAtUtc,
        string scheduleName,
        string? actor,
        string? reasonMessage
    )
    {
        var note = RowReason(reasonMessage);
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNextRunAtUtc, jobNextRunAtUtc));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ChangeMessage, note));
        cmd.AddEventFields(dialect, note, Details(scheduleName, actor));
    }

    /// <summary>
    /// When the recompute finds an upcoming slot, the slot job lands ready. It may carry a scheduled run
    /// time, but waking idle loops makes them re-read their horizon. Without an upcoming slot, the job stays
    /// paused and needs no wake.
    /// </summary>
    public static ValueTask WakeIfDueAsync(
        WorkerWakeupPublisher wakeup,
        DateTime? jobNextRun,
        ControlAction action,
        CancellationToken ct
    ) =>
        action == ControlAction.Applied && jobNextRun is not null
            ? wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct)
            : ValueTask.CompletedTask;
}
