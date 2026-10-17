# Release notes

## 1.0.0-rc.5

Tagged 2026-10-17. Upgrading from rc.4 takes the changes below.

### What a consumer must change

**Drop and reprovision every database**, including one an earlier rc.5 build provisioned: the migrations table changed shape and nothing translates it.

Packages and namespaces:

- `Acta.Relational` and `Acta.Runtime` are gone; their code ships in `Acta`, which every provider brings. Drop the references; `using Acta.Runtime;` becomes `using Acta;`, a log filter on `Acta.Runtime.X` becomes `Acta.X`, and the system-job manifest `RuntimeJobs` is `ActaJobs`.
- Each package has one namespace, its own name: replace sub-namespace usings (`Acta.Postgres.Configuration`, `Acta.AspNetCore.Configuration`, `Acta.Testing.Hosting`, ...) with the package root.

Configuration:

- `UsePostgres`, `UseSqlServer`, and `UseSqlite` take `Action<SqlProviderOptions>`; the per-provider options types are gone. On SQLite the schema is always `main`.
- An outbox source is chosen by connection string: `source.UsePostgres(connectionString)` and its siblings; the `XOutboxSourceOptions` types are gone.
- `JobsOptions.ExecutionProfile`, the `ExecutionProfile` enum, and `JobsOptions.ClaimBatchSize` are gone. A worker claims for every free executor; lower `MaxConcurrentExecutors` to claim less.
- `DriverVersionPolicy` is gone: a driver of another major version logs one warning and runs.

Jobs and statuses:

- `JobStatusCode.Dispatched` (40) is gone; a claim moves a job straight to `Executing`.
- `sys.recovery` is gone: crash recovery runs on the workers' heartbeat. `RegisterSystemJobs` still turns it off.
- A claim writes no `job.execution-started` event: `EventCode.JobExecutionStarted` (40), `ExecutionStatusCode.Executing` (50), and `ExecutionBehavior.Live` are gone. An attempt is recorded once, by `job.execution-finished`.
- `EnqueueBatchAsync` takes at most 1,000 rows (was 5,000).
- Tags attach only to tenants, namespaces, definitions, and jobs: `TagTarget.ForSchedule`, `ForWorker`, `ForAlert`, `ForEvent`, their `/tags` routes, and the `Tags` filter on the schedule, worker, alert, and event lists are gone.
- Durable settings (`ISettings`, the `settings` table, event 160) are gone.
- The `Acta.Testing` scenario API (`Scenario.For`, `ScenarioSession`) is gone: drive jobs through `IActaTestHost`.
- The gauge `acta.executing` is `acta.jobs.inflight`, filtered to `state = executing`.
- An uncaught `StepExhaustedException` fails the job at once with `job.non-retryable-exception` instead of spending its retry budget, since every retry would replay the exhausted step; on a recurring job it rolls over to the next occurrence.
- `NotSupportedException` and `NotImplementedException` roll a recurring job over to its next occurrence, alerting like any failure, instead of ending its schedule.
- `ctx.FailAsync`, `CancelAsync`, `RescheduleAsync`, and the waits called inside a step body decide the job, as they do from the handler body, instead of failing the step.
- An interval schedule fires at most once a second: a shorter interval is `ACTA0122` at compile time, and the override verb refuses one.

Identifiers:

- A name (namespace, job, schedule, signal, tag, lane, tenant, rate meter) is ASCII `dotted.kebab-case`, lowercased on input. A key (deduplication, concurrency, correlation, lock) is printable ASCII kept exactly as given, so `Order-1` and `order-1` are two keys. Display text is any Unicode.
- Two inputs tighten: a correlation key must be printable ASCII, and a tenant or lane must be a valid name.
- `IdentifierSyntax` and `AdminTextLimits` are replaced by `NameSyntax`, `KeySyntax`, and `TextLimits`.
- Renamed to match: `TenantKey` is `TenantName` (and `tenant_key` is `tenant_name`, in the outbox table too); `[Job(RateName = ...)]`; `alerts.deduplication_key`; `events.actor` and the `actor` argument of control verbs. On SQL Server an outbox table's `deduplication_key` and `concurrency_key` take the collation `Latin1_General_100_BIN2_UTF8`.

Responses and read models:

- Every list returns its own `ListXResponse` of `ListXItem`, every get its own `GetXResponse`, and every control verb its own `<Verb><Noun>Response`, replacing `PagedResult<T>`, the `*Detail` types, and the shared `*ControlResult` types. Members and JSON are unchanged; the OpenAPI schemas take the new names.
- `AdminControlAction` is gone: tenant and namespace verbs answer `ControlAction`, which gains `AlreadyInState = 6`; `VersionConflict` is 5 for every verb.
- HTTP control responses drop `message` (build text from `action` and `status`), and job controls no longer echo `jobRef`.
- `GetCheckpointsAsync`, `GetJobIdAsync`, `GetStatusAsync`, `GetInputAsync`, and `GetResultAsync` wrap their value in a response (`.Items`, `.JobId`, `.Status`, `.Input`, `.Result`); a lookup that matches nothing answers `null`.
- The enqueue contract is `EnqueueJobRequest`, `EnqueueJobResponse`, and `EnqueueJobAction`; `EnqueueBatchAsync` returns `EnqueueJobsResponse`.
- Wait results are `ChildWaitOutcome`, `ChildrenWaitOutcome`, and `SignalWaitOutcome`; `AlertDeliveryOutcome` is `AlertSendStatus`; `ActaControlDecision` is `ActaControlVerdict`; `ActaControlRequest` is `ActaControlContext`.
- Code families drop the `Job` prefix (`CheckpointKindCode`, `StepStatusCode`, `EventReasonCode`, ...), and `JobAuditLevelCode` is `DefinitionAuditLevelCode`. A stored id a newer Acta wrote decodes as the family's id-0 member instead of throwing.
- `GetOverviewQuery.IncludeSlowCounts` and the overview's `JobCount` and `SystemJobCount` are gone.
- A tag read answers `{"items": [...]}`; the input template carries its skeleton as JSON text in `templateJson` and answers 404 for an unknown job.
- A worker's last heartbeat is `LastSeenAtUtc` (`lastSeenAtUtc` in JSON) on `GetWorkerResponse` and `ListWorkersItem`, and `WorkerLastSeenAtUtc` on `GetJobExplanationLease`, the name the `workers` column has.

Schema and direct SQL:

- Migrations are `M{yyyyMMddHHmmss}_{name}.sql`, and the `migrations` table is `name` and `applied_at_utc`. Routines and views ship inside migrations; the startup installer is gone.
- `steps` and `schedules` are keyed by `(job_id, name)`: `steps_view` and `schedules_view` have no surrogate id, and a `ListSchedules` cursor from an earlier build is rejected.
- Code columns carry no value-list CHECKs, so a new code value needs no migration.
- `steps_view` and `checkpoints_view` name their decoded status `status`, as every other view does (was `state`), and `alerts_view` adds `acknowledged_at_utc`.
- Only the hot paths stay routines: enqueue, claim, complete, steps, checkpoints, signals and sleep timers, meters, the heartbeat, recovery, and retention. On SQL Server the routines and table types are named verb first (`enqueue_job`, `enqueue_jobs`, `claim_jobs`, `acta.enqueue_tags_batch`, ...).
- The SQLite outbox DDL uses `INTEGER PRIMARY KEY AUTOINCREMENT` and `STRICT`; a table from the earlier script keeps working.

### Execution engine

A worker is one claimer, its executors, and one finalizer, and the database renews only what the process holds. Nothing a consumer calls changes.

- A claim moves its rows straight to Executing: one execution path, no profiles.
- The finalizer lands up to 128 completions of any shape in one `complete_executions` call and repeats a write until it lands. A failing database stops intake instead of growing memory.
- The heartbeat renews only the executions and locks the process holds; anything else lapses to crash recovery.
- Crash recovery runs on the heartbeat: a dead worker's jobs are back within two leases, six minutes at the default heartbeat.
- An attempt cancels at its own lease deadline when renewals stop, so a database outage stops the handler.
- A definition override reaches every worker within one heartbeat.
- A claim starts where the worker's last full claim ended, so it skips the dead index entries earlier claims left behind.
- SQLite syncs every commit (`synchronous = FULL`), so the `AtMostOnce()` caveat for SQLite is gone. Single enqueues and workers with few executors pay for it in throughput, and on Windows one job at a time picks up in about 20 ms instead of 2 ([known limitations](./known-limitations.md)).
- The system slots claim at Realtime, so a Realtime backlog no longer starves them.
- Retention walks `runtimes` from a cursor instead of an index, and SQL Server no longer page-compresses `results`.

### Additions

- `JobRef`, `AlertRef`, and `WorkerRef` also parse the plain Guid behind the ref, hyphenated or not, with or without the prefix.

### Fixes

- SQL Server enqueue seeks its deduplication index instead of scanning `jobs` under `UPDLOCK`, which could escalate to a table lock.
- SQL Server enqueues of different deduplication keys no longer wait for each other: each locks its own key with `sp_getapplock`. A refused key lock raises error 51205, retried like a deadlock.
- SQL Server steps and checkpoints of different jobs no longer queue behind one range lock.
- SQL Server never escalates locks on `jobs` (`LOCK_ESCALATION = DISABLE`).
- A job's rows (`runtimes`, `results`, `steps`, `checkpoints`, `schedules`) carry no foreign key to `jobs`; `PurgeJob` and retention delete them with the job.
- The SQL Server claim no longer deadlocks with an operator verb.
- Raising or resolving an alert no longer deadlocks against a purge of its job on PostgreSQL.
- A SQL Server rate meter no longer refuses the last job of a burst.
- The overview's counts and the Failed list read indexes instead of scanning `runtimes`, and recovery reads only in-flight rows on PostgreSQL and SQLite.
- The dashboard's lists seek by namespace on PostgreSQL and SQLite, so a page costs the same at any history size.
- The overview no longer blames idle workers in one namespace for another namespace's backlog.
- `sys.alerts` skips first-attempt successes, so a failure's alert no longer waits behind them.
- The outbox relay drains a backlog in one tick, and a SQLite outbox source deletes relayed rows without scanning its backlog.
- A scoped service that throws while the attempt's scope is disposed no longer leaves the job Executing with its executor held.
- A handler cancel walks the job's descendants even when the first completion write committed and lost its answer.
- A metrics listener that throws no longer strands claimed rows until their lease lapses.
- On PostgreSQL two first starts of one step no longer fail the later one with a unique violation.
- Recovery's stale child-latch read seeks `ix_checkpoints_pending_latches` instead of scanning `checkpoints`.
- A SQLite completion stamps every row it writes with one instant.

### Documentation

- The guides are [`docs/guide.md`](./guide.md) for writing jobs and [`docs/operator-guide.md`](./operator-guide.md) for running them; contributor pages moved into `CONTRIBUTING.md`.
- The latest benchmark round is [`docs/reference/benchmark.md`](./reference/benchmark.md); the release notes record each certification verdict.

### Evidence

Certified on `ba12ef95`; the tagged commit adds test fixes and leaves `src/` unchanged. The three standard gates (10,000 jobs, 5 steps of 1 s, 7 minutes of chaos; PostgreSQL and SQL Server on 8 workers of 8 executors, SQLite on 6 of 8) and the PostgreSQL ensemble (three processes over two namespaces, 4,000 jobs) passed, with kills in flight in every work shape. Both million-job runs, on PostgreSQL and SQL Server (1,005,000 jobs each, 24 workers of 64 executors, 5 steps of 250 ms, 12 minutes of chaos), passed the same way. The benchmark round on the same commit is [`docs/reference/benchmark.md`](./reference/benchmark.md). Against rc.4's best profile on the same machine, drain more than doubles on PostgreSQL and SQL Server and grows fivefold on SQLite, and pickup latency and enqueue are level or ahead on the servers. Three cells trail: SQLite picks up one job at a time in about 20 ms on Windows ([known limitations](./known-limitations.md)), its single enqueues run at about 1,400 a second now that every commit syncs, and one deep lane runs about a quarter below rc.4's Direct profile, since each handoff waits for the previous completion to commit.

## Earlier releases

The notes for 1.0.0-rc.4 and every release before it are in [the file at that tag](https://github.com/acta-dotnet/acta/blob/v1.0.0-rc.4/docs/release-notes.md).
