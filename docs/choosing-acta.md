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

> This page is a fit guide, not a claim that every existing scheduler should be replaced.

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

## Who Reaches For Acta

- App developers whose retries stopped being enough: the job's state became application state.
- Platform teams who need evidence and intervention: what ran, what is stuck, pause, restart, signal.
- AI and agent developers whose steps are expensive, long-running, tool-using, human-gated, or unsafe to restart from the top.

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

## Atomic Enqueue With Business Data

When a business-data change and an Acta enqueue must commit or roll back together, the path depends on
where the two live:

| Situation | Use |
| --- | --- |
| Business data and the Acta ledger share one database, and you own an explicit transaction | Direct transactional `IJobs` enqueue: pass your `DbTransaction` to the enqueue overload |
| The business data is in a different database | The external outbox: stage an `acta_outbox` row with `AddToActaOutboxAsync` on your provider transaction, and an Acta-owned relay ingests it |
| An EF Core application, either of the above | The same two paths, holding the transaction via `Database.GetDbTransaction()`. There is no Acta EF package |
| Any ORM wrapping the provider transaction | The same two paths, once the ORM exposes or unwraps the native transaction (OrmLite: `ToDbTransaction()` then a concrete cast) |
| You want a single universal exactly-once guarantee | Neither. Execution is at-least-once; handlers own idempotency for external side effects |

Full walkthrough, including the canonical table, cadence, and quarantine policy:
[Transactional enqueue and the external outbox](./guide/transactional-enqueue-and-outbox.md).

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

## AI And Agent Steps

A retried agent step repeats latency, tokens, tool calls, and a human approval, and a
nondeterministic model does not repeat the same output. Your AI can reason again; it should not have
to do the work again. "Persist the output of this step" is also easier to explain than deterministic
replay.

Acta sits underneath agent frameworks (Semantic Kernel, the OpenAI SDK, a custom loop) and gets no
`Agent`, `Prompt`, or `LLM` types, ever. It does not care whether a step calls a model, Stripe, or
FFmpeg. The primitives are general: `RunStepAsync` persists a completed step's output so it is not
paid for twice, `SleepAsync` and `WaitSignalAsync` hold a human-approval gate for days without
holding a worker, and `MapAsync` fans a batch out with lineage. Durable variables
(`GetOrSetVariableAsync`) and `SetProgressAsync` give the agent working memory that survives the
process: another worker can resume the job and find the plan, the partial results, and how far it
got.

## Physical Processes And Layered Systems

The same shape holds where the work is physical. The layering is ERP -> Acta (durable
business/process coordination) -> MES / SCADA / APIs / operators -> machines. Acta is not an MES,
SCADA, or PLC system and never touches real-time or safety control. The shape generalizes: ERP ->
Acta -> payment rails, or agent framework -> Acta -> model and tool APIs.

The durable wait here is one the reader already knows is real: a six-hour cure, a cooldown, a
maintenance window. Nobody argues it should hold a thread, and `SleepAsync` and `WaitSignalAsync`
are the same primitive either way.

When a step is physically consequential, a repeated step is a physical event: a second machine
command, a second label. For those steps, see
[At-most-once steps](./guide/handler-contract.md#at-most-once-steps).

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
