# Configuration and options

This page explains the knobs operators actually set. The source of truth is still
`src/Acta/Configuration/JobsOptions.cs` and `src/Acta/Configuration/SqlProviderOptions.cs`; update
those XML docs first when behavior changes. Like the rest of the SDK, the option types live in the
single `Acta` namespace: `using Acta;` covers ordinary wiring and direct option use alike.

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
| `ApplyMigrationsOnStartup` | `false` | Dev/sample convenience. Production should apply migrations before starting workers. Either way, startup verifies the migration history and refuses an unprovisioned or mismatched database. |
| `DriverVersionPolicy` | `Fail` | What startup does when the loaded ADO driver's major version differs from the one Acta was certified against: `Fail` refuses to start, `Warn` logs one warning and continues. |

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
| `ExecutionProfile` | `Buffered` | per process | Claim/dispatch strategy. See the execution profile table below. |
| `HeartbeatInterval` | 45 seconds | coordination invariant | Worker heartbeat cadence, and the only timing value you set. Must be 6 hours or less. |
| `LeaseTtlSeconds` | 180 (derived) | read-only | `HeartbeatInterval` x4. Lease window refreshed while handlers run. |
| `WorkerDeadAfter` | 315 s (derived) | read-only | `HeartbeatInterval` x7. No-heartbeat window before `sys.recovery` marks a worker Dead. |
| `WorkerRetention` | 90 days | cluster data | Terminal worker row retention, `Stopped` and `Dead` alike. Must be at least one day. |
| `JobEventsRetention` | 365 | cluster data | Retention for every event row, both audit timeline and execution ledger. |
| `AlertRetention` | 90 | cluster data | Retention for alert rows, and the hard cap: a row past it is deleted whether or not delivery settled, and a pass that aged out an unsettled one logs one warning. Also prunes the projector's `alerts-skip-*` variables. |
| `RegisterSystemJobs` | `true` | per process | Registers `sys.alerts`, `sys.recovery`, and `sys.retention` recurring jobs for each worker namespace. **Setting this `false` disables crash recovery**: `sys.recovery` is the only thing that marks dead workers and reclaims their in-flight jobs, so a dead worker's jobs stay `Executing` behind a lapsed lease permanently. The runtime warns at startup when it is off. An explicit `AddOutboxRelay` still registers `sys.outbox` with its `sys.recovery` and `sys.alerts` dependencies when this is `false`; it never adds `sys.retention`. |
| `MaxInlinePayloadBytes` | 1 MiB | cluster data | The one payload ceiling: caller-controlled inline writes (enqueue inputs, variables, progress, step results, signal values) throw past it, an oversize handler result is dropped, and it also caps the HTTP request body. |
| `AlertFailureThreshold` | 3 | cluster data | Automatic failure count threshold for escalation. |
| `AlertChannelValidationMode` | `Warn` | startup policy | `Off`, `Warn`, or `Fail` when definitions route to missing alert channels. |
| `PayloadContractDriftMode` | `Warn` | startup policy | `Warn` or `Fail` when eligible registrations change input/output contract columns. |
| `ManifestGenerationUtc` | entry assembly file time | deployment metadata | Optional. Set explicitly only when you need deterministic definition promotion, especially single-file or AOT publishes. |
| `DeploymentVersion` | assembly informational version | per process | Written to `workers`; should identify the deployed build and differ across rolling deploys. |
| `EnvironmentName` | `DOTNET_ENVIRONMENT`, then `ASPNETCORE_ENVIRONMENT`, then `Production` | per process | The value a `[JobSchedule]`'s `Environments` list is matched against, case-insensitively, to decide whether that schedule registers on this worker. A schedule with no declared environments is a wildcard and registers everywhere; a scoped one registers only where its list contains this name. Null or empty means no environment is known, so every scoped schedule is withheld and only wildcards register. |
| `AllowClockSkew` | `false` | startup policy | Downgrades excessive host/database clock skew from failure to warning. |

The coordination triple can create double execution or stale-worker misclassification if workers
disagree, and nothing can verify that agreement at runtime. So it is a single knob: set
`HeartbeatInterval` and the lease and dead-worker windows derive from it at x4 and x7. Keep that one
value the same across every replica of a namespace and the proportions cannot drift.

Shorten it to make a crash demo watchable - a 1-second beat gives a 4-second lease and a 7-second
dead-worker window - and lengthen it to cut idle database chatter. Long-running handlers do not need a
longer lease; they stay alive by heartbeating.

Engine tuning with no operator-legible meaning is not configurable: the poll floor, claim jitter,
exclusive-key bounce delay, alert delivery retries, and the Bulk-profile completion-buffer thresholds
are fixed. A value nobody can set correctly is a way to break a deployment, not a feature.

`AlertReminderInterval` (24 hours) is the exception, and it is settable because the correct value
depends on something Acta cannot see: how often you want to be paged again about a job that is still
broken. It is a delivery policy, not a deduplication rule: an unresolved incident whose delivery has
already settled — `Delivered` or `Failed` — is re-selected for delivery once this interval has passed,
so a job that has been broken all week pages daily rather than on every failure. It has no bearing on
`AlertFailureThreshold`, which counts failures within the incident itself and is reachable at any job
cadence; the two settings no longer move together. Each delivery stamps its own next reminder as it
settles, so a changed interval applies to deliveries settled after the change — incidents already
waiting keep the instant their last send scheduled, and pick up the new spacing on the reminder after
that.

`JobEventsRetention` (365 days) intentionally outlives the per-definition `[Job(JobRetention =
...)]` default (90 days) that purges terminal job rows: events are the audit ledger, so the incident
timeline survives well past the job row itself.

## Durable settings

Everything above is process configuration: read at startup, changed by redeploy. For slow-changing
configuration that belongs with the data rather than the deployment — feature flags, per-tenant
knobs, operational thresholds your handlers read — Acta keeps a durable `settings` table in the same
database as the jobs, reached through `IActaOperations.Settings`. A setting is a named text value at
one of three scopes, inferred from the arguments you pass: global (no target), one namespace, or one
job definition. Writes are audited (`setting.updated`), versioned, and optionally guarded —
`expectedVersion` turns a write into a compare-and-swap that reports the current version on a
mismatch. New setting names need no migration: rows, not columns.

```csharp
var ops = provider.GetRequiredService<IActaOperations>();

// Write a definition-scoped setting; CAS against the version you last read.
await ops.Settings.SetAsync("batch-size", "500",
    expectedVersion: 3, namespaceName: "shipping", jobName: "ship-order");

// Read it back. Reads are exact-scope: this returns null unless the
// definition-scoped row itself exists.
var setting = await ops.Settings.GetAsync("batch-size", "shipping", "ship-order");
```

`GetAsync` deliberately does not fall back across scopes. If you want definition → namespace →
global resolution, compose it in the caller — three reads, most-specific first — so the precedence
rule lives where you can see and test it, instead of inside an engine that would then own your
configuration semantics. The engine itself reads none of these values; they are for your handlers
and your operators, kept where the rest of the system of record already lives.

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

Tune profiles together with `MaxConcurrentExecutors` and `ClaimBatchSize`; the Bulk group-commit
thresholds are fixed. Measure after each change: throughput gains that overload the database,
downstream APIs, or the connection pool are not real capacity.

## External Outbox Relay

A worker that relays an external `acta_outbox` table attaches one source to a namespace with
`worker.AddOutboxRelay(sourceName, source => ...)`. The relay has no interval option of its own: the
`sys.outbox` slot runs every five seconds and its cadence is managed through the durable schedule controls
for `sys.outbox/default`. The source claim lease reuses `LeaseTtlSeconds` (180 s) rather than adding an
outbox-specific window, so a worker crash can leave claimed source rows invisible for up to that lease.
`QuarantineThreshold` on the source builder is the recoverable-failure count at which a row quarantines
(default 5); malformed and oversize rows quarantine immediately regardless.

Registering a relay adds `sys.outbox` plus its `sys.recovery` and `sys.alerts` dependencies even when
`RegisterSystemJobs` is `false`, because those are dependencies of a relay you asked for, not
automatically added framework jobs. It never forces `sys.retention`. The source provider is selected on
the builder and is independent of the ledger provider. Full guide:
[Transactional enqueue and the external outbox](./transactional-enqueue-and-outbox.md).

## Handler Policy Lives Elsewhere

Retries, backoff, execution timeout, deadline, retention, alert profile, audit level, priority, and
schedule policy are per-definition contract values from `[Job(...)]` / `[JobSchedule(...)]` and the
registered manifest. They are catalog state, not `JobsOptions`.

Use `JobsOptions` for deployment behavior and worker/runtime tuning. Use attributes for job
contract behavior that must travel with the job definition.

The framework retry defaults (`MaxAttempts = 15`, backoff `"1m..1d x2 ~10%"`) mean a persistently
failing job keeps retrying for roughly 4.4 days before it lands terminal Failed: the
delay doubles from one minute up to a one-a-day ceiling, so a dependency that breaks on a Friday
evening still has attempts left when someone reads the alert on Monday. Safe and deliberate, but
worth knowing before you go looking for why a broken job hasn't dead-lettered yet.

For production-oriented defaults and tradeoffs, including provider choice, migration ownership,
worker sizing, leases, dashboard exposure, alerts, and retention, see
[`production.md`](./production.md).
