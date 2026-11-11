# Scheduling and calendars

How a `[Schedule]` becomes work, what the admission engine does with the occurrences it finds, where a calendar comes from, and in what order schedules and calendars deploy. The [developer guide](./developer-guide.md) covers writing the handler the schedule fires, and the [operator guide](./operator-guide.md) the deploy verbs, the control table, and the SQL views.

## The model

Acta represents a scheduled job as one row per named schedule, whose cursor sits in a narrow companion
row, plus the work a schedule delivers: one permanent job per definition by default, or a fresh root
per admitted occurrence under `PerOccurrence` delivery. It does not create future job rows for every
calendar occurrence. The schedule cursors move; the admission engine admits a firing onto the `Idle`
permanent job, or inserts the occurrence's root, when a cursor is due, and the claim runs it.

The admission engine admits a firing when a schedule is due: on a permanent job, due schedules coalesce into one firing, and `JobContext.TriggeringSchedules` names what fired, each with its occurrence; a per-occurrence schedule admits a root of its own. A firing retries under `MaxAttempts` like a one-shot, and a spent budget returns the permanent job to `Idle` for its next firing; what a firing that fails for good does to the schedules is the definition's `FiringFailurePolicy`, and what a job verb does to the permanent job is in [trigger, pause, resume, and preview](#trigger-pause-resume-and-preview).

Recurring cadence, missed-time policy, an operator-triggered run, and historical backfill are four
different operations. Keeping them separate prevents duplicate work and makes the schedule cursor
understandable in SQL and the dashboard.

| Operation | Changes the recurring cursor? | Creates historical work? | Use it when |
| --- | --- | --- | --- |
| Normal recurring fire | Yes, when the firing is admitted | No | The schedule reaches its next due instant normally. |
| Trigger now | No | No | An operator wants one immediate run without changing cadence. |
| MisfireStrategy reconciliation | Yes | One coalesced catch-up firing (`CatchUpOnce`, `CatchUpLatest`), or every missed occurrence in order (`CatchUpAll`) | The admitter finds a stored occurrence already past: after downtime, a resume, or a timed pause's end. |
| Backfill | Not automatically | Yes, explicitly | The application must process named historical periods as separate business work. |

## Declaring a schedule

```csharp
[Job("reconcile-ledger")]
[Schedule("nightly", "0 2 * * *", TimeZoneId = "Europe/Ljubljana")]
[Schedule("every-5m", "5m", Environments = new[] { "production" })]
public Task Reconcile(JobContext ctx, CancellationToken ct) => ...;
```

- A schedule's name is kebab-case, stable for operators, and unique within the definition; several `[Schedule]` may decorate one handler, each with its own cursor.
- The expression is a cron with five fields, or six for seconds (`Cron.Hourly` and its siblings name the common ones), an ISO 8601 duration (`"PT5M"`), or a plain one (`"5m"`, `"1h"`). A duration's largest unit is the day: a month or a year has no fixed length, so write it as a cron. An interval steps on the grid of its anchor, the deploy that introduced it, and never drifts.
- `TimeZoneId` is an IANA or Windows id, UTC by default, and is ignored for intervals.
- `Environments` names the environments the schedule registers in, matched against `JobsOptions.EnvironmentName` by the deploy; a schedule with no list registers everywhere.
- `Description` is persisted on the schedule row, apart from the note an operator writes when pausing or resuming.
- The remaining members are each a section below: `MisfireStrategy`, `ExpireAfter` and `ExpireAction` ([catch-up, misfire, and expiration](#catch-up-misfire-and-expiration)), `MaxOutstanding` and `FullBehavior` ([delivery and capacity](#delivery-and-capacity)), `StartAtUtc` and `EndBeforeUtc` ([finite schedules](#finite-schedules)), and `Calendar` and `Availability` ([calendars](#calendars)).

## Normal recurring cursor

Each `[Schedule]` has its own cursor (`next_occurrence_at_utc`), the next undecided occurrence; every
earlier one was admitted, skipped, or coalesced, and the ledger's `schedule.admitted` and `schedule.skipped`
events carry each decision's range. The admission engine evaluates a schedule when it is due, admits one
firing per permanent job per pass, and moves the cursor at admission, not at completion. A firing is outstanding until its attempt settles: a schedule that comes due meanwhile
waits for capacity and is woken by the settlement. One firing can carry several
`JobContext.TriggeringSchedules` when due schedules coalesce; `JobContext.FiringId` names the firing.

Historical executions are visible in the event timeline (`schedule.admitted`, `schedule.skipped`);
Acta does not pre-materialize a queue of future occurrences.

## Delivery and capacity

`[Job(ScheduledDelivery = ...)]` says how a definition's schedules deliver its work, and it is immutable
once deployed: a changed mode fails the deploy, so a new mode takes a new job name.

- `Permanent` (the default): one permanent job per definition. Due schedules coalesce into one firing of
  it, the handler keeps its durable state across firings, and the permanent job's capacity is one outstanding
  firing.
- `PerOccurrence`: each admitted occurrence is a fresh ordinary root job with the definition's default
  input (serialized at the deploy and stored on the definition, so any host admits it), the schedule as
  its provenance (`jobs.schedule_id`), its own steps, children, retries, and retention, and a deduplication
  key of the schedule and the nominal instant, so an occurrence is never admitted twice. Three schedules
  due at one instant make three roots.

| Mode | What a firing runs | Best for |
| --- | --- | --- |
| `Permanent` (the default) | The one permanent job, `Idle` between firings; its steps, variables, and progress persist across firings, and the handler may reset them as its final action | Synchronization, reconciliation, incremental controllers, repeated maintenance |
| `PerOccurrence` | A fresh ordinary root per occurrence, with its own state, retries, result, and retention | Work where each period or event is distinct: a billing run, an import, a notification |

```csharp
[Job("sync-orders")]                                                              // Permanent: one job, fired again and again
[Schedule("every-5s", "PT5S")]
public Task Sync(JobContext ctx, CancellationToken ct) => ...;

[Job("import-files", ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence)]    // a fresh root per occurrence
[Schedule("hourly", "PT1H", MaxOutstanding = 1)]
public Task Import(ImportFiles input, JobContext ctx, CancellationToken ct) => ...;
```

Both modes run through the same admission and execution engine, and a definition without schedules is
an ordinary definition: no permanent job is created for it. Backfilling historical periods is application work
under either mode ([backfill](#backfill)). The
[continuous-synchronization lab](../concepts/100-scheduling/109-continuous-synchronization/) runs the
permanent mode through a real worker crash and prints the rows.

`[Schedule(MaxOutstanding = 1, FullBehavior = ScheduleFullBehaviorCode.Wait)]` bounds a per-occurrence
schedule's unsettled roots: queued, executing, retrying, paused, and suspended roots all count, children
and ordinary enqueues do not, and a manual trigger counts like an occurrence. Every terminalization
releases the charge once (a completion, a cancel, a reclaim that spends the budget, a retire): the count
is the roots whose firing is still pending, so only the settlement that ends a root's firing releases it.
A restart of a finished root takes the charge again and is Rejected when the schedule is full or gone, and
a root that lands `Failed` is a firing that failed for good under `FiringFailurePolicy`. At the limit,
`Wait` leaves the occurrence undecided and parks the schedule until a settlement wakes it, when the
misfire policy decides the backlog; `Drop` skips the occurrence with the audited reason `capacity`
(`schedule.skipped`) and advances. A `Permanent` definition's schedules allow exactly 1 (`ACTA0125`).

A per-occurrence schedule's decisions that make no root, a skip or an ended pause, are audited against the
definition; an admission or a manual trigger is audited against the root. The
[per-occurrence-delivery concept](../concepts/100-scheduling/110-per-occurrence-delivery/) runs one at a
limit of two, dropping what arrives while it is full. A `PerOccurrence` definition
declares no lane. Its deadline counts from each root's admission, not from its occurrence, so a root admitted
late under catch-up or `FullBehavior = Wait` starts its clock late.

## Trigger now

`IActaOperations.Schedules.TriggerAsync(...)` admits a manual firing, due now: on a `Permanent`
definition onto the permanent job, on a `PerOccurrence` definition as a fresh root charged to the schedule.
It leaves the selected schedule's own cursor and cadence untouched.

Use it for:

- rerunning a report against current state;
- validating a repaired dependency immediately;
- asking a controller to scan for work now.

Do not use it to represent "run the 2026-06-01 occurrence." A trigger-now execution has no historical
period identity unless the job input or application data supplies one.

The dashboard confirmation records an optional note. Triggering is rejected when the schedule is paused,
when its permanent job is not `Idle` (paused, cancelled, failed, or with a firing outstanding), or when a
per-occurrence schedule is at `MaxOutstanding`; a missing or orphaned schedule is not found.

## Catch-up, misfire, and expiration

`MisfireStrategy` decides what a schedule does with the occurrences it finds already past when it is evaluated:

| Strategy | The missed occurrences | What fires |
| --- | --- | --- |
| `Skip` (default) | Skipped (`schedule.skipped`), except the latest when it is within the grace | The latest, on time only |
| `CatchUpOnce` | Coalesced into one firing: the earliest is the nominal occurrence, the latest its `CoveredThroughUtc` | One, at once |
| `CatchUpLatest` | Coalesced into the latest; the earlier ones are audited as skipped | The latest, however late it is |
| `CatchUpAll` | Kept | Each in order, one per settlement, within the schedule's capacity |

Under `CatchUpAll` the schedule is due again the moment a firing is admitted: a permanent job parks until that firing settles and takes the next occurrence then, and a per-occurrence schedule admits one root per pass up to `MaxOutstanding`, then waits or drops by its `FullBehavior`. Ten missed hourly occurrences are one run under `CatchUpOnce` or `CatchUpLatest`, one run under `Skip` only if the tenth is still within the grace, and ten runs under `CatchUpAll`.

The admission engine applies the strategy whenever it evaluates a stored occurrence that is already past: after downtime, when a paused schedule resumes, and when a timed pause ends. An occurrence less than `misfire_grace_seconds` (60) overdue still counts as due. A deploy never decides an occurrence: it registers the declaration and leaves the decision to the running fleet, so a deploy with an unchanged schedule changes nothing, and a worker start writes no schedule at all. A changed expression, expression kind, time-zone id, interval anchor, availability calendar name, or bound retimes the schedule at the deploy: the old timing's undecided occurrences are discarded (`schedule.retimed`) and the cursor seeds from the new timing's first occurrence after now. The zone counts by its declared id, not by the rules a host's time-zone data gives it, so a deploy from a host with other time-zone data retimes nothing ([known limitations](./known-limitations.md#execution-model)). A timed pause decides as of its end: an occurrence inside the window is skipped under `Skip`, runs once under `CatchUpOnce`, runs as the latest under `CatchUpLatest`, and runs in order under `CatchUpAll`. The skip does not wait for capacity: a pause that ends behind a busy permanent job or a full schedule skips its window then, so nothing from inside it fires when the job frees.

`[Schedule(ExpireAfter = "1h", ExpireAction = ScheduleExpireActionCode.Discard)]` bounds how old an occurrence may be when it is evaluated. Occurrences older than the age are decided first and without work, audited as `schedule.skipped` with the reason `expired`, and the strategy then decides the rest. `Discard` moves on. `Pause` also pauses the schedule, with the pause message `Paused by the expiration policy: an occurrence expired unadmitted.` and a `schedule.paused` event with the reason `expired`, so an operator learns that the schedule fell behind. The first evaluation after an operator's resume (`evaluation_status` is `resumed` until then) and the end of a timed pause expire the stale part of the backlog without pausing again, so a resumed schedule moves on. Expiration does not wait for capacity: an evaluation expires every occurrence past the age before it asks whether the permanent job or the schedule can take one, so a schedule parked behind a busy permanent job or a full schedule discards its stale backlog at once, and one parked under `Pause` wakes when its waiting occurrence ages past the expiry, so it pauses then. Without an expiry, `CatchUpAll` runs the whole backlog.

What the admitter does, by what it finds:

| It finds | `Skip` | `CatchUpOnce` | `CatchUpLatest` | `CatchUpAll` |
| --- | --- | --- | --- | --- |
| A worker start after a deploy | Nothing is decided at start; the first pass decides as after downtime | same | same | same |
| Downtime: n occurrences passed | The latest fires if within the grace, else all are skipped | One firing covering all n | The latest fires, n-1 skipped | n firings, in order |
| An indefinite pause resumed | As downtime | As downtime | As downtime | As downtime: the backlog runs |
| A timed pause ended | The window's occurrences are skipped; one at its end fires | One firing for the window | The latest fires | In order |
| A busy permanent job or a full schedule | Parks as `waiting-for-capacity`; decides as above when the settlement wakes it, or under `Pause` when the waiting occurrence expires | same | same | Parks between firings |
| Occurrences older than `ExpireAfter` | Expired first, with the reason; `Pause` then pauses the schedule | same | same | same |

## Finite schedules

`StartAtUtc` and `EndBeforeUtc` bound a schedule to the occurrences in [start, end), each a UTC instant written with a `Z`; either may be left out, and a start must be earlier than an end (`ACTA0129`).

```csharp
[Job("year-end-push")]
[Schedule("hourly", Cron.Hourly, StartAtUtc = "2027-12-01T00:00:00Z", EndBeforeUtc = "2028-01-01T00:00:00Z")]
public Task Push(JobContext ctx, CancellationToken ct) => ...;
```

The bounds are on the occurrences' own instants, not on when they run: the first occurrence is the first the expression produces at or after the start, an interval with a start steps on the start's grid (`start + k * interval`), and the last is the last before the end. An occurrence before the end that is evaluated after it is decided by the misfire and expiry policies exactly as any late occurrence, so a `CatchUpAll` schedule finishes its backlog after the end and a `Skip` schedule skips it. Past the end the schedule reads `exhausted` with no next occurrence, as a calendar with no instant left does; no event marks it. Extending the end in a later build is a timing change: the schedule retimes and goes on. So is declaring a bound on a running schedule, which discards its undecided occurrences as any retime does. A trigger fires the schedule whatever its bounds. `schedules_view` shows the bounds as `start_at_utc` and `end_before_utc`, and the schedule read as `startAtUtc` and `endBeforeUtc`.

## Calendars

A schedule's occurrences come from a cron, an interval, or a calendar: something that answers "the first occurrence after this instant". A settlement calendar, a payroll calendar, or a list of release dates is such a calendar. An availability calendar is the other role: it says when a schedule may fire at all (office hours, the complement of a maintenance blackout), and it filters any source. A schedule names a calendar family by `Calendar`, by `Availability`, or by both, and the family lives in the calendar catalog: `IActaOperations.Calendars`, the `/calendars` routes of the HTTP API, the dashboard's calendar pages, and `calendars_view`.

```csharp
[Job("settle")]
[Schedule("month-end", Calendar = "settlement-days", TimeZoneId = "Europe/Ljubljana")]
public Task Settle(JobContext ctx, CancellationToken ct) => ...;

[Job("sync-crm")]
[Schedule("quarter-hourly", "*/15 * * * *", Availability = "office-hours", TimeZoneId = "Europe/Ljubljana")]
public Task Sync(JobContext ctx, CancellationToken ct) => ...;
```

A deploy step saves, previews, and publishes the families those schedules name, each from its file:

```csharp
var officeHours = new CalendarLookup("sys", "office-hours");
var draft = await calendars.SaveDraftAsync(officeHours, CalendarRoleCode.Availability, await File.ReadAllTextAsync("calendars/office-hours.json"));
var preview = await calendars.PreviewAsync(officeHours, draft.Version!.Value, timeZoneId: "Europe/Ljubljana");
await calendars.PublishAsync(officeHours, draft.Version.Value);
```

`calendars/office-hours.json`:

```json
{
  "kind": "weekly-availability",
  "timeZone": "schedule",
  "weekly": [{ "days": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"], "windows": [["09:00", "12:00"], ["13:00", "17:00"]] }],
  "dateOverrides": { "2027-12-24": [["09:00", "12:00"]], "2027-12-25": [] },
  "excludedRanges": [{ "from": "2027-08-01", "to": "2027-08-14" }]
}
```

The step runs again as it is: a file nobody changed saves nothing, and its version, already current, publishes nothing.

- A family is a name in a namespace, or in `sys`, the shared scope every namespace binds; a name belongs to one scope. A schedule's calendar name resolves to the namespace's family of that name, else to the shared one in `sys`.
- Each save of a changed definition is a new immutable version, and the one Published version is what the name resolves to, read afresh at every evaluation and every preview. The schedule row stores the name and nothing about the version it resolved to, and the timing fingerprint covers the name only, so a deploy never retimes a schedule over its calendar; a publication does, as below.
- A definition is JSON with a `kind`, and every problem in one is reported with its JSON path. The kinds:

| Kind | Role | Members |
| --- | --- | --- |
| `weekly-availability` | Availability | `weekly` rules of `days` and local `windows`, an overnight window ending on the next date; `dateOverrides` replacing one date's windows, or closing it with `[]`; `excludedRanges` subtracted last, each a pair of dates `from` and `to` or a pair of instants `fromUtc` and `toUtc` in UTC with a `Z` |
| `intersection` | Availability | `of`: inline availability definitions |
| `subtraction` | Availability | `from` and `minus`: inline availability definitions |
| `explicit-instants` | Occurrence | `instants`: UTC instants (`2027-01-15T16:00:00Z`) or local date-times (`2027-01-15T16:00`) |
| `daily-local-time` | Occurrence | `times` on `days` (every day when absent), minus `excludedDates` |
| `business-day` | Occurrence | the `nth` (1 when absent) `first` or `last` business day (`select`) of each `week`, `month`, `quarter`, or `year` (`of`), over `businessDays` (Monday to Friday when absent) minus `holidays`, at a local `time` |
| `at-opening` | Occurrence | `availability`: an inline availability definition, each of whose window starts is an occurrence |
| `union` | Either | `of`: inline definitions of the calendar's role |

- The `settlement-days` family above could be `{"kind":"business-day","select":"last","of":"month","time":"16:00"}`: the last weekday of each month at 16:00 in the schedule's zone.
- `timeZone`, on the kinds that read local times, is a zone id; `"schedule"`, or no `timeZone`, means the schedule's zone. A kind that combines other definitions takes none, since each operand names its own.
- A local time in a DST gap resolves to the instant time resumes; an ambiguous one to its earlier instant.
- A rule the kinds cannot express is a cron, an interval, or a list of `explicit-instants` that a job or a deploy step generates and publishes.
- Every window is `[start, end)`: the start is open time and the end is not, so windows that touch join into one and a firing exactly at an opening counts while one exactly at a close does not. That holds for a weekly window, an excluded range of instants, and the windows a preview answers, which are in UTC and open-ended when the end is null. An excluded range of dates covers both of its dates whole.
- An occurrence outside every window is omitted, never shifted: an interval tick in a closed window is dropped and the next tick inside an open one fires on the grid; a cron tick or a calendar instant likewise. An availability calendar opens no time the source does not produce.
- Exhaustion is null: a calendar with no instant left, or an availability calendar with no open time left, leaves the schedule `exhausted`. Everything else is an error, never exhaustion: a name with no published version, a version this build cannot read, or a calendar that throws, such as a weekly availability whose exclusions close it for good. The schedule then reads `evaluation-error`, a `schedule.evaluation-failed` event records the reason once, at every recording level, the worker logs it once, and the admitter retries after a minute.
- A preview of a schedule whose calendar has no published version throws `CalendarUnavailableException`, naming the calendar; over HTTP it is a 409 problem whose detail names it.

The workflow keeps a calendar's definition beside the code that names it:

1. Keep each definition's JSON in source control.
2. Save it as a draft: `SaveDraftAsync`, `POST /calendars/{jobNamespace}/{calendarName}/drafts`, or the dashboard.
3. Preview the draft beside the current version: `PreviewAsync`, `GET /calendars/{jobNamespace}/{calendarName}/preview`, or the dashboard.
4. Publish it, with `PublishAsync`, `POST /calendars/{jobNamespace}/{calendarName}/publish`, or the dashboard, before the deploy of the jobs that name it.

- Workers never publish. A deploy checks every calendar its schedules name before it writes anything, and one that finds a calendar not ready fails, writing nothing, and lists each schedule as `namespace/job/schedule` with its calendar and the reason: no published version, a version published under the other role, or a version this build cannot read, with the compiler's problem.
- `GetAsync` answers a family's head with one version's `Definition`, the stored JSON text, which a draft takes back as it is.
- Every verb's expected version is optional, and leaving it out skips the check. A caller that edits what it read passes the version it read (`expectedLatestVersion`, `expectedCurrentVersion`), so a change made in between is a `VersionConflict` rather than an overwrite.
- `SaveDraftAsync` saves the definition as the family's latest version plus one and creates a namespace no worker has run in yet.
- A save of the definition the latest version already holds writes nothing and answers `AlreadyInState` with that version, whatever the expected version; the comparison is of the JSON's content, so layout and whitespace do not count.
- A save of the definition an Archived latest version holds saves a new draft, which revives the family.
- A definition that does not compile throws `CalendarDefinitionException`, whose message and `Problems` list every problem with its JSON path, and writes nothing; over HTTP the draft answers 409 `rejected` with the same `problems`.
- A role other than the family's, or a name the other scope holds, answers `Rejected` with the reason.
- `PreviewAsync` forecasts a version beside the current one and lists up to 100 bound schedules with an overdue flag, overdue ones first, with `SchedulesTotal` and `OverdueTotal` counting them all; `ListDependentsAsync` pages every one.
- `PublishAsync` makes a draft current and retimes every schedule that names the family in its scope, in one transaction: each cursor seeds from the new version's first occurrence after now, and `schedule.retimed` is recorded. Paused and orphaned schedules keep their state. A family that more than 200 schedules bind is refused.
- Publishing the version already current answers `AlreadyInState` and changes nothing: no schedule is retimed, no event is recorded, and an overdue occurrence does not refuse it. Two publications of one draft at once publish it once, and the second answers `AlreadyInState`.
- A schedule holding an undecided overdue occurrence refuses the publication (`OverdueDecisionCode.Refuse`, the default) until it is drained; under `OverdueDecisionCode.Discard` the occurrence is audited as `schedule.skipped` with the reason `retimed`.
- `ArchiveAsync` retires the current version of a family no live schedule names.

## Backfill

Acta currently has no dedicated schedule-backfill API. Backfill is application work and should be
materialized explicitly with a period in the input, a deduplication key, and usually a correlation id
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

## Trigger, pause, resume, and preview

Address a schedule by its path: the namespace, the scheduled job's name, and the schedule's name. The
permanent job carries no deduplication key; `GET /schedules` and the dashboard carry its ref. A schedule of a
`PerOccurrence` definition has no permanent job: trigger-now admits a fresh root charged to the schedule, and is
rejected while the schedule is at `MaxOutstanding`. An applied trigger answers the job it made ready as `jobRef`,
the permanent job or the fresh root. `GET /schedules/{jobNamespace}/{jobName}/{scheduleName}`
reads the schedule a path names, in any status, with the members its `GET /schedules` row carries, and
answers 404 when the path names none (`ISchedules.GetAsync` in code).

```csharp
var nightly = new ScheduleLookup("billing", "reconcile-ledger", "nightly");

await operations.Schedules.TriggerAsync(nightly, reasonMessage: "validate repaired upstream");
await operations.Schedules.PauseAsync(nightly, untilUtc: maintenanceEndsUtc, reasonMessage: "maintenance");
await operations.Schedules.ResumeAsync(nightly, reasonMessage: "maintenance complete");
```

- Pausing a schedule takes it out of admission without moving its cursor. An indefinite pause needs a resume; a timed pause ends by itself, and the admitter applies the misfire policy as of its end.
- Pausing the permanent job (`IJobs.PauseAsync`) stops all its schedules until an operator resumes, restarts, or cancels it; worker restarts and deploys leave it paused, and trigger-now is rejected.
- A permanent job whose schedules are all paused or removed sits `Idle` with nothing due until a schedule offers a run.
- Rescheduling a permanent job (`IJobs.RescheduleAsync`, the dashboard's Run now, or `POST /jobs/{jobRef}/reschedule` with no instant) runs it now: an `Idle` one, or a Paused one with no firing outstanding, takes a manual firing at the instant, the same firing a schedule trigger admits and rejected the same way in a suspended namespace, and returns to `Idle` when it settles; one mid-firing keeps its firing and moves its instant.
- Cancelling a permanent job ends it until a restart. A manual purge deletes it only when nothing pins it (every schedule orphaned, or the definition retired), and the next deploy registers a new permanent job.
- `[Job(FiringFailurePolicy = ...)]` decides what a firing that fails for good does to the definition's schedules: `Continue` (the default) leaves them scheduled, so the next occurrence fires; `PauseScheduling` pauses every active one with the pause message `Paused by the firing failure policy: the last firing failed for good.`, each resumable on its own. A completion that spends the budget and a reclaim that does apply it alike, as does a per-occurrence root that lands `Failed`; a handler's own fail or cancel stops a permanent job instead.
- A permanent job has no deduplication key: an ordinary job keyed by the job name is unrelated to it, so `IJobs.GetIdAsync` finds a permanent job by its ref or id, never by a key; `GET /schedules` and `ISchedules.ListAsync` carry the permanent job's ref. A per-occurrence root's key is `sys.occurrence.{schedule_id}.{ticks}`, and a manually triggered one's `sys.manual.{schedule_id}.{firingId}`.
- Preview computes upcoming instants without touching the cursor, and is safe on a paused schedule.
- A schedule's expression and time zone are code-owned: a deploy changes them, and an operator pauses, resumes, or triggers. The version moves with those operator actions and with deploys, not with each fire.

A schedule has no events route of its own. A permanent schedule's events are its permanent job's: read them at
`GET /jobs/{jobRef}/events` with the schedule's `jobRef`. A per-occurrence schedule records its admissions
and manual firings on the root each one enqueued, and its other events on its definition.
`GET /definitions/{jobNamespace}/{jobName}/events` lists every schedule event of the definition, of either
kind, and each event names its schedule in the `schedule` member of its details.

What each job verb does to a permanent job from each status is in the operator guide's [verb table](./operator-guide.md#what-each-verb-does-from-each-status).

## Deploying schedules and calendars

- `<app> jobs deploy` and `<app> jobs migrate` write the definitions and schedules this build declares and record its version; a worker start writes none of them ([deploying jobs](./operator-guide.md#deploying-jobs)). In development, `MigrateOnStartup = true` does the deploy at startup.
- A deploy with an unchanged schedule changes nothing. A changed expression, expression kind, time-zone id, interval anchor, availability calendar name, or bound retimes the schedule and discards its undecided occurrences; the zone's rules are not compared, only its id. No deploy decides an occurrence, the running fleet does ([catch-up, misfire, and expiration](#catch-up-misfire-and-expiration)).
- The calendars a deploy's schedules name are published before it ([calendars](#calendars)). A publication and a deploy that binds its family never interleave: whichever starts second waits, so the deploy reads the version the publication leaves, or the publication retimes the schedules the deploy wrote.
- A schedule a deploy drops is orphaned, and comes back without its pause when a build that declares it deploys, so to hold a permanent job across a rollback, pause the job, not the schedule.

## Reading schedules in SQL

`schedules_view` joins each schedule to its cursor, and `calendars_view` lists every calendar version with its role and status; the operator guide's [recurring schedules](./operator-guide.md#recurring-schedules) and [per-occurrence capacity drift](./operator-guide.md#per-occurrence-capacity-drift) recipes read them.

## Testing and concepts

A test fires a schedule without an admitter loop with `ForceScheduleDueAsync` and `AdmitOnceAsync` ([testing a scheduled job](./developer-guide.md#testing-a-scheduled-job)).

Concepts [`103-multiple-schedules`](../concepts/100-scheduling/103-multiple-schedules/), [`105-schedule-control`](../concepts/100-scheduling/105-schedule-control/), [`106-schedule-misfire`](../concepts/100-scheduling/106-schedule-misfire/), [`108-calendar-catalog`](../concepts/100-scheduling/108-calendar-catalog/), [`110-per-occurrence-delivery`](../concepts/100-scheduling/110-per-occurrence-delivery/), and [`111-firing-failure-policy`](../concepts/100-scheduling/111-firing-failure-policy/) show each behavior.
