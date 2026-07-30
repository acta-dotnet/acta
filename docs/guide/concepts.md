# Concepts and glossary

Acta is SQL-native durable work for .NET. It records jobs, retries, schedules, checkpoints, events,
workers, and operator controls in your database.

Durable work lives in SQL: the durable record of background work is kept as rows you can `SELECT`.
There is no broker required, no sidecar, no hosted control plane, and no deterministic replay engine.

This is the single conceptual on-ramp for Acta: the vocabulary to hold before reading the handler
contract, operator guide, or generated references. It is descriptive. The source-owned API docs in
`src/Acta` are authoritative for exact signatures and contracts.

For the rationale behind these choices (why one table, why leaderless, why checkpoints over replay),
see [`design.md`](../internals/design.md). For a diagram-first view of the same model, see
[`architecture-diagrams.md`](../technical/architecture-diagrams.md).

## Glossary

| Term | Meaning |
| --- | --- |
| **Job** | The only independently claimable work unit. One identity/input row in the `jobs` table plus its 1:1 live-state row in `runtimes`. One-shot work, delayed work, recurring slots, coordinating parents, child jobs, and system maintenance all use the same tables. |
| **Execution** | One recorded run of a handler. Every run (success or failure) is recorded as a paired `job.execution.started` / `job.execution.finished` event on `(JobId, ExecutionNumber)`. |
| **Attempt** | One try at the work that counts against retry policy when it fails. `MaxAttempts` is the unified retry budget for both jobs and steps. Successful executions are recorded but do not consume the budget. |
| **Status** | The lifecycle-or-outcome enum column on every entity that has one (`JobRuntime.Status`, `JobWorker.Status`, ...). The job's `runtimes` row stores current status; events explain transitions. |
| **state** | The column substrate tables use (`JobCheckpoint.state`, `JobStep.state`), marking them Job-internal rather than operator-facing lifecycle. |
| **Firing** | One scheduled occurrence of a recurring `[JobSchedule]` (a moment in time). 1:1 with an execution in steady state; misfire policy decides what happens to missed firings. |
| **Namespace** | The service-owned execution boundary. A worker claims only jobs in its own namespace. One `Run(...)` owns one namespace; a process can host several. |
| **Tenant** | Optional, validated scope naming the customer / business entity a job is *about*. Registered in the Acta-owned `tenants` catalog by an opaque external `TenantKey` (GUID / ULID / customer code) and resolved to `jobs.tenant_id` at enqueue. Audit / query / runtime scope only, not an ownership, claim, or scheduling boundary. |
| **Durable slot** | A named, run-once-or-resume primitive owned by a job (step, variable, signal, timer, lock, alert). Shares the job's claim, lease, retry, cancellation, and event lifecycle; never independently claimable. |
| **Provider** | The durable SQL backend. SQL Server and Postgres are the distributed providers; SQLite is the embedded single-node provider used by concepts, demos, tests, and local exploration. |

## One durable work unit

A **Job** is the only independently claimable work unit. Shape is inferred from the descriptor and
row fields; there is no `JobKind` discriminator. Coordinating jobs are ordinary jobs that drive work
through durable slots.

## Three identities

A job carries three keys, each for a different audience:

- `JobId`: internal numeric engine identity used by joins, logs, cursors, leases, and SQL debugging.
- `JobRef`: public opaque handle (`job_` + Crockford Base32) used by dashboards, HTTP routes, CLI
  output, and callers. Numeric ids never appear in dashboard JSON or HTTP routes.
- `DeduplicationKey`: caller-defined dedupe and lookup key for root jobs.

`JobRequestBuilder.Deduplicate(businessKey)` is the primary definition-aware API: because the raw
builder already knows the job name, it composes `<job-name>:<business-key>`. Typed
`JobEnqueueOptionsBuilder` is configured before routing resolves the definition, so it accepts only
an already composed final key through `DeduplicationKey(key)`. Use the static
`DeduplicationKey.ForDefinition`, `PerHour`, `PerDay`, or `PerTimeBucket` helpers to compose that
final key. `AcrossDefinitions` is the explicit cross-definition form. Cross-definition time buckets
are intentionally not exposed as a combined helper; derive that business key in application code
only when the use case genuinely spans job definitions.

Deduplication and exclusive keys are namespace-scoped, never tenant-scoped: `invoice-123` used by
two tenants is one key. When the business identity is tenant-relative, compose the key with
`DeduplicationKey.ForTenant(tenantKey, businessKey)` (also valid for `ExclusiveKey` values, and
nestable as the business key of `ForDefinition`).

## Namespace vs tenant

Namespace and tenant answer two different questions and never substitute for each other.

- **`JobNamespace` = who owns and runs the work**: the microservice / work-ownership boundary. It owns workers, job definitions, schedules, and system jobs, and it is the hot-path claim filter. Good namespaces name a service or work domain: `billing`, `cards`, `kyc`, `notifications`. Bad namespaces smuggle in a customer, environment, or worker identity: `tenant-acme`, `premium-customers`, `prod`, `worker-a`. Enqueuing into another service's namespace is service-to-service routing, not multi-tenancy.
- **Tenant = who the work is about**: the customer / business entity a single job concerns. A tenant does **not** own workers, namespaces, job definitions, schedules, or system jobs. It is set per job at enqueue (`TenantKey`, resolved to `tenant_id`), immutable afterward, and inherited by child jobs; a child naming a different tenant than its parent is rejected unless the enqueue opts in with `TenantKey(key, overrideParent: true)`. It is **audit / query / runtime scope**: it surfaces on `JobContext.TenantId` and `JobContext.TenantKey`, snapshots, lists, and job-scoped events, and as a filter on job/event queries. It is **not** a scheduling, claim, idempotency, or exclusive-key scope: those stay namespace-scoped.

The `TenantKey` is an **opaque external identifier** (a GUID, ULID, or customer code, not a human label: that goes in the tenant's `display_name`, with longer notes in `description`). Register tenants with `operations.Tenants.RegisterAsync(tenantKey, displayName?, description?)`: insert-or-return-existing, so a new tenant is created Active and an existing one is returned untouched. Status changes go through `SuspendAsync`/`ResumeAsync`, metadata through `UpdateMetadataAsync`, and `GetAsync(tenantKey)` is the point read. Enqueuing an unknown or suspended tenant key is rejected atomically. A job with no tenant (including every system job) carries `tenant_id = NULL`; a definition can make the choice durable with `[Job(TenantRequirement = Required)]` (or `Forbidden`), enforced at the enqueue boundary in the database.

Two boundaries to keep straight when designing tenancy around Acta:

- **The tenant catalog is global per store.** `tenants` carries no namespace: within one installed Acta schema, `acme` is one shared identity, and suspending it withdraws admission for explicit `acme` enqueues in every namespace using that store. Suspension is admission control, not work closure: jobs already admitted keep running, and a running workflow may still create children that inherit the suspended tenant. The guarantee is the commit boundary: an enqueue transaction beginning after the suspend commits is rejected.
- **The tenant field is not a security or isolation boundary.** Acta validates, persists, propagates, and surfaces the tenant identity; it does not authorize callers, filter application data, select per-tenant databases, or create a failure domain. Applications enforce data access using `JobContext.TenantKey` as the authoritative identity, and requirements like separate residency, backups, or blast radius take separate Acta stores or deployments, not a tenant row.

## Executions and attempts

An **Execution** is one recorded run of the handler; the event ledger records started and finished
events for every run. An **Attempt** is one try that counts against retry policy when it fails.
A handler can be re-entered after a crash, retry, signal, or sleep; completed durable slots are not
repeated.

Acta is at-least-once. It makes durable state transitions and committed checkpoints repeat-safe; it
does not make arbitrary external side effects exactly-once. Handlers own side-effect idempotency.

## Durable slots inside a job

`JobContext` exposes durable primitives that belong to the parent job and share its claim, lease,
retry, cancellation, and event lifecycle.

| Primitive | Table | Model |
| --- | --- | --- |
| Step | `steps` | Named run-once slot. A succeeded step returns its stored result on replay instead of running the body again. |
| Variable | `checkpoints` (kind `variable`) | Durable per-job value and compute-once cache. |
| Signal | `checkpoints` (kind `signal`) | External release point. `WaitSignalAsync` parks until `IJobs.RaiseSignalAsync` sets the named slot. |
| Timer/Sleep | `checkpoints` (kind `timer`) | Durable wait slot. `SleepAsync` / `SleepUntilAsync` free the executor and resume when due. |
| Lock | `leases` | Handler-facing mutual exclusion through `RunWithLockAsync`. |
| Alert | `alerts` | Operator-facing incident row raised manually or projected from failures. |

Use a child job when work needs its own claim, retry, status, cancellation, lineage, or operator
visibility. Use a step when work is part of one parent job's internal state machine.

## The substrate tables: checkpoints and steps

Simple job-internal durable state lives in one merged `checkpoints` table, keyed by
`(job_id, kind_code, name)` with a CASCADE FK to `jobs`. The kind discriminates the substrate
feature that owns the slot; every row shares the parent job's claim, lease, retry budget, and
event ledger, and is never independently claimable, schedulable, paused, or cancelled.

| Checkpoint kind | Carries |
| --- | --- |
| `variable` | Durable per-job values and the compute-once determinism cache. |
| `signal` | External release points awaited by `WaitSignalAsync`. |
| `timer` | Durable timers from `SleepAsync` / `SleepUntilAsync`. |
| `progress` | The job's single progress slot written by `SetProgressAsync`. |
| `child-latch` | Child terminal-outcome latches that release a waiting parent. |

Steps are richer (attempt counters, per-call retry budget, stored results) and stay in the
separate `steps` table.

## Schedules, workers, and providers

A **Schedule** is catalog metadata plus a recurring job slot. Due schedules coalesce into one
execution of the same durable slot; `JobContext.TriggeringScheduleNames` names what fired.

A **Worker** is a peer process loop: it registers a namespace, heartbeats, claims ready rows,
executes handlers, and refreshes leases. There is no leader or control-plane service.

A **Provider** is the durable SQL backend. SQL Server and Postgres are the distributed providers;
SQLite is the embedded single-node provider used heavily by concepts, demos, tests, and local
exploration.

## Operator view

The `runtimes` row stores each job's current status. **Events** explain transitions. The dashboard,
HTTP API, CLI, `IJobs` read APIs, and raw SQL all read the same durable state. From here:

- [`operator-guide.md`](./operator-guide.md) for SQL, CLI, dashboard/API, retention, alerts, maintenance, security, and the production checklist.
- [`troubleshooting.md`](./troubleshooting.md) for symptom-driven debugging.
- [`data-model.md`](../reference/data-model.md) for tables and columns.
- [`code-families.md`](../reference/code-families.md) for status, reason, event, alert, schedule, and worker codes.
- [`conformance-contracts.md`](../reference/conformance-contracts.md) for tested provider guarantees.
