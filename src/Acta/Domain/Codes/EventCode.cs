using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What an event records, as a dotted code. There is no code for an enqueue, because the job row itself records it.</summary>
[JsonConverter(typeof(EventCodeJsonConverter))]
[ReservedCodeRange(224, 254, "Architecture-controlled reserve")]
[CodeKind("event")]
public enum EventCode : byte
{
    /// <summary>
    /// The persisted id is not one this build knows: the row was written by a newer Acta that added an
    /// event. Never written by Acta; only produced when reading forward.
    /// </summary>
    [Code("unspecified", "Event code not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("tenant.suspended", "An operator suspended a tenant; enqueues that name it are rejected. The message carries the reason.")]
    TenantSuspended = 10,

    [Code(
        "tenant.resumed",
        "An operator resumed a suspended tenant; enqueues that name it are accepted again. The message carries the reason."
    )]
    TenantResumed = 11,

    [Code("tenant.updated", "An operator changed a tenant's display name or description. The message carries the reason.")]
    TenantUpdated = 12,

    [Code("namespace.suspended", "An operator suspended a namespace; enqueues into it are rejected. The message carries the reason.")]
    NamespaceSuspended = 20,

    [Code(
        "namespace.resumed",
        "An operator resumed a suspended namespace; enqueues into it are accepted again. The message carries the reason."
    )]
    NamespaceResumed = 21,

    [Code("namespace.updated", "An operator changed a namespace's owner team or description. The message carries the reason.")]
    NamespaceUpdated = 22,

    [Code(
        "namespace.deployed",
        "A deploy wrote the namespace's job definitions and schedules. The message names the version and the one it replaced; the details list the jobs it added and changed."
    )]
    NamespaceDeployed = 23,

    /// <summary>
    /// The definition is the subject. Always emitted regardless of event recording: config governance is low-volume.
    /// </summary>
    [Code("definition.overrides-updated", "An operator changed a job definition's policy overrides; the message summarizes the change.")]
    JobDefinitionOverridesUpdated = 30,

    /// <summary>
    /// The definition is the subject; each job the retire cancelled carries its own job.cancelled event with the
    /// <c>definition-retired</c> reason. Always emitted regardless of event recording: config governance is low-volume.
    /// </summary>
    [Code("definition.retired", "An operator retired a job definition; enqueues are rejected and its waiting jobs were cancelled.")]
    JobDefinitionRetired = 31,

    [Code(
        "job.execution-succeeded",
        "The attempt's handler returned successfully; durationMs is set. Recorded for every success under audit, under failures only for a success after counted failures, and under an enabled alert profile also for a one-shot job's success past its first attempt."
    )]
    JobExecutionSucceeded = 40,

    [Code(
        "job.execution-failed",
        "The attempt failed and the job lives on: a retry within budget, or a permanent job's firing that spent its budget and returned the job to idle. durationMs and the failure's message are set; the details carry the keys reason and to."
    )]
    JobExecutionFailed = 41,

    [Code(
        "job.execution-orphaned",
        "The lease of the attempt's worker expired and crash recovery reclaimed the job without ending it. workerRef names the worker that held it; the details carry the keys reason and to."
    )]
    JobExecutionOrphaned = 42,

    [Code(
        "job.execution-yielded",
        "The attempt ended without succeeding or failing: rescheduled, suspended, paused, or cancelled by its handler or the engine. Recorded under audit only; the details carry the keys outcome and reason."
    )]
    JobExecutionYielded = 43,

    [Code(
        "job.failed",
        "The job failed for good: its retry budget is spent, its handler failed it, or its failure is not retryable. durationMs is set when an attempt ran; the details carry the key reason."
    )]
    JobFailed = 44,

    [Code(
        "job.suspended",
        "The attempt stopped on a durable wait: a sleep timer made the job ready again at its due time, or a wait on a signal or on child jobs left it suspended. It spends no retry budget; the details carry the key to."
    )]
    JobSuspended = 60,

    [Code(
        "job.rescheduled",
        "The job became ready with a new nextRunAtUtc: its handler rescheduled it, a failed attempt retries within budget, or an operator rescheduled it, who is then the event's actor. The details carry the reason when there is one."
    )]
    JobRescheduled = 61,

    [Code("job.cancelled", "The job was cancelled, which is final.")]
    JobCancelled = 70,

    [Code("job.paused", "The job was paused.")]
    JobPaused = 71,

    [Code(
        "job.resumed",
        "The job was resumed: it is ready again, idle for a permanent job with no firing outstanding, or blocked behind its lane. Crash recovery records the same event, with the reason lane-repaired in its details, when it releases the next job of a stranded lane."
    )]
    JobResumed = 72,

    [Code(
        "job.restarted",
        "The job was restarted: it is ready again, idle for a permanent job, or blocked behind its lane. Its failure count is reset and its retention cleared, and its execution number carries on."
    )]
    JobRestarted = 73,

    [Code("job.reprioritized", "An operator changed the job's priority; the message carries the operator's reason, if any.")]
    JobReprioritized = 74,

    [Code(
        "job.purged",
        "An operator deleted a finished job's record; the event is about the deleted job's id, its details keep the job's jobRef and correlationKey, and its actor is the operator. Recorded whatever the event recording level."
    )]
    JobPurged = 75,

    [Code(
        "job.input-amended",
        "An operator replaced a job's stored input; the operator is the event's actor, the details carry the format name and byte count of the previous input, and the message carries the reason."
    )]
    JobInputAmended = 76,

    [Code(
        "job.signal-raised",
        "A signal was raised for the job. Its signal checkpoint is set, unless the wait had already expired: then nothing changes, and the message says so."
    )]
    JobSignalRaised = 80,

    [Code(
        "job.state-reset",
        "The handler reset the job's state: its checkpoints and steps were cleared so the next execution starts as new. The record of a scheduled firing stays."
    )]
    JobStateReset = 81,

    [Code(
        "job.note-recorded",
        "A note the application's handler wrote. It is the only event an application can write, and the engine never writes it, so every other event is the engine's own. The message carries the line; the details carry the optional payload."
    )]
    JobNoteRecorded = 90,

    /// <summary>
    /// A schedule.* event's subject is the schedule's permanent job, since a schedule has no single numeric identity; a
    /// per-occurrence schedule has none, so its admissions and manual firings sit on the job each one enqueued and its
    /// other events on its definition. Every one carries the definition, and the schedule name rides details.schedule.
    /// </summary>
    [Code("schedule.paused", "A recurring schedule was paused; details carry the schedule name.")]
    SchedulePaused = 100,

    [Code("schedule.resumed", "A recurring schedule was resumed; details carry the schedule name.")]
    ScheduleResumed = 101,

    [Code("schedule.pause-expired", "A timed pause ended and the scheduler resumed the schedule. The details carry the schedule name.")]
    SchedulePauseExpired = 102,

    [Code(
        "schedule.triggered",
        "An operator fired the schedule's job by hand; the schedule's own timing did not move. The details carry the schedule name."
    )]
    ScheduleTriggered = 104,

    [Code(
        "schedule.admitted",
        "The scheduler chose one of the schedule's occurrences to fire; the details carry the schedule, the occurrence, the time through which the schedule is decided, and the firing id."
    )]
    ScheduleAdmitted = 105,

    [Code(
        "schedule.skipped",
        "A range of the schedule's occurrences was decided without work: the misfire policy skipped it, it expired, the schedule was full, its calendar no longer produces it, or a calendar publication discarded it; details carry the schedule, the range, and the reason."
    )]
    ScheduleSkipped = 106,

    [Code(
        "schedule.retimed",
        "A deploy changed the schedule's timing or declared again a schedule it had dropped, or a calendar it names was published; the undecided occurrences of the old timing were discarded and the schedule continues from the first occurrence of the new one. The details carry the schedule name."
    )]
    ScheduleRetimed = 107,

    [Code(
        "schedule.evaluation-failed",
        "The schedule's next occurrence could not be computed, so it admits nothing and is tried again every minute; the message is the error and the details carry the schedule. Recorded whatever the event recording level, when the schedule enters the error."
    )]
    ScheduleEvaluationFailed = 108,

    [Code(
        "schedule.orphaned",
        "A deploy dropped the schedule: its definition no longer declares it, so it fires nothing and keeps its history. The details carry the schedule name. Recorded whatever the event recording level."
    )]
    ScheduleOrphaned = 109,

    /// <summary>
    /// The calendar version row is the subject of every calendar.* event; details carry the calendar's name and the
    /// version. Always emitted regardless of event recording: catalog changes are low-volume operator activity.
    /// </summary>
    [Code(
        "calendar.draft-saved",
        "An operator saved a new draft version of a catalog calendar. The details carry the calendar, the version, and the role; the message carries the reason."
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
    [Code("worker.started", "A worker process registered and is active, or one marked dead heartbeated again.")]
    WorkerStarted = 120,

    [Code("worker.stopped", "A worker process shut down cleanly.")]
    WorkerStopped = 121,

    [Code("worker.died", "A worker's heartbeat went stale and crash recovery marked it dead.")]
    WorkerDied = 122,

    /// <summary>
    /// The alert is the subject of every alert.* event, and the job dimensions are its job's when it has one. Always
    /// emitted regardless of event recording: alert workflow is low-volume operator activity.
    /// </summary>
    [Code("alert.acknowledged", "An operator acknowledged an alert; the message carries the note.")]
    AlertAcknowledged = 140,

    [Code("alert.resolved", "An operator resolved an alert by hand; the message carries the note.")]
    AlertResolved = 141,

    /// <summary>
    /// Both outbox.* events are written against the namespace's sys.outbox permanent job by the tick
    /// that applies a parked operator command. Always emitted regardless of event recording: this is
    /// the evidence trail for actions whose subject rows live (or lived) in the producer's
    /// database, outside the ledger's reach.
    /// </summary>
    [Code("outbox.requeued", "An operator returned quarantined outbox rows to delivery; the message carries the reason and the row ids.")]
    OutboxRequeued = 180,

    [Code("outbox.discarded", "An operator deleted quarantined outbox rows; the message carries the reason and the deleted row ids.")]
    OutboxDiscarded = 181,
}
