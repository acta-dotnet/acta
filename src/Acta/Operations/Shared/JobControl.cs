using System.Data.Common;

namespace Acta;

/// <summary>What the job control handlers share: the operator's input, the actor binding, and the ready wake.</summary>
internal static class JobControl
{
    /// <summary>
    /// The public verbs are operator-only: the actor and the manual reason are stamped here, never taken from
    /// the caller, and the message is capped to its column so both providers store the same text.
    /// </summary>
    public static JobControlInput Operator(string? reasonMessage, string? actor, int? expectedVersion = null) =>
        new(
            JobControlActor.Operator(actor),
            EventReasonCode.JobControlManual,
            reasonMessage.Truncate(ActaTextLimits.ReasonMessage),
            expectedVersion
        );

    public static void BindActor(DbCommand cmd, ISqlDialect dialect, JobControlInput input, bool includeReasonMessage = true)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ActorCode, input.Actor.ActorCode));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.Actor, input.Actor.Actor));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ReasonCode, input.ReasonCode));
        if (includeReasonMessage)
        {
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ReasonMessage, input.ReasonMessage));
        }
    }

    /// <summary>The optional CAS guard, bound last to match every versioned routine's trailing parameter (PostgreSQL arguments are positional).</summary>
    public static void BindExpectedVersion(DbCommand cmd, ISqlDialect dialect, JobControlInput input) =>
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersionOptional, input.ExpectedVersion));

    /// <summary>
    /// Announces new work (a job left Ready, a promoted lane member, a released parent). The routines know no
    /// namespace name, so every worker namespace hears it.
    /// </summary>
    public static ValueTask WakeWorkersIfAsync(WorkerWakeupPublisher wakeup, bool newWork, CancellationToken ct) =>
        newWork ? wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct) : ValueTask.CompletedTask;

    public static ValueTask WakeIfReadyAsync(
        WorkerWakeupPublisher wakeup,
        ControlAction action,
        JobStatusCode? status,
        CancellationToken ct
    ) => WakeWorkersIfAsync(wakeup, action == ControlAction.Applied && status == JobStatusCode.Ready, ct);

    /// <summary>
    /// Resume and restart are recurring-aware: a job with <paramref name="live"/> schedules is a recurring slot and
    /// reruns at its misfire-aware slot MIN, a plain job runs now (null). Reject is true when every schedule of the
    /// slot has no upcoming run, so a removed slot is never resurrected; the verb then answers Rejected before its
    /// routine runs, with no version.
    /// </summary>
    public static async Task<(bool Reject, DateTime? NextRunAtUtc)> RecurringNextRunAsync(
        IReadOnlyList<LiveSchedule> live,
        IActaClock clock,
        CancellationToken ct
    )
    {
        if (live.Count == 0)
        {
            return (false, null);
        }

        var slotMin = ScheduleWalker.RecomputeSlotMin(live, await clock.GetUtcNowAsync(ct));
        return slotMin is null ? (true, null) : (false, slotMin);
    }
}
