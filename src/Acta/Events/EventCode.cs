using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Stable family-local event identifiers. Nearby values improve catalog readability but carry no
/// runtime subject, subgroup, or outcome semantics. Match events by enum member or textual code.
/// </summary>
[JsonConverter(typeof(EventCodeJsonConverter))]
[ReservedCodeRange(224, 254, "Architecture-controlled reserve")]
[CodeKind("event", Extensible = true)]
public enum EventCode : byte
{
    /// <summary>
    /// The persisted id is not one this build knows: the row was written by a newer Acta that added an
    /// event. Never written by Acta; only produced when reading forward.
    /// </summary>
    [Code("unspecified", "Event id not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    // ---------- Tenant / namespace admin ----------
    [Code("tenant.suspended", "An operator suspended a tenant; the key stops resolving at enqueue. ReasonMessage carries the reason.")]
    TenantSuspended = 10,

    [Code("tenant.resumed", "An operator resumed a suspended tenant; its key resolves at enqueue again. ReasonMessage carries the reason.")]
    TenantResumed = 11,

    [Code("tenant.updated", "An operator changed a tenant display name / description. ReasonMessage carries the reason.")]
    TenantUpdated = 12,

    [Code("namespace.suspended", "An operator suspended a namespace; enqueue into it is rejected. ReasonMessage carries the reason.")]
    NamespaceSuspended = 20,

    [Code(
        "namespace.resumed",
        "An operator resumed a suspended namespace; enqueue into it is allowed again. ReasonMessage carries the reason."
    )]
    NamespaceResumed = 21,

    [Code("namespace.updated", "An operator changed a namespace owner team / description. ReasonMessage carries the reason.")]
    NamespaceUpdated = 22,

    // ---------- Definition lifecycle ----------
    // job_id / job_ref are null; definition_id carries the identity. Emitted when an operator
    // edits a definition's policy overrides (who via actor_*, when via created_at_utc, what via
    // reason_message). Always emitted regardless of audit level: config governance is low-volume.

    [Code("definition.overrides-updated", "An operator changed a job definition's policy overrides; ReasonMessage summarizes the change.")]
    JobDefinitionOverridesUpdated = 30,

    // ---------- Job lifecycle ----------
    // No job.enqueued event: the Job row's own (created_at_utc, namespace_id, definition_id)
    // already records the enqueue fact; a separate event row would be pure write amplification on
    // the narrow append-heavy events table. Dedup outcomes surface to the caller via
    // JobEnqueueOutcome.Action; cross-job rate analytics query acta.jobs directly.

    [Code("job.execution-started", "Handler invocation began; paired with job.execution-finished on (JobId, ExecutionNumber).")]
    JobExecutionStarted = 40,

    [Code("job.execution-finished", "Per-attempt outcome finalized. DurationMs / ExecutionStatusCode / JobEventReasonCode populated.")]
    JobExecutionFinished = 41,

    [Code("job.recurring-rolled-over", "Recurring Job's NextRunAtUtc advanced to the next firing instant.")]
    JobRecurringRolledOver = 50,

    [Code("job.suspended", "Handler called ctx.SleepAsync; the Job re-armed to Ready with the sleep timer's due instant; budget-neutral.")]
    JobSuspended = 60,

    [Code(
        "job.rescheduled",
        "The job re-armed Ready with a new NextRunAtUtc: a handler reschedule or an operator RescheduleAsync; actor and reason distinguish."
    )]
    JobRescheduled = 61,

    [Code("job.cancelled", "Job was cancelled (Status to Cancelled, terminal).")]
    JobCancelled = 70,

    [Code("job.paused", "Job was paused (Status to Paused).")]
    JobPaused = 71,

    [Code("job.resumed", "Job was resumed (Status to Ready).")]
    JobResumed = 72,

    [Code("job.restarted", "Job was restarted (Status to Ready; failure_count reset, retention cleared, execution_number unchanged).")]
    JobRestarted = 73,

    [Code("job.reprioritized", "Operator changed the job's claim priority; ReasonMessage carries the operator's reason, if any.")]
    JobReprioritized = 74,

    [Code(
        "job.purged",
        "Operator hard-deleted a terminal job. job_id/job_ref are null (the row is gone); ReasonMessage carries the purged job's ref and name. Always emitted regardless of audit level."
    )]
    JobPurged = 75,

    [Code(
        "job.input-amended",
        "Operator amended a job's stored input payload; Detail carries bounded JSON metadata (format name and byte count) about the previous payload and ReasonMessage carries the why."
    )]
    JobInputAmended = 76,

    // ---------- Substrate ----------

    [Code("job.signal-raised", "Signal delivered via IJobs.RaiseSignalAsync; matching signal checkpoint (State = Set) UPSERTed.")]
    JobSignalRaised = 80,

    [Code(
        "job.state-reset",
        "Handler called ctx.ResetStateAsync; the Job's JobCheckpoint / JobStep / JobResult rows were cleared so the next execution starts as new."
    )]
    JobStateReset = 81,

    [Code(
        "job.note-recorded",
        "Application-authored note from ctx.NoteAsync. The only event code an application can write and one the runtime never emits, so every other event stays provably system-written. ReasonMessage carries the line; Detail carries the optional JSON payload."
    )]
    JobNoteRecorded = 90,

    // ---------- Schedule lifecycle ----------
    // Emitted against the slot job_id (JobEvent has no schedule_id); the schedule name rides reason_message.

    [Code("schedule.paused", "A recurring schedule was paused; ReasonMessage carries the schedule name.")]
    SchedulePaused = 100,

    [Code("schedule.resumed", "A recurring schedule was resumed; ReasonMessage carries the schedule name.")]
    ScheduleResumed = 101,

    [Code(
        "schedule.pause-expired",
        "A timed pause elapsed; the scheduler auto-resumed the schedule. ReasonMessage carries the schedule name."
    )]
    SchedulePauseExpired = 102,

    [Code(
        "schedule.overrides-updated",
        "Operator changed a schedule's expression/timezone overrides; ReasonMessage summarizes the change and carries the schedule name."
    )]
    ScheduleOverridesUpdated = 103,

    [Code(
        "schedule.triggered",
        "Operator fired a schedule manually; the slot's cursor was pulled to now. ReasonMessage carries the schedule name."
    )]
    ScheduleTriggered = 104,

    // ---------- Worker lifecycle ----------
    // job_id is null on every worker.* event; worker_id / namespace_id carry the identity.

    [Code("worker.started", "Worker process registered; a workers row was appended (Status: Active).")]
    WorkerStarted = 120,

    [Code("worker.stopped", "Worker process shut down cleanly (Status: Active/Draining to Stopped).")]
    WorkerStopped = 121,

    [Code("worker.died", "Worker heartbeat went stale; the sys.recovery system job flipped the worker to Dead.")]
    WorkerDied = 122,

    // ---------- Alert lifecycle ----------
    // job_id carries the alert's job when it has one; the alert id rides reason_message. Always
    // emitted regardless of audit level: alert workflow is low-volume operator activity.

    [Code("alert.acknowledged", "Operator acknowledged an alert; ReasonMessage carries the alert id and note.")]
    AlertAcknowledged = 140,

    [Code("alert.resolved", "Operator manually resolved an alert; ReasonMessage carries the alert id and note.")]
    AlertResolved = 141,

    // ---------- Settings ----------
    // No job columns; Detail carries {"name": ...} identifying the setting, since events has no
    // setting column. Always emitted: settings writes are low-volume operator/deployment activity.

    [Code("setting.updated", "A durable setting was written (created or overwritten); Detail carries the setting name.")]
    SettingUpdated = 160,
}
