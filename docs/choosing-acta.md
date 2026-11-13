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
| A call that must answer now, to a service that may fail for a moment | An in-process resilience library such as Polly: retries, timeouts, and circuit breakers that live in memory for the length of the call. Nothing has to outlive the request. |
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
| Hangfire, a background job runner | It already runs your jobs well: a widely used runner with recurring jobs, automatic retries, continuations, and a dashboard, on storage you know, with batches and throttling in its paid packages. | Progress inside a job has to survive a restart: named steps, signals, long waits, and child jobs in one handler, with order and limits held across every worker and the job's state in SQL views you can query. |
| Quartz.NET, a scheduler | The calendar is the hard part: rich cron and calendar rules, misfire handling, and clustering you have already tuned. Its 4.x line adds a dashboard, continuations, and execution groups that cap work across a cluster. | The job itself has state to keep: steps whose results survive a restart, long waits, child jobs, lineage, and one answer to why a job is where it is. |
| TickerQ, a scheduler | You want a source-generated scheduler with persistence, a dashboard, priorities, and, in its 5.x line, durable chains of jobs with parallel branches. | One job should carry its own progress: checkpoints inside the handler, signals and timers it waits on, lanes and limits, recovery evidence, and operator intervention. |
| Workflow Core, a workflow library | Your workflows are graphs of steps, declared in fluent C#, JSON, or YAML, and it already persists them where you need. | You would rather write the workflow as an ordinary C# handler, with named steps, waits, and child jobs as its durable points, and run it under the same controls, limits, and SQL views as every other job. |
| Temporal, a workflow platform | Workers in several languages share one workflow, you want deterministic replay of its event history, or you already run its server or use its cloud service. | The workflow is .NET, its state belongs in the SQL database the application already uses, and you would rather not run a separate workflow server. |
| Elsa Workflows, a workflow library | You want workflows as data that can change without a deploy, built in code or drawn in its designer, hosted in your application or on their own. | Your team writes and reviews workflows as plain C# handlers, and wants its jobs, schedules, and workflows in one model. |
| Durable Task (Durable Functions and its SDKs), an orchestration framework | You host on Azure Functions, or want a managed Azure service to hold your orchestrations' state. Durable Functions can also keep it in SQL Server. | The state belongs in the application's own PostgreSQL, SQL Server, or SQLite database, wherever the application runs, and you want checkpoints you place yourself rather than orchestrator code that replays. |
| Wolverine, a messaging framework | Your background work is message handling: a durable inbox and outbox, durable local queues, and scheduled messages, on a database you already run. | The unit is a job with a life of its own: steps, waits, child jobs, a schedule, a history, and an operator who can pause, restart, or signal it. |
| RabbitMQ, Kafka, Service Bus, or SQS, a message broker | You need messaging between services, those in other languages included, with whatever pub/sub, fan-out, or replay your broker or streaming platform offers. | Add Acta beside it when the work a message starts needs retries, steps, and a history: the broker carries the message, and Acta runs the work. Without a broker, Acta runs background work on the database alone. |
| Polly, a resilience library | The caller is waiting: retries, timeouts, circuit breakers, hedging, fallbacks, and rate limiters around one call, held in memory in one process. | Add Acta around it when a retry has to outlive the process: the attempt count and the backoff are a row, a finished step is not repeated, a limit holds across every worker, and a job that ran out of attempts can be read and restarted weeks later. Polly inside a step makes the quick retries, and Acta makes the long ones. |

Each row describes the project as its own documentation did in November 2026: Quartz.NET at 4.x, and TickerQ at 5.x, then a preview.

## The same six questions, asked of each

The table above says when each tool is the right one. Here the same six questions are asked of Acta and of six of them, and each answer is the one the project's own documentation gives, with the page it comes from, as it read in November 2026. Where a project's documentation is silent, the answer says so.

### Waiting hours or days, for something outside the job

- **Acta**: The handler awaits `ctx.WaitSignalAsync<Approval>("approval")`, and `jobs.RaiseSignalAsync` releases it; `SleepAsync` and `SleepUntilAsync` are durable timers. The job holds no worker while it waits. ([guide](./developer-guide.md#steps-variables-signals-and-timers))
- **Hangfire**: A job is one method call. A later stage is another job, started by `BackgroundJob.ContinueJobWith` or delayed with `BackgroundJob.Schedule`; its documentation does not describe a wait for an outside event inside a running job. ([docs](https://docs.hangfire.io/en/latest/background-methods/calling-methods-with-delay.html))
- **Quartz.NET**: A continuation trigger (`StartAfter`) waits in the job store for its parent firing to end, and a trigger can be stored paused with a reason until someone resumes it. The wait is between jobs, not inside one. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/how-tos/job-continuations.html))
- **Workflow Core**: The workflow definition calls `.WaitFor("event", ...)`, released by `host.PublishEvent`; `.Schedule` and `.Recur` cover time. ([docs](https://workflow-core.readthedocs.io/en/latest/external-events/))
- **Elsa**: A blocking activity (`Delay`, `Timer`, `Event`, `UserTask`) or `context.CreateBookmark` leaves a bookmark, and the workflow resumes when the bookmark is triggered. ([docs](https://docs.elsaworkflows.io/guides/running-workflows/long-running-workflows))
- **Temporal**: A `[WorkflowSignal]` method sets state and the workflow awaits `Workflow.WaitConditionAsync`; `Workflow.DelayAsync` is the durable timer. ([docs](https://docs.temporal.io/develop/dotnet/message-passing))
- **Durable Task**: The orchestrator awaits `context.WaitForExternalEvent<T>("Approval")`, raised by a client, and `context.CreateTimer` is the durable timer. ([docs](https://learn.microsoft.com/azure/durable-task/common/durable-task-external-events))

### A finished step's output, where it lives, and what a restart does with it

- **Acta**: A row in `steps`, in the application's database, readable through `steps_view`. After a crash the handler runs again from its first line, and a step that succeeded returns its stored result without running its body. ([guide](./developer-guide.md#steps-variables-signals-and-timers))
- **Hangfire**: A job's return value is kept with its succeeded state in the job storage, and since 1.8 a continuation can take its parent's result. An interrupted job is retried from the start. ([docs](https://docs.hangfire.io/en/latest/background-processing/processing-background-jobs.html))
- **Quartz.NET**: The job's `JobDataMap` in the job store, saved after a run when the job asks for it; 4.x adds a run report to the execution history. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-data-map.html))
- **Workflow Core**: Steps map their output into the workflow's data object, which is persisted with the instance between steps in the configured provider. ([docs](https://workflow-core.readthedocs.io/en/latest/getting-started/))
- **Elsa**: In the instance's persisted workflow state and execution log, in the configured store; interrupted instances are requeued on startup. ([docs](https://docs.elsaworkflows.io/operate/workflow-state-and-journal))
- **Temporal**: An event in the workflow's event history, held by the Temporal Service. A worker replays the workflow code against that history, and a completed activity returns its recorded result. ([docs](https://docs.temporal.io/workflow-execution/event))
- **Durable Task**: An event in the orchestration's history in the backend: the Durable Task Scheduler, or for Durable Functions also Azure Storage or SQL Server. The orchestrator replays against it. ([docs](https://learn.microsoft.com/azure/durable-task/common/durable-task-orchestrations))

### Changing the code, while work is outstanding

- **Acta**: New code takes effect the next time a job is entered, since the handler always runs from the top. Step, signal, timer, and child names are keys into stored state and keep their meaning, and stored payloads must stay readable. There is no recorded history the code has to replay consistently. ([guide](./developer-guide.md#contract-evolution))
- **Hangfire**: A queued job names its method. Its documentation pages do not cover changing one; a post on hangfire.io advises adding a new method over changing the parameters, name, or type of one a queued job names. ([post](https://www.hangfire.io/blog/2014/05/10/are-your-methods-ready-to-run-in-background.html))
- **Quartz.NET**: A job type that can no longer be loaded puts its trigger in an error state until the stored class name is corrected, and stored job data must stay readable by the new code. ([docs](https://www.quartz-scheduler.net/documentation/troubleshooting.html))
- **Workflow Core**: A definition has an id and a version, and an instance is started for one version. Its documentation does not describe changing a definition under running instances. ([docs](https://workflow-core.readthedocs.io/en/latest/getting-started/))
- **Elsa**: Definitions are versioned: publishing makes a new version and a running instance keeps its own. An alteration moves instances to a newer version. ([docs](https://docs.elsaworkflows.io/guides/running-workflows/workflow-definition-lifecycle))
- **Temporal**: Workflow code must stay deterministic against the recorded history. A change that running executions would replay needs patching (`Workflow.Patched`), worker versioning, or a new workflow type. ([docs](https://docs.temporal.io/develop/dotnet/versioning))
- **Durable Task**: Adding, removing, or reordering an orchestrator's calls breaks instances in flight. Orchestration versioning stamps each instance with a version the code branches on. ([docs](https://learn.microsoft.com/azure/durable-task/common/durable-orchestration-versioning))

### A limit, shared by every worker, or one process's own

- **Acta**: A concurrency key and its limit, a rate limit, and a lane are held in the database, so each one holds across every worker. ([guide](./developer-guide.md#ordering-and-exclusion))
- **Hangfire**: The worker count is per server. Mutexes, semaphores, and rate windows are in the paid Hangfire Ace package, which keeps their state in the job storage. ([docs](https://docs.hangfire.io/en/latest/background-processing/throttling.html))
- **Quartz.NET**: The thread count is per node. An execution group caps a category of work per node, or across the cluster when given a cluster limit; a start rate is a limiter added as job middleware. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/execution-groups.html))
- **Workflow Core**: Its documentation describes no concurrency or rate setting. ([docs](https://workflow-core.readthedocs.io/en/latest/multi-node-clusters/))
- **Elsa**: Its documentation describes no cluster-wide cap on workflow concurrency; a distributed lock coordinates access to one instance across nodes. ([docs](https://docs.elsaworkflows.io/hosting/distributed-hosting))
- **Temporal**: A worker's concurrency and rate options are its own. `MaxTaskQueueActivitiesPerSecond` is enforced by the service across every worker of a task queue. ([docs](https://docs.temporal.io/guides/rate-limit-downstream-apis))
- **Durable Task**: Durable Functions' concurrency throttles apply to each worker. A Durable Task Scheduler task queue, a preview, has a throughput limit the scheduler enforces. ([docs](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler-task-queues))

### Why work is not moving, how an operator finds out

- **Acta**: `jobs.ExplainAsync`, `<app> jobs explain`, or the job's dashboard page says what the status means, what the job waits on, why the last attempt failed, and what an operator can do next. `jobs_view` and `events_view` answer the same from SQL. ([guide](./operator-guide.md#start-with-jobs-explain))
- **Hangfire**: The dashboard shows a job's state history and its exception, and the monitoring API returns each state with its reason. ([docs](https://docs.hangfire.io/en/latest/configuration/using-dashboard.html))
- **Quartz.NET**: The 4.x dashboard counts triggers by state, blocked, paused, and in error among them, and shows what is executing and the execution history. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/packages/dashboard.html))
- **Workflow Core**: Its documentation describes no UI. A step's error reaches the host's `OnStepError` event and the logs, and an Elasticsearch index can be searched by status. ([docs](https://workflow-core.readthedocs.io/en/latest/error-handling/))
- **Elsa**: Elsa Studio's instance viewer shows the status, incidents, and journal of an instance, and the REST API returns the same. ([docs](https://docs.elsaworkflows.io/operate/workflow-state-and-journal))
- **Temporal**: The Web UI shows a workflow's event history, pending activities, and call stack, and `temporal workflow describe` and `show` give them from the CLI. ([docs](https://docs.temporal.io/web-ui))
- **Durable Task**: The Durable Task Scheduler dashboard shows an instance's timeline and history; Durable Functions adds an HTTP status API. ([docs](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler-dashboard))

### What runs beside the application, for more than one node

- **Acta**: The application's own PostgreSQL or SQL Server database, and nothing else; one process can run on SQLite. ([support](./compatibility-and-support.md))
- **Hangfire**: A job storage: SQL Server, or Redis with the paid Pro package. ([docs](https://docs.hangfire.io/en/latest/configuration/using-sql-server.html))
- **Quartz.NET**: A shared database behind its ADO.NET job store, with clustering on and the nodes' clocks within a second of each other. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/advanced-enterprise-features.html))
- **Workflow Core**: A persistence database, plus a queue provider and a distributed lock manager. ([docs](https://workflow-core.readthedocs.io/en/latest/multi-node-clusters/))
- **Elsa**: A database, plus a distributed lock provider, a message bus for cache invalidation, and clustered scheduling. Its designer is optional. ([docs](https://docs.elsaworkflows.io/guides/clustering))
- **Temporal**: A Temporal Service: self-hosted with its persistence and visibility stores, or Temporal Cloud. ([docs](https://docs.temporal.io/temporal-service/persistence))
- **Durable Task**: Durable Functions runs on Azure Functions with a storage account and a state backend. The standalone SDKs run anywhere and keep their state in the Durable Task Scheduler, a managed Azure service. ([docs](https://learn.microsoft.com/azure/durable-task/common/durable-task-storage-providers))

If an answer here is wrong or has gone out of date, that is a defect in this page: open an issue.

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
