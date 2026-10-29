using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Stable family-local event identifiers. Nearby values improve catalog readability but carry no runtime subject,
/// subgroup, or outcome semantics; match events by enum member or textual code. There is no <c>job.enqueued</c>
/// event on purpose: the Job row's own <c>(created_at_utc, namespace_id, definition_id)</c> records the enqueue fact,
/// and a separate event row would be write amplification on the narrow append-heavy events table; dedup outcomes
/// reach the caller via <c>EnqueueJobResponse.Action</c>, and cross-job rate analytics query <c>acta.jobs</c> directly.
/// </summary>
[JsonConverter(typeof(EventCodeJsonConverter))]
[ReservedCodeRange(224, 254, "Architecture-controlled reserve")]
[CodeKind("event")]
public enum EventCode : byte
{
    /// <summary>
    /// The persisted id is not one this build knows: the row was written by a newer Acta that added an
    /// event. Never written by Acta; only produced when reading forward.
    /// </summary>
    [Code("unspecified", "Event id not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("tenant.suspended", "An operator suspended a tenant; the name stops resolving at enqueue. EventMessage carries the reason.")]
    TenantSuspended = 10,

    [Code("tenant.resumed", "An operator resumed a suspended tenant; its key resolves at enqueue again. EventMessage carries the reason.")]
    TenantResumed = 11,

    [Code("tenant.updated", "An operator changed a tenant display name / description. EventMessage carries the reason.")]
    TenantUpdated = 12,

    [Code("namespace.suspended", "An operator suspended a namespace; enqueue into it is rejected. EventMessage carries the reason.")]
    NamespaceSuspended = 20,

    [Code(
        "namespace.resumed",
        "An operator resumed a suspended namespace; enqueue into it is allowed again. EventMessage carries the reason."
    )]
    NamespaceResumed = 21,

    [Code("namespace.updated", "An operator changed a namespace owner team / description. EventMessage carries the reason.")]
    NamespaceUpdated = 22,

    /// <summary>
    /// The definition is the subject. Always emitted regardless of event recording: config governance is low-volume.
    /// </summary>
    [Code("definition.overrides-updated", "An operator changed a job definition's policy overrides; EventMessage summarizes the change.")]
    JobDefinitionOverridesUpdated = 30,

    /// <summary>
    /// The definition is the subject; each job the retire cancelled carries its own job.cancelled event with the
    /// <c>definition-retired</c> reason. Always emitted regardless of event recording: config governance is low-volume.
    /// </summary>
    [Code("definition.retired", "An operator retired a job definition; enqueue is rejected and its parked jobs were cancelled.")]
    JobDefinitionRetired = 31,

    [Code(
        "job.execution-succeeded",
        "The attempt's handler returned successfully; DurationMs is set. Audit records every one, Failures a success after counted failures, and an enabled alert profile also a one-shot's success past its first attempt."
    )]
    JobExecutionSucceeded = 40,

    [Code(
        "job.execution-failed",
        "The attempt failed and the job runs again: a retry within budget or a recurring rollover. DurationMs and the failure's EventMessage are set; details carry reason and to."
    )]
    JobExecutionFailed = 41,

    [Code(
        "job.execution-orphaned",
        "The attempt's worker lease expired and crash recovery reclaimed the job without ending it. WorkerId names the stale holder; details carry reason and to."
    )]
    JobExecutionOrphaned = 42,

    [Code(
        "job.execution-yielded",
        "The attempt ended without succeeding or failing: rescheduled, suspended, paused, or cancelled by its handler or the engine. Audit only; details carry outcome and reason."
    )]
    JobExecutionYielded = 43,

    [Code(
        "job.failed",
        "The job failed for good: its retry budget is spent, its handler failed it, or its failure is not retryable. DurationMs is set when an attempt ran; details carry reason."
    )]
    JobFailed = 44,

    [Code(
        "job.suspended",
        "The attempt parked on a durable wait: a ctx.SleepAsync timer re-armed the job Ready at its due instant, or a signal or child wait left it Suspended. Budget-neutral; details carry to."
    )]
    JobSuspended = 60,

    [Code(
        "job.rescheduled",
        "The job re-armed Ready with a new NextRunAtUtc: a handler reschedule, a failed attempt's retry within budget, or an operator RescheduleAsync; details carry actor or reason."
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

    [Code("job.reprioritized", "Operator changed the job's claim priority; EventMessage carries the operator's reason, if any.")]
    JobReprioritized = 74,

    [Code(
        "job.purged",
        "Operator hard-deleted a terminal job; the subject is the deleted job's id. Details snapshot its jobRef, correlationKey, and actor. Always emitted regardless of event recording."
    )]
    JobPurged = 75,

    [Code(
        "job.input-amended",
        "Operator amended a job's stored input payload; details carry the actor and bounded metadata (format name and byte count) about the previous payload, and EventMessage carries the why."
    )]
    JobInputAmended = 76,

    [Code("job.signal-raised", "Signal delivered via IJobs.RaiseSignalAsync; matching signal checkpoint (State = Set) UPSERTed.")]
    JobSignalRaised = 80,

    [Code(
        "job.state-reset",
        "Handler called ctx.ResetStateAsync; the job's checkpoints and steps were cleared so the next execution starts as new. The scheduler's firing envelope stays."
    )]
    JobStateReset = 81,

    [Code(
        "job.note-recorded",
        "Application-authored note from ctx.NoteAsync. The only event code an application can write and one the runtime never emits, so every other event stays provably system-written. EventMessage carries the line; details carry the optional payload."
    )]
    JobNoteRecorded = 90,

    /// <summary>
    /// Every schedule.* event has the slot job as its subject (a schedule has no single numeric identity); the
    /// schedule name rides details.schedule.
    /// </summary>
    [Code("schedule.paused", "A recurring schedule was paused; details carry the schedule name.")]
    SchedulePaused = 100,

    [Code("schedule.resumed", "A recurring schedule was resumed; details carry the schedule name.")]
    ScheduleResumed = 101,

    [Code("schedule.pause-expired", "A timed pause elapsed; the scheduler auto-resumed the schedule. Details carry the schedule name.")]
    SchedulePauseExpired = 102,

    [Code(
        "schedule.triggered",
        "Operator admitted a manual firing of the slot; no schedule cursor moved. Details carry the schedule name."
    )]
    ScheduleTriggered = 104,

    [Code(
        "schedule.admitted",
        "The admission engine selected one of the schedule's occurrences for a firing; details carry the schedule, the occurrence, the covered-through boundary, and the firing id."
    )]
    ScheduleAdmitted = 105,

    [Code(
        "schedule.skipped",
        "The misfire policy discarded a range of the schedule's occurrences without work; details carry the schedule and the range."
    )]
    ScheduleSkipped = 106,

    [Code(
        "schedule.retimed",
        "A deploy changed the schedule's timing; the undecided occurrences of the old timing were discarded and the cursor moved to the first occurrence of the new one. Details carry the schedule name."
    )]
    ScheduleRetimed = 107,

    /// <summary>
    /// The calendar version row is the subject of every calendar.* event; details carry the calendar's name and the
    /// version. Always emitted regardless of event recording: catalog changes are low-volume operator activity.
    /// </summary>
    [Code(
        "calendar.draft-saved",
        "An operator saved a new draft version of a catalog calendar. Details carry the calendar, the version, and the role; EventMessage carries the reason."
    )]
    CalendarDraftSaved = 110,

    [Code(
        "calendar.published",
        "An operator published a catalog calendar version, which became the family's current one. Details carry the calendar, the version, the version it replaced, the overdue decision, and the schedules it affected."
    )]
    CalendarPublished = 111,

    [Code(
        "calendar.archived",
        "An operator archived a catalog calendar's current version; the family has no current version until another is published. Details carry the calendar and the version."
    )]
    CalendarArchived = 112,

    /// <summary>The worker is the subject of every worker.* event.</summary>
    [Code("worker.started", "Worker process registered; a workers row was appended (Status: Active).")]
    WorkerStarted = 120,

    [Code("worker.stopped", "Worker process shut down cleanly (Status: Active/Draining to Stopped).")]
    WorkerStopped = 121,

    [Code("worker.died", "Worker heartbeat went stale; crash recovery flipped the worker to Dead.")]
    WorkerDied = 122,

    /// <summary>
    /// The alert is the subject of every alert.* event, and the job dimensions are its job's when it has one. Always
    /// emitted regardless of event recording: alert workflow is low-volume operator activity.
    /// </summary>
    [Code("alert.acknowledged", "Operator acknowledged an alert; EventMessage carries the note.")]
    AlertAcknowledged = 140,

    [Code("alert.resolved", "Operator manually resolved an alert; EventMessage carries the note.")]
    AlertResolved = 141,

    /// <summary>
    /// Both outbox.* events are written against the namespace's sys.outbox slot job by the tick
    /// that applies a parked operator command. Always emitted regardless of event recording: this is
    /// the evidence trail for actions whose subject rows live (or lived) in the producer's
    /// database, outside the ledger's reach.
    /// </summary>
    [Code(
        "outbox.requeued",
        "An operator command returned quarantined outbox rows to Pending; EventMessage carries the justification and the row ids."
    )]
    OutboxRequeued = 180,

    [Code(
        "outbox.discarded",
        "An operator command deleted quarantined outbox rows; EventMessage carries the justification and the deleted row ids."
    )]
    OutboxDiscarded = 181,
}
