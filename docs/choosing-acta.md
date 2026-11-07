# Choosing Acta

Use this page to decide whether Acta should run your application's background jobs and workflows.

If the tool you run today already makes background work dependable and easy to see into, keep it. Acta is for work that has become application state: its steps, waits, retries, and history must survive a crash and stay in the SQL database the application already uses.

A scheduler decides **when work should start**. Acta also records **what happened after it started**, so these questions have an answer in the database:

- What ran?
- What failed?
- What runs next?
- Why is this job stuck?
- Can I pause, resume, restart, signal, or debug it?

> This page is a fit guide, not a claim that every existing scheduler should be replaced.

For concrete migration patterns from Hangfire, Quartz, TickerQ, platform schedulers, and custom task tables, see [Moving from another scheduler](#moving-from-another-scheduler).

## Start from the kind of work

| Kind of work | Use |
| --- | --- |
| A local loop where a missed tick is harmless | `BackgroundService` or `PeriodicTimer`. Nothing needs to be stored, so nothing more is needed. |
| A script or container the platform starts on a calendar | cron, systemd timers, Windows Task Scheduler, a Kubernetes CronJob, or a cloud timer. The platform owns the clock and the process. |
| Messages between services | A message broker or a streaming platform, for pub/sub, fan-out, and streams you replay. Acta can run the work a message starts, with the message id as its deduplication key. |
| Background work the application owns | Acta, when the work needs retries, durable state, a history, or an operator who can pause, restart, or signal it. |
| A controller that keeps an external system current: durable progress, bounded batches, a worker that may die mid-batch | Acta, as one permanent job ([the lab](../concepts/100-scheduling/109-continuous-synchronization/)). |
| A long-running workflow in .NET | Acta: one C# handler with steps, child jobs, signals, and timers, and its state as rows in your database. |
| Workflows across several languages, or drawn in a designer | A workflow platform. Acta is .NET only and has no visual designer or BPMN. |

## Who reaches for Acta

- App developers whose retries stopped being enough: the job's state became application state.
- Platform teams who need evidence and intervention: what ran, what is stuck, pause, restart, signal.
- AI and agent developers whose steps are expensive, long-running, tool-using, human-gated, or unsafe to restart from the top: a recorded step's output is not paid for twice, and an approval gate waits for days without holding a worker.

## Use Acta when

- The work belongs to the application, not only to the host or platform.
- The work must survive process restarts, deployments, or worker crashes.
- A missed, failed, duplicated, or stuck run needs to be visible.
- Operators need to pause, resume, cancel, restart, signal, explain, or debug jobs.
- Retries, delayed execution, recurring schedules, durable waits, or child jobs should share one model.
- Work for one customer, order, or device must run one job at a time, in order. Lanes give you the
  semantics of SQS FIFO message groups on your own database.
- SQL Server or PostgreSQL is already part of the system and can own the durable state, or the
  deployment is one process and SQLite can.
- You want background work to be inspectable as rows, events, schedules, leases, and checkpoints.

## Do not use Acta when

- Losing a tick is harmless and no one will ask what happened.
- The work is host maintenance better owned by the OS, container platform, or cloud scheduler.
- What you need is messaging between services: a message bus, an event stream, or a transport abstraction. Acta runs the work a message starts and does not carry the message.
- You want a long-running function's local variables to survive a crash without naming what to
  persist. Acta re-runs the handler from the top and skips completed steps, so state that outlives an
  attempt is an explicit step result or durable variable.
- You need BPMN, a visual process designer, or hosted workflow SaaS.
- A duplicate external side effect would be unrecoverable, and you cannot make it idempotent,
  guarded, or reconciled with durable step semantics.
- Workers in more than one language share the orchestration. Acta is .NET only.

## What Acta adds over a scheduler

| Pain | Acta's answer |
| --- | --- |
| Every team uses a different timer, hosted service, or scheduler | One durable job model: `[Job]`, `EnqueueAsync`, `ScheduleAsync`, and `[Schedule]` |
| Scheduled jobs vanish or double-run after restarts | Jobs, schedules, cursors, leases, attempts, and events are persisted in SQL |
| Nobody knows what happened last night | Job status, event timelines, alerts, dashboard/API, CLI, and SQL inspection |
| A worker died mid-job | Worker leases lapse and recovery reclaims eligible jobs for retry |
| A job must not repeat blindly | Deduplication keys, durable steps, locks, concurrency keys, and at-most-once step policy |
| Operators need control | Pause, resume, cancel, restart, signal, debug, explain, and schedule pause/resume |
| Teams do not want more infrastructure | The worker is the app process; durable state is the app database |

## If you already run one of these

If the tool already makes your background work boring, visible, and consistent, keep it.

Cron, retries, dashboards, and SQL storage exist everywhere. What Acta offers is one durable work model: the timer, the queue, the retry loop, and the hand-maintained state table become one job with a schedule, a lease, a budget, and a ledger, and a parent, its children, and a long wait are jobs in the same model.

| Existing tool | Keep it when | Consider Acta when |
| --- | --- | --- |
| Hangfire, a background job runner | It already runs your jobs well: a widely used runner with recurring jobs, automatic retries, and a dashboard, on storage you know. | A job's state should be data in your own SQL database that you can query, with typed inputs, durable steps, signals, child jobs, and order and limits held across every worker. |
| Quartz.NET, a scheduler | The calendar is the hard part: rich cron and calendar rules, misfire handling, and clustering you have already tuned. | What happens after a job starts matters more than when it starts: steps, long waits, crash recovery, lineage, and operator controls. |
| TickerQ, a scheduler | You want a source-generated scheduler with persistence, a dashboard, chaining, priorities, and concurrency controls. | The database should be the record of resumable work: checkpoints, signals, recovery evidence, and operator intervention. |
| Temporal, a workflow platform | Workers in several languages share one workflow, you want deterministic replay of its event history, or you already run its server or use its cloud service. | The workflow is .NET, its state belongs in the SQL database the application already uses, and you would rather not run a separate workflow server. |
| Elsa Workflows, a workflow library | You want a visual designer, with workflows stored as data that can change without a deploy. | Your team writes and reviews workflows as code, as plain C# handlers. |
| RabbitMQ, Kafka, Service Bus, or SQS, a message broker | You need messaging between services, those in other languages included, with whatever pub/sub, fan-out, or replay your broker or streaming platform offers. | Add Acta beside it when the work a message starts needs retries, steps, and a history: the broker carries the message, and Acta runs the work. Without a broker, Acta runs background work on the database alone. |

Teams usually move for one of these reasons:
- We have several competing ways to run background work.
- Nobody can explain what happened to last night's job.
- We need to inspect, restart, or debug old jobs.
- We need durable steps, child jobs, signals, long waits, or operator controls.
- We do not want a broker, sidecar, hosted scheduler, or separate control plane.
- We want the application database to be the source of truth.

## Moving from another scheduler

Migrate when the job is application state and you want the database to be the source of truth, not
because Acta also schedules jobs.

| Existing concept | Acta equivalent |
| --- | --- |
| Background job | `[Job]` + `IJobs.EnqueueAsync` |
| Delayed job | `IJobs.ScheduleAsync` with a `TimeSpan` or `DateTimeOffset` |
| Recurring job | `[Schedule]` on the `[Job]` method |
| Dashboard | `MapActa()` from `Acta.Dashboard`, under `/acta` |
| Retry policy | `[Job(MaxAttempts = ...)]` and backoff settings |
| Queue | Job namespace and worker registration |
| Job id | `JobRef` for public references, `JobId` internally |
| Continuations / batches | Child jobs and fan-out / fan-in APIs |
| Job parameters | The handler's parameters: one input record, or several parameters stored as one JSON record |
| Job history / logs | Durable `events`, `jobs_view`, dashboard/API, CLI, and `events_view` SQL |
| Application logs | Use normal app logging and correlate with `JobRef` / `JobId` when needed |

The event timeline is the durable job log: operators query `acta.events_view`, open the dashboard's
Events page, drill into a job's timeline, or use the CLI, without decoding a private scheduler store.

### Keep the platform clock

When the team already standardizes on a clock (Kubernetes CronJob, Azure Functions TimerTrigger, SQL
Server Agent, cron, systemd timers, Windows Task Scheduler), keep the trigger and let it enqueue an
Acta job. The platform owns when to poke the app; Acta owns idempotency, retries, execution history,
recovery, and operator controls.

For a typed job:

```csharp
public sealed record ReconcileInvoices(DateOnly BusinessDate);

await jobs.EnqueueAsync(
    new ReconcileInvoices(DateOnly.FromDateTime(DateTime.UtcNow)),
    o => o.DeduplicationKey(DeduplicationKey.PerDay("reconcile-invoices", "billing")),
    ct);
```

For a host that only knows namespace and job name:

```csharp
var request = JobRequestBuilder
    .Create("billing", "reconcile-invoices")
    .Json(new { BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow) }) // the same JSON the typed record writes
    .DeduplicationKey(DeduplicationKey.PerDay("reconcile-invoices", "billing"))
    .Build();

await jobs.EnqueueAsync(request, ct);
```

Do not write directly into Acta tables from the platform scheduler. Call application code, an
internal endpoint, a small console host, or another supported enqueue surface, so payload formats,
idempotency rules, validation, and catalog lookup stay in one place.

### Replace a custom job table

A homemade scheduler starts as `Tasks(Id, Name, LastRunTime, Interval, Status, Error)` and a processor
that scans due rows, flips a status, runs code, and records a timestamp, then grows retries, locks,
error history, and recovery. Replace both with a scheduled job:

```csharp
public sealed class ImportJobs
{
    [Job("import-files")]
    [Schedule("default", "PT10M")]
    public Task ImportFiles(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
```

| Custom table field | Acta replacement |
| --- | --- |
| `Name` | `[Job("import-files")]` |
| `Interval` | `[Schedule("default", "PT10M")]` or cron |
| `LastRunTime` | event timeline and schedule cursor |
| `Status` | `jobs_view.job_status`, schedule status, worker lease state |
| `Error` | failed execution event, its details reason, alert, result/details payload |
| processor loop | Acta worker runtime |
| manual lock row | worker leases, concurrency keys, durable locks |

Afterwards the question is no longer what the scheduler code did but what the durable rows say:
start with the [SQL recipes](./operator-guide.md#sql-recipes), then use the dashboard or CLI for
control actions.

### Checklist

- Pick one namespace per owning service, such as `billing`, `users`, or `imports`.
- Convert recurring work to `[Job]` + `[Schedule]`.
- Keep platform clocks only when the organization already standardizes on them.
- Add deduplication keys to platform-triggered jobs and user-triggered duplicate-prone jobs.
- Use `JobContext.JobRef` or `JobContext.JobId` in logger scopes for correlation.
- Wrap side effects in durable steps so a retry skips the ones that completed; a crash before a step records its outcome can still repeat it, so the side effect needs an idempotency key or `AtMostOnce()`.
- Teach operators to start with `jobs_view`, `events_view`, the dashboard, and `jobs explain`.

## Rule of thumb

Use `BackgroundService` or platform scheduling for disposable local loops.

Use Acta for durable application work that someone will later need to inspect, retry, pause, resume,
signal, debug, or explain.

For a runnable first pass, start with [your first job](./first-job.md). For the unusual implementation
ideas Acta can demonstrate live, read [Acta Engineering Labs](./engineering-labs.md). What evidence
stands behind the reliability claims, and what does not exist yet, is in
[Compatibility and support, evidence behind the claims](./compatibility-and-support.md#evidence-behind-the-claims).
