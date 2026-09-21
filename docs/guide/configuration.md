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
concurrency-key bounce delay, alert delivery retries, and the Bulk-profile completion-buffer thresholds
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
tradeoff is accepted. `Bulk` on SQLite degrades to `Direct` in full — no completion batching exists
there, and it selects the same relaxed synchronous mode — see
[known limitations](../technical/known-limitations.md#storage-and-providers).

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

Retries, backoff, execution timeout, deadline, retention, alert profile, audit level, priority,
concurrency limit, and schedule policy are per-definition contract values from `[Job(...)]` /
`[JobSchedule(...)]` and the registered manifest. They are catalog state, not `JobsOptions`.

Use `JobsOptions` for deployment behavior and worker/runtime tuning. Use attributes for job
contract behavior that must travel with the job definition.

The framework retry defaults (`MaxAttempts = 15`, backoff `"1m..1d x2 ~10%"`) mean a persistently
failing job keeps retrying for roughly 4.4 days before it lands terminal Failed: the
delay doubles from one minute up to a one-a-day ceiling, so a dependency that breaks on a Friday
evening still has attempts left when someone reads the alert on Monday. Safe and deliberate, but
worth knowing before you go looking for why a broken job hasn't dead-lettered yet.

### Concurrency limit

`[Job("rebuild-index", ConcurrencyLimit = 4)]` caps how many attempts of that definition execute at
once, cluster-wide, between 1 and 1024. Like every other policy slot it has an operator override on
the definition row, so the effective limit is the override when one is set and the declared value
otherwise.

The limit is enforced on a key, and the key is the enqueue's `ConcurrencyKey` when the job carries
one, otherwise the definition name. A key with no limit admits one at a time, which is what a
concurrency key has always meant. Each admitted attempt holds one slot lease for as long as its
handler runs; a job that finds every slot held skips its handler and re-arms Ready after the fixed
bounce delay, without spending a retry.

Two definitions that share a key share its slots, and the key's capacity is the largest limit among
the participants. A definition with the smaller limit competes for the first N slots only, so
lowering one definition never reduces another participant's capacity.

A changed limit reaches a worker on its next successful definition-policy reload. Until every worker
has observed it, admissions may still use the old limit, and attempts already holding higher slots
run to completion; the overshoot after a decrease is therefore bounded by the old limit until the
last worker observes the change, plus one attempt's duration.

### Rate limit

`[Job("send-invoice", RateLimit = "10/s", RateKey = "stripe")]` caps how *often* attempts of that
definition start, where the concurrency limit caps how *many* run at once. The two are independent
gates and a job passes both. The format is `N/s`, `N/m`, or `N/h` with `N` a positive whole number,
and the rate may not exceed 1000 per second: the interval between admissions is the period divided by
`N`, rounded up to whole milliseconds, which is the resolution the meter is stored at. Anything else
is rejected at build time by the source generator, again at worker startup, and again at the override
gate. Like every other policy slot the rate has an operator override on the definition row.

`RateKey` names the meter. Omit it and the meter is the definition name, so a rate alone throttles
just that definition - and a definition *named* `stripe` is on the same meter as one declaring
`RateKey = "stripe"`. Definitions that share a meter **must declare the same rate**; worker startup
rejects a namespace where two of them disagree, because one meter cannot run at two rates. Unlike the
rate, the key has no operator override: moving a definition onto another meter changes which
definitions it competes with, which is a contract change rather than a dial.

**An override applies to every definition sharing the meter.** Write a `RateLimit` override on one
participant and the same write lands on every other definition on that meter in one statement, so the
meter never carries two rates; clear the override on one and it clears on all of them too. Two
operators retuning the same meter at once do not split it - the last write to reach the database wins
for every participant, not just the one addressed. A definition that joins a meter whose participants
already carry an override must declare the meter's effective rate (the override, not the bare code
value) or worker startup rejects it, naming the meter and its effective rate. A meter whose
participants disagree is reported by a startup and policy-reload warning; retune it by overriding
any participant.

**Admission is a reservation, not a retry loop.** The meter keeps the instant the next admission is
due. A job that arrives after that instant runs immediately. A job that arrives early is given the
next free instant, and the attempt re-arms Ready at exactly that instant without spending retry
budget, carrying the reason `job.rate-limited`; that is never a failure, so it writes no
`Failures`-level event and raises no alert. Because the turn is booked rather than contended for, a
backlog drains in arrival order at the rate, with no re-racing. A turn under a quarter of a second away is not
re-armed at all: the executor keeps the job and sleeps until the exact instant, on a wait the meter
measured on the database clock, then takes the turn; only a farther turn goes back through the claim
path, and it costs that job **one re-arm**, never more.

**A booked turn stays valid for one second past its instant**, or one interval when the interval is
longer. A job that comes back inside that window runs without touching the meter, because the meter
already counted it when it booked the turn, and a second covers the worker's pickup latency at any
rate. A job whose turn went stale - every executor was busy for longer than that when its instant came
round - goes back through the meter and is given a new one, which costs it a second re-arm. That is
what stops a queue of overdue jobs from all starting the moment executors free up: after a long stall
they are released at the burst, not in one crowd. One meter is one row lock, so a single key tops out
around a thousand admissions per second cluster-wide, which is also the fastest rate the parser accepts.

The contract: the meter allocates at most `R*T + B` turns in any `T` seconds, where `B` is the burst an
idle meter hands out back to back: one second's worth of the rate, at least one, so `10/s` and `600/m`
both admit ten at once and then one per 100 ms, and `5/m` admits one and then one every twelve seconds.
A per-minute or per-hour rate therefore reads as a smooth rate with a small cushion, never a whole
period's worth released at once. A booked turn may be taken up to its validity window `W` late, one
second or one interval when the interval is longer, so a window of `T` seconds can hold that much of
late turns on top: at most `R*(T + W) + B`. Within 10% of `R` over 60
seconds of continuous demand holds as long as executors are free - the other half of the contract, since
the rate caps how fast jobs *start*, so if every executor is busy the realized rate is lower.

A booked turn is held for fifteen minutes past its instant, a fixed grace that no worker setting moves, so a late but living job never loses
one. Past that, the ordinary lock expiry sweep collects it: a job that was cancelled, or reclaimed
before it came back, simply loses its place and is metered afresh when it next asks - one more re-arm,
at the tail of the queue. The meter itself is swept the same way once it has been idle for a full
burst, which restores it to a fresh meter with its full burst.

For production-oriented defaults and tradeoffs, including provider choice, migration ownership,
worker sizing, leases, dashboard exposure, alerts, and retention, see
[`production.md`](./production.md).
