using System.Data.Common;

namespace Acta.Runtime;

/// <summary>What the schedule control verbs share.</summary>
internal static class ScheduleControl
{
    /// <summary>An operator's note as the schedule row stores it, cut to that column.</summary>
    public static string? RowReason(string? reasonMessage) => reasonMessage.Truncate(ActaTextLimits.ScheduleReasonMessage);

    /// <summary>Binds what pause and resume take after their own arguments: the slot cursor, the operator, and the note.</summary>
    public static void BindTail(DbCommand cmd, ISqlDialect dialect, DateTime? jobNextRunAtUtc, string? actorKey, string? reasonMessage)
    {
        var actor = JobControlActor.Operator(actorKey);
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNextRunAtUtc, jobNextRunAtUtc));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, actor.ActorCode));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, actor.ActorKey));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobSchedule.ReasonMessage, RowReason(reasonMessage)));
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
