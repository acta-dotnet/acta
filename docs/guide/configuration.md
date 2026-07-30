# Configuration and options

This page explains the knobs operators actually set. The source of truth is still
`src/Acta/Configuration/JobsOptions.cs` and `src/Acta.Relational/Connections/SqlProviderOptions.cs`; update
those XML docs first when behavior changes.

## Wiring Shape

Every host chooses one durable provider and then declares either a worker namespace with `Run(...)`
or an enqueue-only catalog with `Reference(...)`.

```csharp
builder.Services.UseActa(j =>
{
    j.UsePostgres(pg =>
    {
        pg.ConnectionString = builder.Configuration.GetConnectionString("acta")!;
        pg.Schema = "acta";
        pg.ApplyMigrationsOnStartup = false;
    });

    j.ConfigureOptions(o =>
    {
        o.ExecutionProfile = ExecutionProfile.Direct;
        o.MaxConcurrentExecutors = 32;
        o.ClaimBatchSize = 64;
        o.DeploymentVersion = builder.Configuration["GIT_SHA"];
    });

    j.Run<BillingJobs>(namespaceName: "billing", ownerTeam: "payments");
});
```

`Run<TManifest>(...)` registers definitions, schedules, a worker row, and a claim loop for one
namespace. `Reference<TManifest>(...)` only makes typed enqueue available in a process that does not
run handlers.

## Provider Options

SQL Server, Postgres, and SQLite all inherit the same provider option shape. SQLite is embedded and
uses schema `main`; SQL Server and Postgres default to schema `acta`.

| Option | Default | Use |
| --- | --- | --- |
| `ConnectionString` | empty | Provider connection string. Required unless a local helper supplies one. |
| `Schema` | `acta` (`main` on SQLite) | Schema/container for Acta tables, views, indexes, routines, and migration history. |
| `CommandTimeout` | 30 seconds | Runtime operation timeout applied by the store. |
| `DeadlockRetryAttempts` | 5 | Store-level retry count for database deadlock victims. Set `1` to disable retry. |
| `ApplyMigrationsOnStartup` | `false` | Dev/sample convenience. Production should apply migrations before starting workers. |

Local concepts, demos, and Anvil use `UseLocalDatabase(...)`, which selects a provider from explicit
argument, `Acta:Provider`, `ACTA_LOCAL_PROVIDER`, then SQLite as the zero-setup default.

## Worker And Cluster Options

`JobsOptions` is validated at startup. Invalid retention windows, executor counts, payload caps,
alert settings, or lease/heartbeat relationships fail fast.

| Option | Default | Scope | Notes |
| --- | --- | --- | --- |
| `MaxConcurrentExecutors` | `clamp(ProcessorCount * 4, 8, 64)` | per process | Maximum in-flight handler executions per worker runtime. |
| `ClaimBatchSize` | 32 | per process | Ready rows pulled per claim poll. Raise with executor count and DB capacity. |
| `SafetyPollInterval` | 1 second | per process | Idle polling ceiling when no wakeup is received. Must be at least 1 second. |
| `MinPollFloor` | 50 ms | per process | Anti-spin floor for due-but-locked horizons. Must be `> 0` and `<= SafetyPollInterval`. |
| `ClaimIdleJitterMax` | 100 ms | per process | Idle poll jitter. Must be between 0 and 1 second. |
| `ExecutionProfile` | `Buffered` | per process | Claim/dispatch strategy. See the execution profile table below. |
| `LeaseTtlSeconds` | 180 | coordination invariant | Lease window refreshed while handlers run. Keep this consistent across workers. |
| `HeartbeatInterval` | 45 seconds | coordination invariant | Worker heartbeat cadence. Keep the lease about four times this value. |
| `WorkerDeadAfter` | 5 minutes | coordination invariant | No-heartbeat window before `sys.recovery` marks a worker Dead. Must be greater than `LeaseTtlSeconds`. |
| `WorkerRetention` | 90 days | cluster data | Dead worker row retention. Must be at least one day. |
| `JobEventsRetentionDays` | 365 | cluster data | Retention for every event row, both audit timeline and execution ledger. |
| `AlertRetentionDays` | 90 | cluster data | Retention for settled alert rows. In-flight alert delivery rows are not purged by age. |
| `RegisterFrameworkJobs` | `true` | per process | Registers `sys.alerts`, `sys.recovery`, and `sys.retention` recurring jobs for each worker namespace. An explicit `AddOutboxRelay` still registers `sys.outbox` with its `sys.recovery` and `sys.alerts` dependencies when this is `false`; it never adds `sys.retention`. |
| `MaxInlinePayloadBytes` | 256 KB | cluster data | Hard cap for caller-controlled inline writes: enqueue inputs, variables, progress, step results, and signal values. Handler results warn-and-persist when larger. |
| `AlertDedupeWindow` | 1 hour | cluster data | Bucket width for deduped manual and automatic alerts. Must be positive. |
| `AlertDeliveryMaxRetries` | 5 | cluster data | Delivery retries before an alert lands terminal Failed. |
| `AlertFailureThreshold` | 3 | cluster data | Automatic failure count threshold for escalation. |
| `AlertChannelValidationMode` | `Warn` | startup policy | `Off`, `Warn`, or `Fail` when definitions route to missing alert channels. |
| `PayloadContractDriftMode` | `Warn` | startup policy | `Warn` or `Fail` when eligible registrations change input/output contract columns. |
| `ManifestGenerationUtc` | entry assembly file time | deployment metadata | Optional. Set explicitly only when you need deterministic definition promotion, especially single-file or AOT publishes. |
| `DeploymentVersion` | assembly informational version | per process | Written to `workers`; should identify the deployed build and differ across rolling deploys. |
| `AllowClockSkew` | `false` | startup policy | Downgrades excessive host/database clock skew from failure to warning. |

The coordination invariant options are the ones that can create double execution or stale-worker
misclassification if workers disagree. Keep `LeaseTtlSeconds`, `HeartbeatInterval`, and
`WorkerDeadAfter` the same across every replica of a namespace.

`JobEventsRetentionDays` (365 days) intentionally outlives the per-definition `[Job(JobRetention =
...)]` default (90 days) that purges terminal job rows: events are the audit ledger, so the incident
timeline survives well past the job row itself.

## Execution profiles

| Profile | Use when | Tradeoff |
| --- | --- | --- |
| Buffered | Conservative default | More observable state, more round trips |
| Direct | Higher throughput with durable completion | Less `Dispatched` visibility |
| Bulk | Re-runnable high-volume work | Relaxed completion durability; crash can re-run completed handler work |

`Buffered` claims jobs into `Dispatched`, then starts execution with a second durable transition.
That intermediate state is useful when operators care about "claimed but not yet running" as a
visible state.

`Direct` claims straight into execution. It removes the `Dispatched` visibility window and reduces
round trips while keeping per-job completion durable on SQL Server and PostgreSQL. On SQLite, Direct
also uses SQLite's faster synchronous mode, appropriate for local and embedded scenarios where that
tradeoff is accepted.

`Bulk` is Direct plus group-committed completions. The handler may finish successfully before Acta
flushes the completion batch; if the process crashes in that window, recovery sees the job as still
executing and can run the handler again. `Bulk` is therefore only for jobs whose side effects are
idempotent or safely repeatable. Good candidates: cache warmers, search indexing, rebuildable
projections, idempotent batch transforms. Bad candidates: charging cards, sending emails without
idempotency, irreversible external side effects, one-time webhooks.

Tune profiles together with `MaxConcurrentExecutors`, `ClaimBatchSize`, and the Bulk-only
`BatchCompletionSize` / `BatchCompletionInterval` / `BatchCompletionMaxBytes`. Measure after each
change: throughput gains that overload the database, downstream APIs, or the connection pool are
not real capacity.

## External Outbox Relay

A worker that relays an external `acta_outbox` table attaches one source to a namespace with
`worker.AddOutboxRelay(sourceName, source => ...)`. The relay has no interval option of its own: the
`sys.outbox` slot runs every five seconds and its cadence is managed through the durable schedule controls
for `sys.outbox/default`. The source claim lease reuses `LeaseTtlSeconds` (180 s) rather than adding an
outbox-specific window, so a worker crash can leave claimed source rows invisible for up to that lease.
`QuarantineThreshold` on the source builder is the recoverable-failure count at which a row quarantines
(default 5); malformed and oversize rows quarantine immediately regardless.

Registering a relay adds `sys.outbox` plus its `sys.recovery` and `sys.alerts` dependencies even when
`RegisterFrameworkJobs` is `false`, because those are dependencies of a relay you asked for, not
automatically added framework jobs. It never forces `sys.retention`. The source provider is selected on
the builder and is independent of the ledger provider. Full guide:
[Transactional enqueue and the external outbox](./transactional-enqueue-and-outbox.md).

## Handler Policy Lives Elsewhere

Retries, backoff, execution timeout, deadline, retention, alert profile, audit level, priority, and
schedule policy are per-definition contract values from `[Job(...)]` / `[JobSchedule(...)]` and the
registered manifest. They are catalog state, not `JobsOptions`.

Use `JobsOptions` for deployment behavior and worker/runtime tuning. Use attributes for job
contract behavior that must travel with the job definition.

The framework retry defaults (`MaxAttempts = 15`, backoff `"1m..8h"`) mean a persistently failing
job keeps retrying for roughly days before it lands terminal Failed: safe and deliberate, but worth
knowing before you go looking for why a broken job hasn't dead-lettered yet.

For production-oriented defaults and tradeoffs, including provider choice, migration ownership,
worker sizing, leases, dashboard exposure, alerts, and retention, see
[`production.md`](./production.md).
