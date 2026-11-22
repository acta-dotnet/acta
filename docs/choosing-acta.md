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

## Where Acta sits

Acta is **a library, on the database the app already has, with plain C# handlers, recorded steps and a readable history.**

Being established and being in that place are different things. Look at what each well-known tool is:

- **Hangfire and Quartz.NET** are established at starting work. Neither skips a job's finished work when it runs again.
- **Temporal** is established at durable workflows, and it runs as its own service, self-hosted or in its cloud.
- **Durable Task** is the closest overlap. Its orchestrators replay a history, kept in an Azure service or, for Durable Functions, in SQL Server anywhere.

## Decide in a minute

Ask these in order and stop at the first yes.

1. **Can the work be lost without anyone noticing?** Use `BackgroundService`, a timer, or Polly around the call. Nothing needs to be stored.
2. **Is it a message from one service to another?** Use a broker. Add Acta when what the message starts has to be retried, waited on, or explained afterwards.
3. **Is it live state for many small objects, answered from memory?** Use an actor framework such as Orleans. Add Acta for the long work an object starts.
4. **Do workers in several languages share one workflow, or is it drawn in a designer?** Use a workflow platform.
5. **Does the work have steps, waits, retries, or someone who will later ask what happened?** Use Acta.

## Who reaches for Acta

- Teams with a job table of their own: a status column, a retry count and a polling loop. Acta [replaces it](#replace-a-custom-job-table) with recorded steps, waits, retries and a history, in the same database.
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
- SQL Server or PostgreSQL, in a [supported version](./compatibility-and-support.md), is already part
  of the system and can own the durable state, or the deployment is one process and SQLite can.
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
- You need a frozen data model today. Acta is a release candidate: its schema freezes at 1.0.0, and
  until then any candidate may re-cut it, and a database from an earlier candidate is then dropped and
  provisioned again.

## What Acta adds over a scheduler

| Pain | Acta's answer |
| --- | --- |
| Every team uses a different timer, hosted service, or scheduler | One durable job model: `[Job]`, `EnqueueAsync`, `ScheduleAsync`, and `[Schedule]` |
| Timers and in-memory schedules lose or repeat runs after restarts | Jobs, schedules, cursors, leases, attempts, and events are persisted in SQL |
| Nobody knows what happened last night | Job status, event timelines, alerts, dashboard/API, CLI, and SQL inspection |
| A worker died mid-job | Worker leases lapse and recovery reclaims eligible jobs for retry |
| A job must not repeat blindly | Idempotency keys, durable steps, a check before a step repeats, locks, concurrency keys, and at-most-once steps |
| Operators need control | Pause, resume, cancel, restart, signal, debug, explain, and schedule pause/resume |
| Teams do not want more infrastructure | The worker is the app process; durable state is the app database |

## Start from the kind of work

| Kind of work | Use |
| --- | --- |
| A call that must answer now, to a service that may fail for a moment | An in-process resilience library such as Polly: retries, timeouts, and circuit breakers held in the process's memory. Nothing has to outlive the request. |
| A local loop where a missed tick is harmless | `BackgroundService` or `PeriodicTimer`. Nothing needs to be stored, so nothing more is needed. |
| A script or container the platform starts on a calendar | cron, systemd timers, Windows Task Scheduler, a Kubernetes CronJob, or a cloud timer. The platform owns the clock and the process. |
| Messages between services | A message broker or a streaming platform, for pub/sub, fan-out, and streams you replay. Acta can run the work a message starts, with the message id as its idempotency key. |
| Many small stateful objects (a player, a wallet, a device) that stay addressable across a cluster | A virtual-actor framework such as Orleans. Each object holds its own state in memory, and the runtime places and activates it. Acta can run the work such an object starts. |
| Background work the application owns | Acta, when the work needs retries, durable state, a history, or an operator who can pause, restart, or signal it. |
| A controller that keeps an external system current: durable progress, bounded batches, a worker that may die mid-batch | Acta, as one permanent job ([the lab](../learn/100-scheduling/109-continuous-synchronization/)). |
| A long-running workflow in .NET | Acta: one C# handler with steps, child jobs, signals, and timers, and its state as rows in your database. |
| Workflows across several languages, or drawn in a designer | A workflow platform. Acta is .NET only and has no visual designer or BPMN. |

## If you already run one of these

If the tool already makes your background work boring, visible, and consistent, keep it.

Cron, retries, dashboards, and SQL storage exist everywhere. What Acta offers is one durable work model: the timer, the queue, the retry loop, and the hand-maintained state table become one job with a schedule, a lease, a budget, and a ledger, and a parent, its children, and a long wait are jobs in the same model.

The tools fall into five groups. Each group opens with what Acta adds in that space, then says when to keep the tool and when to choose Acta.

### Job runners and schedulers

They decide when work starts, and they run it. When a job runs again, none of them skips the work it already finished: a retry runs its code from the start.

**What Acta adds:** the job carries its own progress. One that stops at step four of six runs again from the top on any worker, its three finished steps return their recorded results without running, and the new work starts at step four. It can wait days for an approval without holding a thread, and its order and limits hold across every worker.

| Tool | Keep it when | Choose Acta when |
| --- | --- | --- |
| Hangfire, a background job runner | It already runs your jobs well: a widely used runner with recurring jobs, automatic retries, continuations, and a dashboard, on storage you know, with batches and throttling in its paid packages. | Progress inside a job has to survive a restart: named steps, signals, long waits, and child jobs in one handler, with order and limits held across every worker and the job's state in SQL views you can query. |
| Quartz.NET, a scheduler | The calendar is the hard part: rich cron and calendar rules, misfire handling, and clustering you have already tuned. It has a dashboard, continuations, and execution groups that cap work across a cluster. | The job itself has state to keep: steps whose results survive a restart, long waits, child jobs, lineage, and one answer to why a job is where it is. |
| TickerQ, a scheduler | You want a source-generated scheduler with persistence, a dashboard, priorities, and durable chains of jobs with parallel branches, under its own licence with a free Community option and a commercial subscription. | One handler should keep its finished steps across a restart, wait on signals and timers, and hold lanes and limits across every worker. |

### Workflow engines

They already keep progress. They differ in where the state lives and in how you write the workflow.

**What Acta adds:** against the platforms, nothing new to run: the state is rows in the database the application already has, with no workflow server or hosted service beside it. Against the libraries, one model: a workflow is an ordinary C# handler, and it runs under the same schedules, limits, operator controls and SQL views as every other job. Against the engines that replay a history, steps answered by name: a handler runs from the top with its finished steps answered from their records, so new code is not checked against the order of calls an earlier version made. Step names and stored results must keep their meaning.

| Tool | Keep it when | Choose Acta when |
| --- | --- | --- |
| Durable Task (Durable Functions and its SDKs), an orchestration framework | You host on Azure Functions, or want a managed Azure service to hold your orchestrations' state. Durable Functions can also keep it in SQL Server. | The state belongs in the application's own PostgreSQL, SQL Server, or SQLite database, wherever the application runs, and you want checkpoints you place yourself rather than orchestrator code that replays. |
| Elsa Workflows, a workflow library | You want workflows as data that can change without a deploy, built in code or drawn in its designer, hosted in your application or on their own. | Your team writes and reviews workflows as plain C# handlers, and wants its jobs, schedules, and workflows in one model. |
| Temporal, a workflow platform | Workers in several languages share one workflow, you want deterministic replay of its event history, or you already run its server or use its cloud service. | The workflow is .NET, its state belongs in the SQL database the application already uses, and you would rather not run or rent a separate workflow service. |
| Workflow Core, a workflow library | Your workflows are graphs of steps, declared in fluent C#, JSON, or YAML, and it already persists them where you need. | You would rather write the workflow as an ordinary C# handler, with named steps, waits, and child jobs as its durable points, and run it under the same controls, limits, and SQL views as every other job. |

### Messaging

A message says that something happened. A job is work that has to finish. The two are often confused because both sit in a queue.

**What Acta adds:** what a message starts gets steps, a retry budget, a history, and an operator who can restart it weeks later. An enqueue can commit in the same transaction as your own rows, so the work exists exactly when the data does. This holds with a broker or without one: the broker carries the message, and Acta runs the work.

| Tool | Keep it when | Choose Acta when |
| --- | --- | --- |
| Kafka, RabbitMQ, Service Bus, or SQS, a message broker | You need messaging between services, those in other languages included, with whatever pub/sub, fan-out, or replay your broker or streaming platform offers. | Add Acta beside it when the work a message starts needs steps, waits, and a history beyond redelivery. Without a broker, Acta runs that work on the database alone. |
| Wolverine, a messaging framework | Your background work is message handling: a durable inbox and outbox, durable local queues, scheduled messages, and sagas, on a database you already run. | The unit is a job with a life of its own: steps, waits, child jobs, a schedule, a history, and an operator who can pause, restart, or signal it. |

### Actors

An actor is a small object that is always there to call and has an address: one object for one player, one wallet or one device. You call it by its id, and the framework finds it on a server of the cluster or activates it there. It handles one call at a time by default, so two calls never change its state at once. Its state is in memory while it is active and is saved only when its code says so. Orleans has durable features of its own too, durable jobs and journaled state, kept on the storage its silos configure.

An actor is a thing that stays. A job is work that starts and ends.

**What Acta adds:** a record of the work. An actor saves the state it chooses to save, and keeps a history only if its code writes one. The long work an actor starts becomes a job with recorded steps, waits that last days, a retry budget, and an answer to why it stopped. The nearest thing in Acta is a lane: one job at a time for a customer, an order or a device, in the order they were enqueued, held in the database. Silos form a cluster and send each other messages. Acta's workers do not call each other: each one works from the database.

**Acta and actors work together.** In an application that runs both, a grain enqueues a job and returns, and the job's handler calls the grain back when the work is done. [Integrate Acta with Orleans](../learn/700-topology-and-deployment/707-orleans-integration/) shows it running.

| Tool | Keep it when | Choose Acta when |
| --- | --- | --- |
| Orleans, a distributed application framework built on virtual actors | Your application is many small stateful objects that must stay addressable and in memory across a cluster, where a call is answered from the object's memory like a method call, with no database write unless its code makes one, and you want the runtime to place them, activate them on demand, and persist the state each one chooses to write. | The unit is a piece of work with a beginning and an end: a handler whose steps, waits, retries and history are recorded as it runs, in the SQL database the application already uses. Add Acta beside it when a grain starts work that must be retried, waited on for days, and explained afterwards. |

### In-process resilience

**What Acta adds:** the retries that have to outlive the process.

| Tool | Keep it when | Choose Acta when |
| --- | --- | --- |
| Polly, a resilience library | The caller is waiting: retries, timeouts, circuit breakers, hedging, fallbacks, and rate limiters around one call, held in memory in one process. | Add Acta around it when a retry has to outlive the process: the attempt count and the backoff are a row, a finished step is not repeated, a limit holds across every worker, and a job that ran out of attempts can be read and restarted weeks later. Polly inside a step makes the quick retries, and Acta makes the long ones. |

## How each tool handles the hard parts

The tables above say when each tool is the right one. Here the same questions are asked of Acta and of the engines among them, and each answer is the one the project's own documentation gives, with the page it comes from, as it read in November 2026. Where a project's documentation is silent, the answer says so.

### Waiting hours or days, for something outside the job

- **Acta**: The handler awaits `ctx.WaitSignalAsync<Approval>("approval")`, and `jobs.RaiseSignalAsync` releases it; `SleepAsync` and `SleepUntilAsync` are durable timers. The job holds no worker while it waits. ([guide](./developer-guide.md#steps-variables-signals-and-timers))
- **Durable Task**: The orchestrator awaits `context.WaitForExternalEvent<T>("Approval")`, raised by a client, and `context.CreateTimer` is the durable timer. ([docs](https://learn.microsoft.com/azure/durable-task/common/durable-task-external-events), [timers](https://learn.microsoft.com/azure/durable-task/common/durable-task-timers))
- **Elsa**: A blocking activity (`Delay`, `Timer`, `Event`) or `context.CreateBookmark` leaves a bookmark, and the workflow resumes when the bookmark is triggered. ([docs](https://docs.elsaworkflows.io/guides/running-workflows/long-running-workflows))
- **Hangfire**: A job is one method call. A later stage is another job, started by `BackgroundJob.ContinueJobWith` or delayed with `BackgroundJob.Schedule`; its documentation does not describe a wait for an outside event inside a running job. ([docs](https://docs.hangfire.io/en/latest/background-methods/calling-methods-with-delay.html), [continuations](https://www.hangfire.io/))
- **Orleans**: A reminder's definition is stored, and it calls the grain back on a period meant to be minutes, hours or days; a tick due while the cluster is down is missed, and a timer stops when the grain deactivates. A call fails its caller after 30 seconds by default. Durable Jobs run a one-time job at least once, and with persistent storage the job survives silo restarts. ([docs](https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders), [timeout](https://learn.microsoft.com/dotnet/api/orleans.configuration.messagingoptions.responsetimeout), [durable jobs](https://github.com/dotnet/orleans/blob/9a0833d3a45c9160e2e65f31473df47022f2c600/src/Orleans.Durable/DurableJobs/README.md))
- **Quartz.NET**: A continuation trigger (`StartAfter`) waits in the job store for its parent firing to end, and a trigger can be stored paused with a reason until someone resumes it. The wait is between jobs, not inside one. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/how-tos/job-continuations.html), [pausing](https://www.quartz-scheduler.net/documentation/quartz-4.x/how-tos/pausing-with-a-reason.html))
- **Temporal**: A `[WorkflowSignal]` method sets state and the workflow awaits `Workflow.WaitConditionAsync`; `Workflow.DelayAsync` is the durable timer. ([docs](https://docs.temporal.io/develop/dotnet/message-passing))
- **Workflow Core**: The workflow definition calls `.WaitFor("event", ...)`, released by `host.PublishEvent`; `.Schedule` and `.Recur` cover time. ([docs](https://workflow-core.readthedocs.io/en/latest/external-events/), [time](https://github.com/danielgerlag/workflow-core/blob/master/docs/control-structures.md))

### A finished step's output, where it lives, and what a restart does with it

- **Acta**: A row in `steps`, in the application's database, readable through `steps_view`. After a crash the handler runs again from its first line, and a step that succeeded returns its stored result without running its body. ([guide](./developer-guide.md#steps-variables-signals-and-timers))
- **Durable Task**: An event in the orchestration's history in the backend: the Durable Task Scheduler, or for Durable Functions also Azure Storage, Netherite or SQL Server. The orchestrator replays against it. ([docs](https://learn.microsoft.com/azure/durable-task/common/durable-task-orchestrations))
- **Elsa**: In the instance's persisted workflow state and execution log, in the configured store; interrupted instances are requeued on startup. ([docs](https://docs.elsaworkflows.io/operate/workflow-state-and-journal))
- **Hangfire**: A job's return value is kept with its succeeded state in the job storage, and with `UseResultsInContinuations` a continuation can take its parent's result. An interrupted job is retried from the start. ([docs](https://api.hangfire.io/html/T_Hangfire_States_SucceededState.htm), [results](https://www.nuget.org/packages/Hangfire/1.7.8), [retries](https://docs.hangfire.io/en/latest/getting-started/index.html))
- **Orleans**: In the grain's state, in the storage provider the silo configures (ADO.NET, Azure Storage, Cosmos DB, DynamoDB, Redis). The state is loaded when the grain activates and written only when the grain calls `WriteStateAsync`. After a silo fails the grain reactivates on another silo and reads what it last wrote. An event-sourced `JournaledGrain` can store its events instead, depending on its log-consistency provider. ([docs](https://learn.microsoft.com/dotnet/orleans/grains/grain-persistence/), [failover](https://learn.microsoft.com/dotnet/orleans/implementation/messaging-delivery-guarantees), [events](https://learn.microsoft.com/dotnet/orleans/grains/event-sourcing/log-consistency-providers))
- **Quartz.NET**: The job's `JobDataMap` in the job store, saved after a run when the job asks for it. A run report is kept in the execution history, in memory in each process unless the history is given a store. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-data-map.html), [history](https://www.quartz-scheduler.net/documentation/quartz-4.x/packages/dashboard.html))
- **Temporal**: An event in the workflow's event history, held by the Temporal Service. A worker replays the workflow code against that history, and a completed activity returns its recorded result. ([docs](https://docs.temporal.io/workflows))
- **Workflow Core**: Steps map their output into the workflow's data object, which is persisted with the instance between steps in the configured provider. ([docs](https://workflow-core.readthedocs.io/en/latest/getting-started/))

### Changing the code, while work is outstanding

- **Acta**: New code takes effect the next time a job is entered, since the handler always runs from the top. Step, signal, timer, and child names are keys into stored state and keep their meaning, and stored payloads must stay readable. New code is not checked against the order of calls an earlier version made. ([guide](./developer-guide.md#contract-evolution))
- **Durable Task**: Adding, removing, or reordering an orchestrator's calls can fail instances in flight. Orchestration versioning stamps each instance with a version the code branches on and workers match. ([docs](https://learn.microsoft.com/azure/durable-task/common/durable-orchestration-versioning))
- **Elsa**: Definitions are versioned: editing a published definition makes a new version, and publishing does not move running instances. An alteration can migrate an instance to another version. ([docs](https://docs.elsaworkflows.io/guides/running-workflows/workflow-definition-lifecycle))
- **Hangfire**: A queued job names its method. Its documentation pages do not cover changing one; a post on hangfire.io advises adding a new method over changing the parameters, name, or type of one a queued job names. ([post](https://www.hangfire.io/blog/2014/05/10/are-your-methods-ready-to-run-in-background.html))
- **Orleans**: A grain interface can carry a `[Version]`, versioned grains are assumed backward compatible by default, and silos with different versions share a cluster in a rolling upgrade. The versioning of grain state is outside that: its documentation advises a version-tolerant serialization format, so that already-stored state still loads. ([docs](https://learn.microsoft.com/dotnet/orleans/grains/grain-versioning/grain-versioning), [state](https://learn.microsoft.com/dotnet/orleans/grains/grain-persistence/))
- **Quartz.NET**: A job type that can no longer be loaded puts its trigger in an error state. The old type name can be mapped to the new one. Serialized job data must stay readable by the new code. ([docs](https://www.quartz-scheduler.net/documentation/troubleshooting.html), [job data](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-data-map.html))
- **Temporal**: Workflow code must stay deterministic against the recorded history. A change that running executions would replay needs patching (`Workflow.Patched`), worker versioning, or a new workflow type. ([docs](https://docs.temporal.io/develop/dotnet/versioning))
- **Workflow Core**: A definition has an id and a version, and an instance is started for one version. Its documentation does not describe changing a definition under running instances. ([docs](https://workflow-core.readthedocs.io/en/latest/getting-started/))

### A limit, shared by every worker, or one process's own

- **Acta**: A concurrency key and its limit, a rate limit, and a lane are held in the database, so each one holds across every worker of its namespace. A rate limit paces admissions, and is no hard burst cap. ([guide](./developer-guide.md#ordering-and-exclusion))
- **Durable Task**: Durable Functions' concurrency throttles apply to each worker. A Durable Task Scheduler task queue, a preview, has a throughput limit the scheduler enforces. ([docs](https://learn.microsoft.com/azure/durable-task/durable-functions/durable-functions-perf-and-scale), [task queues](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler-task-queues))
- **Elsa**: Its documentation describes no cluster-wide cap on workflow concurrency; a distributed lock coordinates access to one instance across nodes. ([docs](https://docs.elsaworkflows.io/hosting/distributed-hosting))
- **Hangfire**: The worker count is set on each background job server, and the free `DisableConcurrentExecution` filter narrows overlap without guaranteeing it. Mutexes, semaphores, and rate windows are in the paid Hangfire Ace package, which keeps their state in the job storage. ([workers](https://docs.hangfire.io/en/latest/background-processing/configuring-degree-of-parallelism.html), [docs](https://docs.hangfire.io/en/latest/background-processing/throttling.html))
- **Orleans**: By default a grain runs one request at a time in at most one activation, so work addressed to one grain is serialized across silos; the default directory allows occasional duplicate activations during cluster instability. A stateless worker's `maxLocalWorkers` is per silo. Its documentation describes no cluster-wide rate limit. ([docs](https://learn.microsoft.com/dotnet/orleans/grains/request-scheduling), [directory](https://learn.microsoft.com/dotnet/orleans/host/grain-directory), [stateless workers](https://learn.microsoft.com/dotnet/orleans/grains/stateless-worker-grains))
- **Quartz.NET**: The thread count is each scheduler instance's own. An execution group caps a category of work per node, or across the cluster when given a cluster limit; a start rate is a limiter added as job middleware, counted in each process. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/execution-groups.html))
- **Temporal**: A worker's concurrency and rate options are its own. `MaxTaskQueueActivitiesPerSecond` is enforced by the service across every worker of a task queue, and eager activity execution can bypass it. ([docs](https://docs.temporal.io/guides/rate-limit-downstream-apis))
- **Workflow Core**: Its documentation describes no concurrency or rate limit shared across nodes. ([docs](https://workflow-core.readthedocs.io/en/latest/multi-node-clusters/))

### Why work is not moving, how an operator finds out

- **Acta**: `jobs.ExplainAsync`, `<app> jobs explain`, or the job's dashboard page says what the status means, what the job waits on, why the last attempt failed, and what an operator can do next. `jobs_view` and `events_view` show the job and its events in SQL. ([guide](./operator-guide.md#start-with-jobs-explain))
- **Durable Task**: The Durable Task Scheduler dashboard shows an instance's timeline and history; Durable Functions adds an HTTP status API and, on SQL Server, views over the data. ([docs](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler-dashboard), [HTTP](https://learn.microsoft.com/azure/durable-task/durable-functions/durable-functions-http-features), [SQL](https://github.com/microsoft/durabletask-mssql))
- **Elsa**: Elsa Studio's instance viewer shows the status, incidents, and journal of an instance, and the REST API returns the same. ([docs](https://docs.elsaworkflows.io/operate/workflow-state-and-journal))
- **Hangfire**: The monitoring UI shows statistics, exceptions, and the history of background jobs, and the monitoring API returns each state of a job with its reason. ([docs](https://www.hangfire.io/overview.html), [API](https://api.hangfire.io/html/T_Hangfire_Storage_Monitoring_StateHistoryDto.htm))
- **Orleans**: The Orleans Dashboard, an official package that its documentation calls a preview, shows silo health, grain activations, method calls, reminders, metrics, live logs, and the current state of one grain. Its documentation describes a buffer of recent metric samples, not a record of a grain's past calls; metrics and traces export through OpenTelemetry. ([docs](https://learn.microsoft.com/dotnet/orleans/dashboard/), [monitoring](https://learn.microsoft.com/dotnet/orleans/host/monitoring))
- **Quartz.NET**: The 4.x dashboard counts triggers by state, blocked, paused, and in error among them, and shows what is executing and the execution history, which is in memory in each process unless it is given a store. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/packages/dashboard.html))
- **Temporal**: The Web UI shows a workflow's event history, pending activities, and, while a worker is running, its call stack, and `temporal workflow show` and `stack` print the history and the stack from the CLI. ([docs](https://docs.temporal.io/web-ui), [CLI](https://docs.temporal.io/cli/workflow))
- **Workflow Core**: Its documentation describes no UI. A step's error reaches the host's `OnStepError` event, and an Elasticsearch index can be searched by status. ([docs](https://workflow-core.readthedocs.io/en/latest/error-handling/), [search](https://github.com/danielgerlag/workflow-core/blob/master/docs/elastic-search.md))

### What runs beside the application, for more than one node

- **Acta**: The application's own PostgreSQL or SQL Server database, and nothing else; one process can run on SQLite. ([support](./compatibility-and-support.md))
- **Durable Task**: Durable Functions runs on Azure Functions with a storage account and a state backend, or outside Azure on SQL Server. The standalone SDKs run anywhere and keep their state in the Durable Task Scheduler, a managed Azure service. ([docs](https://learn.microsoft.com/azure/durable-task/common/durable-task-storage-providers))
- **Elsa**: A database, plus a distributed lock provider, a message bus for cache invalidation, and clustered scheduling. ([docs](https://docs.elsaworkflows.io/guides/clustering))
- **Hangfire**: A job storage: SQL Server, Redis through the Hangfire Pro package, or a community storage such as PostgreSQL. ([docs](https://www.hangfire.io/), [Pro](https://www.hangfire.io/pro/), [community](https://www.hangfire.io/extensions.html))
- **Orleans**: A durable membership table every silo shares (ADO.NET, Azure Table Storage, Cosmos DB, Redis, Consul, ZooKeeper and others), silo and gateway ports, and storage providers for grain state and for reminders when grains use them. ([docs](https://learn.microsoft.com/dotnet/orleans/implementation/cluster-management))
- **Quartz.NET**: A shared database behind its ADO.NET job store, with clustering on and the nodes' clocks within a second of each other. ([docs](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/advanced-enterprise-features.html))
- **Temporal**: A Temporal Service: self-hosted with its persistence and visibility stores, or Temporal Cloud. ([docs](https://docs.temporal.io/temporal-service/persistence), [cloud](https://docs.temporal.io/evaluate/cloud))
- **Workflow Core**: A persistence database, plus a queue provider and a distributed lock manager. ([docs](https://workflow-core.readthedocs.io/en/latest/multi-node-clusters/))

If an answer here is wrong or has gone out of date, that is a defect in this page: open an issue.

Each row describes the project as its own documentation did in November 2026.

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
Events page, drill into a job's timeline, or use the CLI.

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
    o => o.IdempotencyKey(IdempotencyKey.PerDay("reconcile-invoices", "billing")),
    ct);
```

For a host that only knows namespace and job name:

```csharp
var request = JobRequestBuilder
    .Create("billing", "reconcile-invoices")
    .Json(new { BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow) }) // the same JSON the typed record writes
    .IdempotencyKey(IdempotencyKey.PerDay("reconcile-invoices", "billing"))
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
- Add idempotency keys to platform-triggered jobs and user-triggered duplicate-prone jobs.
- Use `JobContext.JobRef` or `JobContext.JobId` in logger scopes for correlation.
- Wrap side effects in durable steps so a retry skips the ones that completed; a crash before a step records its outcome can still repeat it, so the side effect needs the provider's idempotency key, a check (`RunStepWithCheckAsync`), or `AtMostOnce()`.
- Teach operators to start with `jobs_view`, `events_view`, the dashboard, and `jobs explain`.

## Rule of thumb

Use `BackgroundService` or platform scheduling for disposable local loops.

Use Acta for durable application work that someone will later need to inspect, retry, pause, resume,
signal, debug, or explain.

For a runnable first pass, start with [your first job](./first-job.md). For the unusual implementation
ideas Acta can demonstrate live, read [Acta Engineering Labs](./engineering-labs.md). What evidence
stands behind the reliability claims, and what does not exist yet, is in
[Compatibility and support, evidence behind the claims](./compatibility-and-support.md#evidence-behind-the-claims).

## Check this page yourself

Each answer under "How each tool handles the hard parts" links the documentation it comes from, and the page is
written by Acta's author. Do not take it on trust. Paste this into the AI assistant you use; it has to be able to browse
the web.

```text
I want to check a vendor's comparison page before I trust it. Browse the web for this; do not answer from memory, and tell me when a page will not open.

1. Read https://useacta.net/choosing. It compares Acta, a durable job engine for .NET, with other tools. If it will not open, read the page's published source, https://raw.githubusercontent.com/acta-dotnet/acta/main/site/choosing.html.
2. Take every statement the page makes about another tool. Where the page links that tool's documentation, open the link and check the statement against it. Where the page says a tool's documentation does not describe something, search that documentation for it and say what you searched.
3. Take every statement the page makes about Acta itself and check it against https://useacta.net/llms.txt and the source at https://github.com/acta-dotnet/acta.

Answer with:
- a table: the statement, which tool it is about, your finding (correct, wrong, outdated, misleading, or could not check), the sentence you relied on, and its link;
- how many statements you checked, and the ones you skipped;
- what each of those tools does that Acta does not;
- the cases where one of them is a better choice than Acta.

Be strict with Acta: its author wrote the page. Recommend nothing yet. When you are done, ask me what I am building.
```

If it finds this page wrong, [open an issue](https://github.com/acta-dotnet/acta/issues). A wrong statement
here is a bug.
