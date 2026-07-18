# Choosing Acta

Most .NET teams start by asking the same question:

> How should we run periodic, delayed, or scheduled jobs?

The hard part is not finding a timer, cron parser, or scheduler. .NET teams already have many
options: `BackgroundService`, `PeriodicTimer`, Hangfire, Quartz, TickerQ, MassTransit scheduling,
cloud timers, Kubernetes CronJobs, OS schedulers, and custom database tables.

The hard part is choosing one execution model before the codebase grows several.

A scheduler decides **when work should start**. Acta is for cases where you also need to know
**what happened after it started**.

Acta is for **app-owned background work that has become application state**: work with durable
inputs, retries, leases, attempts, results, event history, operator controls, and questions like:

- What ran?
- What failed?
- What runs next?
- Why is this job stuck?
- Can I pause, resume, restart, signal, or debug it?

Use Acta when scheduling is not the whole problem anymore. Use it when background work itself needs
to be durable, visible, inspectable, and recoverable through the SQL database the application already
uses.

> Acta is an early preview. This page is a fit guide for evaluation, not a claim that every existing
> scheduler should be replaced.

For concrete migration patterns from `BackgroundService`, cron, Hangfire, Quartz, TickerQ, platform
schedulers, and custom task tables, see [Scheduler migration](./guide/scheduler-migration.md).

## The Real Split

The question is not **scheduler or no scheduler**. The question is what kind of work you are
scheduling.

| Need | Use |
| --- | --- |
| A local loop where missed ticks are acceptable | `BackgroundService`, `PeriodicTimer`, or a plain hosted service |
| The platform starts one script, endpoint, or container on a calendar | cron, systemd timers, Windows Task Scheduler, Kubernetes CronJob, Azure Functions timer |
| App-owned work that needs retries, state, audit, recovery, or operator visibility | Acta |
| Distributed messaging, pub/sub, event streaming, or fan-out transport | a message bus or streaming platform |
| Deterministic event-history replay, BPMN, or visual workflow orchestration | a workflow engine, not Acta |

## Use Acta When

- The work belongs to the application, not only to the host or platform.
- The work must survive process restarts, deployments, or worker crashes.
- A missed, failed, duplicated, or stuck run needs to be visible.
- Operators need to pause, resume, cancel, restart, signal, explain, or debug jobs.
- Retries, delayed execution, recurring schedules, durable waits, or child jobs should share one model.
- SQL Server, PostgreSQL, or SQLite is already part of the system and can own the durable state.
- You want background work to be inspectable as rows, events, schedules, leases, and checkpoints.

## Do Not Use Acta When

- Losing a tick is harmless and no one will ask what happened.
- The work is host maintenance better owned by the OS, container platform, or cloud scheduler.
- You need a message bus, event stream, or transport abstraction.
- You need deterministic workflow replay.
- You need BPMN, a visual process designer, or hosted workflow SaaS.
- A duplicate external side effect would be unrecoverable, and you cannot make it idempotent,
  guarded, or reconciled with durable step semantics.

## What Acta Adds Over A Scheduler

| Pain | Acta's answer |
| --- | --- |
| Every team uses a different timer, hosted service, or scheduler | One durable job model: `[Job]`, `EnqueueAsync`, delayed jobs, and `[JobSchedule]` |
| Scheduled jobs vanish or double-run after restarts | Jobs, schedules, cursors, leases, attempts, and events are persisted in SQL |
| Nobody knows what happened last night | Job status, event timelines, alerts, dashboard/API, CLI, and SQL inspection |
| A worker died mid-job | Worker leases lapse and recovery reclaims eligible jobs for retry |
| A job must not repeat blindly | Deduplication keys, durable steps, locks, exclusive keys, and at-most-once step policy |
| Operators need control | Pause, resume, cancel, restart, signal, debug, explain, and schedule pause/resume |
| Teams do not want more infrastructure | The worker is the app process; durable state is the app database |

## If You Already Use Hangfire, Quartz, Or TickerQ

If Hangfire, Quartz, or TickerQ already makes your background work boring, visible, and consistent,
keep it. Acta is not here to replace every scheduler in every codebase.

Acta fits when the problem has moved beyond **when should this run?** into **what durable state does
this work create, and how do we operate it?**

| Existing tool | Keep it when | Consider Acta when |
| --- | --- | --- |
| Hangfire | You want a familiar persistent job runner with recurring jobs, retries, and dashboard | Job state should be first-class application data in SQL, with typed contracts, durable steps, signals, child jobs, and SQL-first inspection |
| Quartz | Calendar sophistication, cron behavior, misfires, and scheduler features are the main problem | The execution lifecycle matters more than the calendar: lineage, leases, waits, recovery, and operator controls |
| TickerQ | You want a source-generated scheduler with persistence, dashboard tooling, chaining, priorities, and concurrency controls | SQL must serve as the durable operational ledger for resumable work: checkpoints, signals, recovery evidence, and operator intervention |

The migration trigger is not **we need cron**. The migration trigger is one of these:

- We have several competing ways to run background work.
- Nobody can explain what happened to last night's job.
- We need to inspect, restart, or debug old jobs.
- We need durable steps, child jobs, signals, long waits, or operator controls.
- We do not want a broker, sidecar, hosted scheduler, or separate control plane.
- We want the application database to be the source of truth.

## The Smallest Scheduled Job

Use a scheduled Acta job when the interval matters enough to be durable and visible:

```csharp
public sealed class CleanupJobs
{
    [Job("cleanup-expired-sessions")]
    [JobSchedule("every-5-minutes", "5m")]
    public Task CleanupExpiredSessions(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
```

During local demos, use a short interval such as `10s` so the run is visible. In production, use the
business cadence: `5m`, `1h`, or a cron expression.

For a runnable first pass, start with [Quickstart](./quickstart.md). For the unusual implementation
ideas Acta can demonstrate live, read [Acta Engineering Labs](./engineering-labs.md).

## Rule Of Thumb

Use `BackgroundService` or platform scheduling for disposable local loops.

Use Acta for durable application work that someone will later need to inspect, retry, pause, resume,
signal, debug, or explain.
