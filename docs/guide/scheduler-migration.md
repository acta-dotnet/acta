# Scheduler migration guide

Acta is not another timer. Use it as the standard substrate for app-owned background work when a
team has outgrown scattered `PeriodicTimer`, cron, Hangfire, Quartz, TickerQ, platform timers, and
custom task tables.

The migration rule is simple: keep disposable host-local loops simple; move durable application work
to Acta.

## Pick One House Style

| Current choice | When it is enough | When Acta is better |
| --- | --- | --- |
| `PeriodicTimer` / `BackgroundService` | In-process housekeeping, no persistence, missed ticks are fine | Missed runs matter, retries/history are required, multiple workers may run |
| Windows Task Scheduler / cron / systemd | A host starts one process on a calendar | The application needs idempotency, retries, job lineage, and dashboard visibility |
| Kubernetes CronJob / Azure Functions timer / WebJob | The platform clock is already standard | The work still needs durable state, operator control, and SQL-first diagnosis |
| Hangfire | Established background jobs, recurring jobs, retries, dashboard | Job state should be first-class application data with generated typed contracts and ordinary SQL inspection |
| Quartz | Complex calendar semantics and mature scheduler behavior | Execution lifecycle matters more than calendar expressiveness: leases, lineage, controls, recovery |
| TickerQ | Source-generated scheduling with persistence, dashboard, priorities, and throttling | SQL must be the durable ledger for resumable work: checkpoints, signals, recovery evidence, operator intervention |
| Custom DB scheduler | Almost never | Replace it with Acta |

## From BackgroundService

Keep `BackgroundService` for local loops such as refreshing an in-memory cache, draining an in-process
channel, or polling a health dependency where losing a tick is harmless.

Move the job to Acta when someone will later ask what happened:

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

That one declaration creates a durable definition, recurring schedule row, slot job, retries,
events, dashboard/API visibility, and schedule controls. For common cron expressions, use the
`Cron` constants such as `Cron.Every5Minutes`, `Cron.DailyAt5`, or `Cron.Weekdays`.

## From Hangfire, Quartz, Or TickerQ

Do not migrate just because Acta also schedules jobs. Migrate when the job is application state and
you want the database to be the source of truth.

| Existing concept | Acta equivalent |
| --- | --- |
| Background job | `[Job]` + `IJobs.EnqueueAsync` |
| Recurring job | `[JobSchedule]` on the `[Job]` method |
| Dashboard | `MapActa()` under `/acta/jobs` |
| Retry policy | `[Job(MaxAttempts = ...)]` and backoff settings |
| Queue | Job namespace and worker registration |
| Job id | `JobRef` for public references, `JobId` internally |
| Continuations / batches | Child jobs and fan-out / fan-in APIs |
| Job parameters | Typed input contracts and payload formats |
| Job history / logs | Durable `events`, `jobs_view`, dashboard/API, CLI, and `events_view` SQL |
| Application logs | Use normal app logging and correlate with `JobRef` / `JobId` when needed |

What Acta adds is SQL-native inspectability. The event timeline is the durable job log: operators can
query `acta.events_view`, inspect the dashboard Events page, drill into a job's timeline, or use the
CLI without decoding a private scheduler store.

## Use Platform Schedulers As The Clock

Sometimes the team already has a standard clock: Kubernetes CronJob, Azure Functions TimerTrigger,
SQL Server Agent, cron, systemd timers, Windows Task Scheduler, or a cloud scheduled task.

In that case, keep the platform trigger and let it enqueue an Acta job. The platform owns when to
poke the app; Acta owns idempotency, retries, execution history, recovery, and operator controls.

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
    .DeduplicationKey(DeduplicationKey.PerDay("reconcile-invoices", "billing"))
    .Build();

await jobs.EnqueueAsync(request, ct);
```

Do not write directly into Acta tables from the platform scheduler. Call application code, an
internal endpoint, a small console host, or another supported enqueue surface so payload formats,
idempotency rules, validation, and catalog lookup all stay in one place.

## Replace A Custom Periodic Job Table

A common homemade scheduler starts like this:

```sql
Tasks(Id, Name, LastRunTime, Interval, Status, Error)
```

The usual processor scans due rows, flips a status, runs code, records a timestamp, and eventually
grows custom retry logic, locks, error history, and recovery.

Replace that table and processor with a scheduled job:

```csharp
public sealed class ImportJobs
{
    [Job("import-files")]
    [JobSchedule("default", "PT10M")]
    public Task ImportFiles(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
```

| Custom table field | Acta replacement |
| --- | --- |
| `Name` | `[Job("import-files")]` |
| `Interval` | `[JobSchedule("default", "PT10M")]` or cron |
| `LastRunTime` | event timeline and schedule cursor |
| `Status` | `jobs_view.status`, schedule status, worker lease state |
| `Error` | failed execution event, reason code, alert, result/detail payload |
| processor loop | Acta worker runtime |
| manual lock row | worker leases, exclusive keys, durable locks |

After migration, the operational question is no longer "what did our scheduler code do?" It is
"what do the durable rows say?" Start with [SQL recipes](./sql-recipes.md), then use the dashboard
or CLI for control actions.

## Migration Checklist

- Pick one namespace per owning service, such as `billing`, `users`, or `imports`.
- Convert recurring work to `[Job]` + `[JobSchedule]`.
- Keep platform clocks only when the organization already standardizes on them.
- Add deduplication keys to platform-triggered jobs and user-triggered duplicate-prone jobs.
- Use `JobContext.JobRef` or `JobContext.JobId` in logger scopes for correlation.
- Use durable steps for side effects that must not be repeated after a retry.
- Teach operators to start with `jobs_view`, `events_view`, the dashboard, and `jobs explain`.

See [Choosing Acta](../choosing-acta.md) for the high-level decision rule and
[Operator guide](./operator-guide.md) for day-2 controls.
