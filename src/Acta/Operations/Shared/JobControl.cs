using System.Data.Common;

namespace Acta;

/// <summary>What the job control handlers share: the operator's input, the actor binding, and the ready wake.</summary>
internal static class JobControl
{
    /// <summary>
    /// The public verbs are operator-only: the actor is stamped here, never taken from the caller, the operator
    /// code implies the reason, and the message is capped to its column so every provider stores the same text.
    /// </summary>
    public static JobControlInput Operator(string? reasonMessage, string? actor, int? expectedVersion = null) =>
        new(JobControlActor.Operator(actor), null, reasonMessage.Truncate(ActaTextLimits.EventMessage), expectedVersion);

    /// <summary>Binds the transition's event: its message as <c>event_message</c> and its actor and reason as details.</summary>
    public static void BindEvent(DbCommand cmd, ISqlDialect dialect, JobControlInput input) =>
        cmd.AddEventFields(dialect, input.Message, input.Details);

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
}
