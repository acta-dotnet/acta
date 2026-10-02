# Schedule operations

## Purpose

Recurring cadence, missed-time policy, an operator-triggered run, and historical backfill are four
different operations. Keeping them separate prevents duplicate work and makes the schedule cursor
understandable in SQL and the dashboard.

Acta represents a recurring job as one durable slot job plus one row per named schedule. It does not
create future job rows for every calendar occurrence. The schedule cursors move; the stable slot job
runs when the earliest active cursor is due.

## The four operations

| Operation | Changes the recurring cursor? | Creates historical work? | Use it when |
| --- | --- | --- | --- |
| Normal recurring fire | Yes, after the firing is handled | No | The schedule reaches its next due instant normally. |
| Trigger now | No | No | An operator wants one immediate run without changing cadence. |
| MisfireStrategy reconciliation | Yes | At most one coalesced catch-up run | A schedule returns after downtime or resumes after a pause. |
| Backfill | Not automatically | Yes, explicitly | The application must process named historical periods as separate business work. |

## Normal recurring cursor

Each `[JobSchedule]` has its own next-run cursor. If a job has several schedules, the owning slot's
`next_run_at_utc` is the minimum across active schedule cursors. One execution can report several
`JobContext.TriggeringScheduleNames` when due schedules coalesce at the same claim.

The recurring controller is reused. Historical executions are visible in the event timeline; Acta
does not pre-materialize a queue of future occurrences.

## Trigger now

`IActaOperations.Schedules.TriggerNowAsync(...)` pulls the owning slot due now. It leaves the selected
schedule's own cursor and cadence untouched.

Use it for:

- rerunning a report against current state;
- validating a repaired dependency immediately;
- asking a controller to scan for work now.

Do not use it to represent “run the 2026-06-01 occurrence.” A trigger-now execution has no historical
period identity unless the job input or application data supplies one.

The dashboard confirmation records an optional note. Triggering is rejected when the schedule or its
recurring job is paused, the recurring job is cancelled or failed, the schedule is missing/orphaned,
or its slot already has a firing in flight.

## Catch-up and misfire

MisfireStrategy policy answers one narrow question: what should the cursor do when its stored occurrence is
already in the past?

- `Skip` advances to the first occurrence strictly after now. This is the default.
- `CatchUpOnce` keeps one missed occurrence due, causing one coalesced catch-up execution, then
  resumes from the next occurrence after now.

`CatchUpOnce` does not create one job for every missed period. Ten missed hourly occurrences
still produce one catch-up execution. The handler can inspect its current data and the triggering
schedule names, but it does not receive ten synthetic occurrence rows.

MisfireStrategy reconciliation occurs when a worker starts and registers its schedules, and when a
paused schedule is resumed. At a worker start, an occurrence less than a minute overdue is not missed:
it is due, and a running worker is about to claim it, so the start leaves it due under either policy.
A start also leaves alone a slot whose occurrence is under way, running or parked on a wait, sleep,
retry, or bounce: it keeps its status, wake instant, and due cursors. It reconciles under the
operator's override when one is in force. A resume gets no such grace: under `Skip`, an occurrence
that came due while the schedule was paused is skipped, however recent.

## Backfill

Acta currently has no dedicated schedule-backfill API. Backfill is application work and should be
materialized explicitly with a period in the input, an deduplication key, and usually a correlation id
or tags for operator search.

```csharp
public sealed record ReconcileDay(DateOnly Day);

foreach (var day in requestedDays)
{
    await jobs.EnqueueAsync(
        new ReconcileDay(day),
        o => o
            .DeduplicationKey(
                DeduplicationKey.ForDefinition("reconcile-day", day.ToString("O")))
            .CorrelationKey($"backfill:{requestId}"),
        ct);
}
```

This makes each historical period visible, retryable, and deduplicated. The recurring controller row
keeps its normal cadence while backfill jobs carry the historical identity.

Before a large backfill:

- estimate downstream load and database write volume;
- choose a distinct correlation key and tags;
- use deterministic deduplication keys per historical period;
- decide whether normal recurring work should remain active;
- verify retention is long enough for the incident/audit window;
- start with a small range and inspect the dashboard timeline and alerts.

## Pause, resume, preview, and overrides

- Pause excludes a schedule from the slot's minimum cursor. An indefinite pause needs an operator
  resume; a timed pause wakes and reconciles by misfire policy.
- Pausing the recurring job itself (`IJobs.PauseAsync`) stops all of its schedules at once and holds
  until an operator resumes, restarts, or cancels the job. A worker restart, a deploy, and any
  schedule change leave it paused, and trigger-now is rejected. Each worker start logs a warning
  naming the paused job.
- A recurring job whose schedules are all paused, exhausted, or removed by a deploy is Paused with no
  next run. It returns to Ready once a schedule offers a run; a job resume is refused until one does.
- Cancelling a recurring job, by an operator or by its handler, ends it. A worker restart, a
  deploy, and retention leave it Cancelled; only a job restart brings it back. A manual purge
  deletes it, and the next start registers the declaration as a new job.
- A recurring job's slot uses the job's name as its deduplication key. A worker refuses to start
  while an ordinary job of the namespace holds that key, and names the recurring job in the error,
  rather than turning that job into the slot.
- Resume does not blindly run every missed occurrence. It applies `Skip` or `CatchUpOnce`. A job
  resume or restart arms the job by that policy but leaves each schedule's cursor where it was, so
  the first run after it can list a `Skip` schedule whose occurrence the hold missed in
  `TriggeringScheduleNames`.
- Preview computes upcoming instants from the effective expression and time zone without reading or
  advancing the persisted cursor. It is safe on a paused schedule.
- Expression/time-zone overrides are operator state protected by an expected-version check. A stale
  dashboard edit must be reloaded, reviewed, and submitted again. A deploy that switches a schedule
  between interval and cron drops its expression override, which was written for the other kind.

Try the behavior in concepts
[`103-multiple-schedules`](../../concepts/100-scheduling/103-multiple-schedules/),
[`105-schedule-control`](../../concepts/100-scheduling/105-schedule-control/), and
[`106-schedule-misfire`](../../concepts/100-scheduling/106-schedule-misfire/).
