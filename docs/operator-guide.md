# Operator guide

Running Acta: day-2 operations first, then the production guide, alerting, failure modes,
troubleshooting, and SQL recipes. Writing jobs is the [guide](./guide.md).

Day-2 operations. The operator surface is three things: the `IJobs` facade for per-job controls and
read-only list APIs (optionally surfaced by the embedded dashboard package), and plain SQL against
the ledger for everything else diagnostic. Queries below are
Postgres-flavored against the `acta` schema; on SQL Server bracket the identifiers and swap the
interval arithmetic (`DATEADD`).

The job's `runtimes` row stores current state. Job events explain transitions. Use the CLI, API,
dashboard, or a SQL query over the events timeline to find why.

Curated operator views cover the common read paths: `acta.jobs_view`, `acta.events_view`,
`acta.checkpoints_view`, `acta.steps_view`, `acta.schedules_view`, `acta.alerts_view`,
`acta.workers_view`, `acta.definitions_view`, and `acta.tags_view`. Views are for ad-hoc inspection
and learning the schema; they are not a stable integration API. Do not build pipelines, ETL, or
external tooling on them: their shape may change in any release. Runtime code does not depend on
them, and migrations do not derive from them.

Reference docs: [`data-model.md`](./reference/data-model.md) (every table and column),
[`code-families.md`](./reference/code-families.md) (every status / reason / event code),
[`conformance-contracts.md`](./reference/conformance-contracts.md) (the tested guarantees).

## Status quick reference

Live statuses: `Ready = 10`, `Blocked = 15` (a laned job waiting behind its lane's head), `Suspended = 20`,
`Paused = 30`, `Executing = 50`.
Terminal statuses follow the band rule: `100` is success (`Succeeded`), `200+` is unsuccessful
(`Failed = 200`, `Cancelled = 220`). The reason for a transition lives on `events`, not on the
runtime row.

Workers use the same bands: `Active = 10`, `Draining = 80` (stopped polling, finishing in-flight),
`Stopped = 100`, `Dead = 200`.

## How a worker ended

There are exactly two ways a worker process ends, and the terminal status names which one:

| Status | What happened | Event and reason |
|---|---|---|
| `Stopped = 100` | Exited cleanly through SIGTERM / `IHostedService.StopAsync` | `121 worker.stopped` / `100 worker.clean-shutdown` |
| `Dead = 200` | Heartbeat went stale past the liveness window; crash recovery flipped it | `122 worker.died` / `101 worker.heartbeat-stale` |

On a terminal worker, `modified_at_utc` is the instant it ended: both transitions stamp it. A worker
killed outright never reaches the clean path, so it stays `Active`, or `Draining` if the kill landed
mid-drain, until recovery marks it `Dead` - which is why a kill and a crash are indistinguishable
here, and correctly so. A worker cut off by a database outage longer than `WorkerDeadAfter` is
marked `Dead` too, and its first heartbeat after the outage makes it `Active` again, with no event
of its own.

```sql
-- Workers that died in the last hour, and how long they had been running.
SELECT worker_id, host, deployment_version, created_at_utc, modified_at_utc
  FROM acta.workers_view
 WHERE status_code = 200 AND modified_at_utc > now() - interval '1 hour'
 ORDER BY modified_at_utc DESC;
```

The same rows are readable through `IActaOperations.Workers.ListAsync`, filtered with
`new ListWorkersQuery(Status: WorkerStatusCode.Dead)`.

## Common queries

Read freely — the views exist for it. Repair through the control verbs, not through UPDATE. The
schema's own checks guard row *shape* (lease all-or-nothing, paired payload formats, non-negative
counters), but they cannot guard *history*: the runtime fences every mutation
on version counters and lease ownership, so a hand-edited `version`, `execution_number`, or lease
column produces a row the engine either refuses to touch or mistakes for another worker's — with no
constraint to stop you. Every control verb (restart, cancel, reschedule, purge) makes its change
under those fences and leaves an audited event; a manual UPDATE does neither.

Lanes make a hand edit worse. Setting a laned job back to Ready, or a Blocked job to Ready, puts two
jobs of one lane in flight, and no repair can take that back. Setting a lane's head to a terminal
status by hand releases nobody: the recovery sweep finds such a stranded lane and releases its next
job, with a `job.lane-repaired` event. Each pass visits a bounded share of the namespace's lanes, so a
large namespace can take several passes. Restart a laned job with the restart verb, which reactivates it
under the lane lock and runs it right after the current head.

```sql
-- One job, by public ref (the job_ref backs the "job_..." value dashboards and clients use).
SELECT * FROM acta.jobs_view WHERE job_ref = :job_ref;

-- One job, by internal id (engine identity; logs and SQL joins use it).
SELECT * FROM acta.jobs_view WHERE job_id = :job_id;

-- By deduplication key (root jobs; children are keyed per parent instead).
SELECT * FROM acta.jobs_view
 WHERE namespace = 'billing' AND deduplication_key = :key AND parent_id IS NULL;

-- Failures in the last 24 hours, newest first.
SELECT job_id, job_ref, namespace, job_name, failure_count, modified_at_utc
  FROM acta.jobs_view
 WHERE status = 'failed' AND modified_at_utc > now() - interval '24 hours'
 ORDER BY modified_at_utc DESC;

-- Queue depth: claimable backlog per namespace.
SELECT namespace, COUNT(*) AS ready
  FROM acta.jobs_view
 WHERE status = 'ready'
 GROUP BY namespace;

-- One tenant's work, by the tenant name the caller enqueued with (names fold lowercase).
SELECT job_id, job_ref, namespace, job_name, status
  FROM acta.jobs_view
 WHERE tenant_name = :tenant_name;

-- Backlog per tenant, busiest first. Untenanted work groups under NULL.
SELECT tenant_name, COUNT(*) AS ready
  FROM acta.jobs_view
 WHERE status = 'ready'
 GROUP BY tenant_name
 ORDER BY ready DESC;

-- In-flight work and who holds it.
SELECT job_id, status, leased_by_worker_id, leased_by_worker_host, lease_expires_at_utc
  FROM acta.jobs_view
 WHERE status = 'executing';

-- The whole timeline for a job tree (parents + children share lineage_root_id).
SELECT created_at_utc, job_id, event, from_status, to_status, reason, reason_message, detail_format, detail_text
  FROM acta.events_view
 WHERE lineage_root_job_id = :root_job_id
 ORDER BY created_at_utc, event_id;

-- Children of a parent.
SELECT job_id, job_ref, deduplication_key, status
  FROM acta.jobs_view
 WHERE parent_id = :job_id;

-- Suspended jobs and the signals they wait on.
SELECT c.job_id, c.job_ref, c.checkpoint_name, c.state, c.value_format, c.value_text
  FROM acta.checkpoints_view c
  JOIN acta.jobs_view j ON j.job_id = c.job_id
 WHERE j.status = 'suspended' AND c.kind = 'signal';

-- Durable step slots for a job: which named steps ran, their outcome, and retry state.
SELECT step_name, state, attempt_number, next_retry_at_utc, reason, reason_message
  FROM acta.steps_view WHERE job_id = :job_id ORDER BY step_name;

-- Durable sleep timers for a job: when each named wait is due.
SELECT checkpoint_name, state, due_at_utc
  FROM acta.checkpoints_view
 WHERE job_id = :job_id AND kind = 'timer'
 ORDER BY due_at_utc;

-- Jobs carrying an exact tag, optionally narrowed by its preserved value.
SELECT t.scope_id AS job_id, j.status, t.tag_name, t.tag_value
  FROM acta.tags_view t JOIN acta.jobs_view j ON j.job_id = t.scope_id
 WHERE t.scope = 'job' AND t.namespace = :ns
   AND t.tag_name = :tag_name AND t.tag_value = :tag_value;
```

The reason for a transition lives on the `events` timeline (and per-step on `steps`); the
`runtimes` row carries status only.

## Control verbs

All per-job actions go through `IJobs` (or your own endpoint that wraps it); the framework stamps
the actor and reason, so callers cannot forge the audit trail.

```csharp
// Address the job by its public ref (what dashboards and clients hold), by deduplication key,
// or by internal id (advanced/debug); all three resolve through the same JobLookup.
JobLookup job = JobRef.Parse("job_2n1t201rmv87aae5j4csam8000");

await jobs.CancelAsync(job, "superseded by order-9");  // cascades to non-terminal descendants
await jobs.PauseAsync(job);                             // hold a not-yet-running job
await jobs.ResumeAsync(job);                            // Paused -> Ready (recurring-aware)
await jobs.RestartAsync(job);                           // resets failure budget + retention and Exhausted steps, runs now
await jobs.RaiseSignalAsync(job, "approval", payload);  // releases a Suspended WaitSignalAsync
await jobs.RescheduleAsync(job, inTwoHours);             // move a waiting job's next run (re-arms Ready)
await jobs.ReprioritizeAsync(job, JobPriorityCode.High); // change claim priority in place
await jobs.PurgeAsync(job);                              // hard-delete a terminal job (audited as job.purged)
```

Cancel of a parent cancels the whole non-terminal subtree (descendants carry reason
`job.parent-cancelled`). Restart leaves a terminal row's history intact and re-arms the same id; there
is never a replacement row. Reschedule applies to Paused/Suspended/Ready/Blocked rows and re-arms them
Ready, or Blocked behind their lane; purge refuses a non-terminal job, a job that has child jobs, and a child whose parent is not
terminal (finish or cancel the tree, then purge the leaves first).
Every verb returns Applied / Rejected / NotFound rather than throwing on an illegal transition.

Cancel, pause, resume, restart, reschedule, and reprioritize also take an optional `expectedVersion`
after `actor`. Left null, the verb writes unconditionally, which is what an operator acting on a
moving job wants. Pass the `Version` from a `GetJobResponse` (or from a previous control result) and the
write becomes a compare-and-set: a row that changed underneath you answers `VersionConflict` with its
current version instead of overwriting someone else's decision.

### Schedule controls

Address a schedule by its recurring slot job plus schedule name. Definition-backed slot jobs use the
job name as their deduplication key.

```csharp
var nightly = new ScheduleLookup(
    JobLookup.ByDeduplicationKey("billing", "reconcile-ledger"),
    "nightly");

await operations.Schedules.TriggerNowAsync(nightly, reasonMessage: "validate repaired upstream");
await operations.Schedules.PauseAsync(nightly, untilUtc: maintenanceEndsUtc, reasonMessage: "maintenance");
await operations.Schedules.ResumeAsync(nightly, reasonMessage: "maintenance complete");
```

Trigger now, misfire catch-up, normal cursor movement, and historical backfill are deliberately
different operations. Use [Schedule operations](./guide.md#schedule-operations) before choosing a control.

### What each verb does from each status

Read from the provider routines, not from intent. Every verb answers one of three ways: **applied**
(`200`), **rejected** (`409`, the job exists but its status forbids the transition), or **not found**
(`404`); a verb given an `expectedVersion` has a fourth, **version conflict** (`409`, the row moved
since you read it), and nothing is written. All four carry the same body, so a
client reads `action`, the resulting `status`, and the row's `version` without special-casing the code.

`R` Ready · `B` Blocked · `S` Suspended · `P` Paused · `X` Executing · `✓` Succeeded · `F` Failed ·
`C` Cancelled. **A** applied, **409** rejected.

| Verb | R | B | S | P | X | ✓ | F | C | Result on applied |
|---|---|---|---|---|---|---|---|---|---|
| `pause` | A | A | A | A | 409 | 409 | 409 | 409 | Paused |
| `resume` | 409 | 409 | 409 | A | 409 | 409 | 409 | 409 | Ready, or Blocked behind its lane |
| `restart` | A | A | A | A | 409 | A | A | A | Ready, or Blocked behind its lane; failure budget, retention, and Exhausted steps reset |
| `cancel` | A | A | A | A | A | 409 | 409 | 409 | Cancelled, cascading to non-terminal descendants |
| `purge` | 409 | 409 | 409 | 409 | 409 | A | A | A | Row hard-deleted, `job.purged` event kept |
| `reschedule` | A | A | A | A | 409 | 409 | 409 | 409 | Ready at the new instant, or Blocked behind its lane |
| `reprioritize` | A | A | A | A | A | 409 | 409 | 409 | Priority changed, status untouched |
| `input` (amend) | A | A | A | A | 409 | A | A | A | Input replaced, format round-tripped |
| `signal` | A | A | A | A | A | 409 | 409 | 409 | Slot set; a Suspended job waiting on that name goes Ready |

Repeating a verb is a no-op only where the table says so. **`pause` is idempotent** (Paused stays
Paused, applied). **`resume` is not**: it accepts only Paused, so resuming an already-running job is
a 409 rather than a silent success. That asymmetry is deliberate - pausing twice expresses the same
intent, resuming something that was never paused does not.

Five rules the table cannot show:

- **`purge` also rejects a job that has children**, whatever its status. `parent_id` carries no
  database cascade, so purging a parent would leave a child pointing at a row that no longer exists.
  It also rejects a completed child whose parent is not terminal: the parent's replay dedupes onto
  that child and reads its result, so purging it would run the child again. Once the tree is
  terminal, purge the leaves first.
- **`cancel` on an Executing job** marks the row Cancelled and cancels the running attempt's token;
  the handler still has to return. The row is terminal before the process notices.
- **`signal` on a Paused job** records the slot and leaves the job Paused. The signal is not lost;
  it is waiting for a `resume`.
- **`input` on a terminal job is allowed**, which looks odd until you want it: amend the input of a
  failed job, then `restart` it. Only Executing rejects, because that is the state where a worker
  may already have read the payload. A job that stored no input has nothing to
  amend and answers `409` from the endpoint before the routine is reached.
- **`restart` and `resume` have two more refusals.** A finished laned job is not restarted while an
  unfinished ancestor or descendant sits in its lane, and a recurring job whose schedules offer no
  run is neither resumed nor restarted. A recurring job restarts at its next scheduled run, not now.

## Tenant and namespace administration

Tenants and namespaces carry an operator-controlled status: `active` resolves at enqueue, `suspended`
withdraws admission. Tenant suspension is admission control, not work closure: new enqueues naming the
tenant name are rejected once the suspend has committed, while jobs already admitted keep running and a
running workflow may still create children that inherit the suspended tenant (an inherited tenant id
carries no key to re-check). Suspension is reversible and status-only.

```csharp
await operations.Tenants.SuspendAsync("cust-4711", "billing hold");
await operations.Tenants.ResumeAsync("cust-4711");
await operations.Tenants.UpdateAsync("cust-4711", displayName: "ACME Corp", description: null, expectedVersion: version);
await operations.Namespaces.SuspendAsync("billing", "incident 1042");
await operations.Namespaces.ResumeAsync("billing");
await operations.Namespaces.UpdateAsync("billing", ownerTeam: "payments", description: null, expectedVersion: version);
```

Suspend/resume are idempotent (already-in-state succeeds as a no-op, reported as alreadyInState, with no event); metadata updates are
version-CAS guarded, a stale `version` returns a conflict with the current row state instead of writing.
Tenant metadata is `displayName` + `description`; namespace metadata is `ownerTeam` + `description`
(namespaces have no display name). Null clears a field. The seeded `sys` namespace cannot be suspended or
edited. Every applied transition is audited on the events timeline in the admin band
(`tenant.suspended` 10 through `namespace.updated` 22); tenant events land on the seeded
`sys` namespace, namespace events on the namespace itself.

A tenant is registered with `ITenants.RegisterAsync(tenantName, displayName, description)` or
`POST /tenants`; an enqueue that names an unregistered tenant is refused.

Over HTTP the same verbs are control-gated (`EnableControls` + confirmation header):
`POST /tenants`, `POST /tenants/{tenantName}/suspend|resume`, `PATCH /tenants/{tenantName}`, `POST /namespaces/{name}/suspend|resume`,
`PATCH /namespaces/{name}`. Reads are ungated: `GET /tenants` pages the catalog and
`GET /tenants/{tenantName}` is the point read (`ITenants.GetAsync` in code).

An enqueue that trips a tenant or namespace guard throws a typed `EnqueueRejectedException` whose
`Reason` is machine-readable: `NamespaceSuspended`, `TenantSuspended`, `TenantUnknown`,
`TenantRequired` / `TenantForbidden` (the definition's `TenantRequirement` policy), and
`TenantMismatch` (a child named a different tenant than its parent without the explicit override),
`RouteUnknown` (no definition by that namespace and name), `DefinitionRetired`, `AncestorLane` (a child
in the lane of an unfinished ancestor), and `LaneIsolation` (a laned enqueue inside a caller
transaction whose isolation reads a snapshot older than the lane lock). Guard-wrapped HTTP handlers
map it to 409 with the reason in the ProblemDetails. A missing or terminal parent still surfaces as a
raw provider error. The same guards apply when the external outbox relays a record: a record that trips one
(for example a Required definition staged without a tenant) lands in the outbox failure path
instead of enqueuing.

## Built-in control CLI

Every Acta host doubles as a control CLI: `<app> jobs <verb>` runs the verb against the database and exits, starting no worker and running no migrations. `j.DisableCli()` opts out.

```
<app> jobs help
<app> jobs info    <job-ref|deduplication-key|id>
<app> jobs status  <job-ref|deduplication-key|id>
<app> jobs result  <job-ref|deduplication-key|id>
<app> jobs explain <job-ref|deduplication-key|id>
<app> jobs events  <job-ref|deduplication-key|id> [--take <n>] [--after <cursor>]
<app> jobs cancel  <job-ref|deduplication-key|id> [--reason <msg>]
<app> jobs pause   <job-ref|deduplication-key|id> [--reason <msg>]
<app> jobs resume  <job-ref|deduplication-key|id> [--reason <msg>]
<app> jobs restart <job-ref|deduplication-key|id> [--reason <msg>]
<app> jobs signal  <job-ref|deduplication-key|id> <name> [--value <json>]
<app> jobs debug   <job-ref|deduplication-key|id> [--break]
```

A `job_...` target is a job ref, another non-numeric one a deduplication key, and an integer the internal id; an omitted target is read from the clipboard. Add `--ns <ns>` to pick the namespace for a deduplication-key lookup and `--json` for JSON output.

`explain` is where to start for any stuck, waiting, or failed job: it reads the job's status, lease, worker, slots, and latest reason in one snapshot and says what happened and what to do next. It cannot know what your code or an external system did.

`debug` resets the job to Ready with restart semantics, claims exactly it, and runs the handler in this process, so a breakpoint in the handler stops it; leases keep renewing while it is stopped. A live worker can take the job first, and an Executing job is refused. `--break` raises the debugger at the handler.

| Exit code | Meaning |
|------|--------------------------------------|
| 0    | Applied, found, or debug run succeeded |
| 1    | Rejected or handler failed           |
| 2    | Job not found                        |
| 64   | Usage error                          |
| 130  | Cancelled (Ctrl-C)                    |

## HTTP API and dashboard

`Acta.AspNetCore` serves the operator surface over HTTP, with an embedded dashboard:

```csharp
app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.ConfigureEndpoints = group => group.RequireAuthorization("ActaOperators");
});
```

- **Local only by default.** Remote requests answer 403, and a local one must address `localhost` or an IP. To expose it, set `LocalOnly = false` and require your own authorization through `ConfigureEndpoints`, which covers the page, its assets, and the API; without it startup throws unless `UnsafeAllowAnonymousRemoteAccess = true`. The package ships no authentication, and CORS is not authorization. `MapActaApi` maps the API alone.
- **Reads expose payloads.** Anyone who can read can see every job's input, result, and checkpoint values, up to `MaxInlinePayloadBytes`. Treat read access as access to every payload in the ledger.
- **Controls are opt-in.** `EnableControls = true` maps the control routes (job verbs, signals, schedules, alerts, tenants, namespaces, outbox). Each control request must send `X-Acta-Control: true`, a guard against cross-site requests, not authentication. An outcome answers 200 applied, 404 not found, or 409 rejected; problem details are kept for malformed input, authorization, and faults.
- **Jobs are addressed by ref** (`/acta/api/v1/jobs/{jobRef}`) or by deduplication key (`/jobs/by-key?jobNamespace=&deduplicationKey=`). `EnableNumericIdLookup = true` also allows `/jobs/id:{n}` on reads, for debugging from a log line.
- **Lists page by cursor.** Pass `NextCursor` back as `Cursor`; a cursor is tied to its query, and a stale one is rejected. Pages default to 50 and cap at 100, and `IncludeTotal` adds a count (events only with a job filter).
- `GET /capabilities` reports whether controls are enabled, so a client can hide edit UI.

The [OpenAPI document](./reference/openapi.json) lists every route.

### Control authorization

Register an `IActaControlAuthorizer` and every control request asks it first: a denial answers 403
before the verb runs, and reads never ask. Each request names its verb, derived from the route:
`/jobs/{jobRef}/cancel` is `cancel`, `/tenants/{tenantName}/suspend` is `tenants.suspend`, and where one
route carries two mutations the method says which, as in `tags.add` and `tags.remove`. The request's
`HttpContext` carries the route values, method, and claims for a rule that needs more. The verbs:

```text
alerts.acknowledge
alerts.resolve
cancel
definitions.retire
definitions.tags.add
definitions.tags.remove
definitions.update
enqueue
input
namespaces.resume
namespaces.suspend
namespaces.tags.add
namespaces.tags.remove
namespaces.update
outbox.discard
outbox.requeue
pause
purge
reprioritize
reschedule
restart
resume
schedules.overrides
schedules.pause
schedules.resume
schedules.trigger
signals
tags.add
tags.remove
tenants.register
tenants.resume
tenants.suspend
tenants.tags.add
tenants.tags.remove
tenants.update
```

## Retention and purge

A job landing terminal gets `retention_until_utc` from its definition's `JobRetention` (default 90 days). The hourly `sys.retention` job deletes terminal jobs past it, with their checkpoints, steps, and results, and deletes events, alerts, and stopped or dead workers past `JobEventsRetention`, `AlertRetention`, and `WorkerRetention`. Each tick walks `runtimes` from where the last one stopped, up to two million rows, so a job goes within one walk of its deadline.

A recurring slot is never purged while its definition is live. Its results are capped by the definition's `RecurringResultCap` (default 1), trimmed on every completion.

`AlertRetention` is a hard cap: an alert past it is deleted even if it was never delivered or is still open, and a pass that deleted undelivered alerts logs one warning (`reason=alert-retention-cap`). If that line appears, find out why delivery is not settling.

| | Retention sweep | Manual purge |
| --- | --- | --- |
| Trigger | `sys.retention` finds terminal jobs past `retention_until_utc` | `IJobs.PurgeAsync`, `POST /jobs/{jobRef}/purge`, or the dashboard |
| Eligible job | Terminal and past its deadline, unless it is a stopped recurring job whose definition is live | Terminal now |
| Child jobs | Keeps a completed child while its parent is live; a finished tree drains leaves first | Refuses a parent with children and a child whose parent is live |
| Job event | None | `job.purged`, carrying the job's ref and name |
| The job's events and alerts | Age out on their own windows | Deleted at once |

Manual purge removes Acta's record of one finished job and nothing else, such as your business data or backups. It is not backlog control: pause, cancel, reschedule, or add capacity instead.

## What maintenance does for you

Workers heartbeat their leases (`HeartbeatInterval`), and once a lease one worker's heartbeat takes
the namespace's recovery pass, which reclaims jobs whose lease lapsed (back to Ready in budget, terminal Failed past `MaxAttempts`, with retention stamped),
marks silent workers Dead after `WorkerDeadAfter`, and re-raises any child-completion latch a crash
left pending; `sys.retention` runs the purge. If a job looks stuck in `Executing`, check the
worker's `last_seen_at_utc` first; recovery is automatic once the lease lapses.

## Production guide

Production-facing checklist for running Acta safely. The public API, the schema, and the persisted
codes are frozen at 1.0, and schema changes ship only as additive migrations, so this is the guidance
for production deployments, not for an evaluation.

### Production checklist

- Choose SQL Server or PostgreSQL for distributed multi-worker deployments. Use SQLite for embedded
  single-node deployments, local development, demos, and tests.
- Apply Acta migrations in a deployment step before workers start. Keep
  `ApplyMigrationsOnStartup = false` outside development.
- Pin one Acta package version across all workers in a namespace.
- Deploy with a rolling restart: apply migrations, start the new build, stop the old one. See
  [Rolling deploys](#rolling-deploys) for what each build does with the other's jobs.
- Set `DeploymentVersion` to a build id or git SHA. Set `ManifestGenerationUtc` only when you need
  deterministic definition promotion across packaged or single-file deployments.
- Keep `HeartbeatInterval` identical across replicas of a namespace; the lease and dead-worker windows
  derive from it.
- Size total handler concurrency as `worker process count * MaxConcurrentExecutors`, then validate
  against database capacity and downstream systems.
- Keep inline payloads small. Store large or sensitive bodies externally and enqueue references.
- Expose the dashboard/API remotely only behind host authentication and authorization.
- Enable HTTP controls only for authorized operators.
- Configure alert channels and decide whether missing alert channels should warn or fail startup.
- Review retention windows for jobs, events, alerts, and terminal (stopped or dead) workers.
- Review [retention versus manual purge](#retention-and-purge), including backup retention.
- Verify backup and restore behavior with queued, executing, and terminal jobs.
- Drill the [documented failure modes](#what-happens-if) for worker loss, SQL outage, and restore.
- Test a rolling deploy with mixed old and new workers and queued rows before relying on one.
- Review known limitations before using Acta in production: [`known-limitations.md`](./known-limitations.md).

### Provider choice

Use SQL Server or PostgreSQL when more than one process will claim work from the same namespace.
They are the server providers and the right default for service deployments. A PostgreSQL database
must keep a deterministic collation, which is the default, so that keys compare exactly.

Use SQLite when the database is embedded in one process. SQLite is useful for concepts, local
exploration, tests, and small single-process tools. Several processes can share one SQLite file on one
machine for a try-out, as the demos do, but that is not a supported deployment.

Redis wakeup is optional. SQL polling is the correctness baseline; a wakeup transport only lowers
pickup latency.

### Migration ownership

In production, deployment owns migrations, not application startup:

```csharp
j.UsePostgres(pg =>
{
    pg.ConnectionString = builder.Configuration.GetConnectionString("acta")!;
    pg.ApplyMigrationsOnStartup = false;
});
```

- Run the provisioning script for your provider ([`schema-pg.sql`](./reference/schema-pg.sql), [`schema-mssql.sql`](./reference/schema-mssql.sql), [`schema-sqlite.sql`](./reference/schema-sqlite.sql)) from the release pipeline under a DDL-capable principal, with a client that stops at the first error (`psql -v ON_ERROR_STOP=1`, `sqlcmd -b`, `sqlite3 -bail`). It applies only the migrations the database lacks, so re-running it is safe. Routines and views ship inside the migrations.
- The application principal then needs only DML and EXECUTE on the Acta schema.
- The SQL Server script turns on `READ_COMMITTED_SNAPSHOT`, which disconnects other sessions once: run it in a maintenance window.
- A migration runs in one transaction under an exclusive schema lock: on a live system it briefly blocks the statements it collides with, and jobs wait rather than fail.
- Every start checks that the database has a history row for each migration the build ships, and fails with instructions if not. Rows the build does not know are fine, so an older worker runs against a newer database. A hand-edited object is not detected.

### Rolling deploys

Deploy: apply migrations, start the new workers, stop the old ones once the new ones are healthy. Rollback: start the old build, stop the new one. While both builds run:

- Either build may claim a job only the other can run. The worker without the handler hands it back to `Ready` without charging a failure and stops claiming that definition.
- Registration never retires a definition, so the old fleet finishes its jobs and a rollback finds nothing missing. An older build cannot overwrite a newer build's definition.
- Schedules follow the last build that registered: one the new build dropped is orphaned, and comes back, without its pause, when a build that declares it registers again. Pause the job, not the schedule, to hold it across a rollback.
- Migrations are forward-only and additive, and an older build never rewrites what a newer one migrated.

Jobs of a definition only the withdrawn build carried stay `Ready` until a build with the handler returns or an operator retires the definition (`IActaOperations.Definitions.RetireAsync`, `POST /definitions/{namespace}/{name}/retire`, or the dashboard). Retiring cancels the definition's parked jobs with `job.definition-retired`, lets running attempts finish, and rejects new enqueues. Retire only after the old fleet is gone: an old worker that restarts reactivates the definition. Until then, a long run of such jobs slows every old worker's claim.

### Coexisting with your application's packages

Acta declares only minimum versions for its database drivers (`Npgsql`, `Microsoft.Data.SqlClient`,
`Microsoft.Data.Sqlite`) — floors, never upper bounds. Your application stays free to pin the driver
version it needs, including through Central Package Management; NuGet resolves one driver for the
process and Acta uses whatever it resolves.

An upper bound in package metadata would not actually protect you — NuGet demotes a version-range
conflict to a warning that most builds never read. So the check runs at startup instead: the
provider compares the major version of the driver actually loaded against the major Acta was
certified with, and on a mismatch logs one warning naming both. The host keeps running, because a
driver often moves when another library pulls in its next major; the warning tells an operator which
version is running uncertified, and the conformance suite is what certifies a new one.

### Worker count and executor count

Each `Run<TManifest>(...)` registers one worker namespace in a process. You can run several
replicas of the same namespace for horizontal capacity; every worker is a peer and claims work
competitively from SQL.

Keep a namespace at 16 workers or fewer. Its workers all claim from one head of the claim index, which
leads with the namespace, so past that each worker added makes claims queue on the head and
completions wait on claims: on PostgreSQL a single namespace drained fastest at 16 workers and slower
at 24 and 32. Size a namespace by its work (peak jobs per second times average handler seconds,
divided by executors per worker) plus a spare for a crash or a deploy, and when it needs more than 16
workers, split its work across a second namespace, which has a claim head of its own.

`MaxConcurrentExecutors` is per process. If you run 6 replicas with 16 executors each, the namespace
can have up to 96 handlers in flight before downstream throttles or job-level limits apply.

Tune together:

- `MaxConcurrentExecutors`: handler concurrency per process, and the most rows one claim takes.
- Database connection pool size.
- Downstream rate limits and idempotency guarantees.

Raise concurrency in steps and watch database CPU, lock waits, connection pool pressure, job
latency, failure rate, and downstream saturation.

### Database server settings

Acta runs on the server's defaults. Under sustained load these settings help, and Acta's bootstrap
leaves them to you because each one applies to the whole database or server.

PostgreSQL:

- Raise `max_wal_size` (for example to `8GB`) and `checkpoint_timeout` (to `15min`), and set
  `wal_compression = lz4`. At the 1 GB default a busy queue checkpoints every few seconds, and every
  checkpoint makes the next change to each page write a full page image to the WAL. All three reload
  without a restart.
- Keep transactions short across the whole database, not only Acta's. A claim starts past the dead
  index entries earlier claims left, so an open transaction no longer slows it, but vacuum cannot
  reclaim those rows until the oldest transaction that can still see them ends, and `runtimes` grows
  meanwhile.

SQL Server:

- Put the transaction log on the fastest storage you have. Every claim, every completion batch, and
  every enqueue commits, so a busy server waits on log flushes (`WRITELOG`) before anything else.
- Consider `ALTER DATABASE ... SET AUTO_UPDATE_STATISTICS_ASYNC ON`. Acta's tables grow quickly, and
  a synchronous statistics refresh makes the query that triggered it wait
  (`WAIT_ON_SYNC_STATISTICS_REFRESH`); the asynchronous refresh lets it run on the old statistics.

### Database clock and lease safety

Acta uses database time for durable scheduling, leases, retention, and event timestamps. Worker
startup checks host/database clock skew and fails loudly when skew is excessive unless
`AllowClockSkew = true`.

Keep clocks healthy rather than suppressing the check. A skewed host or database can make leases
look expired too early or too late.

The coordination triple is one setting. Keep `HeartbeatInterval` the same across every worker in a
namespace; `LeaseTtlSeconds` (x4) and `WorkerDeadAfter` (x7) derive from it and are read-only, so
replicas that agree on the heartbeat agree on all three by construction.

The default shape is a 45-second heartbeat, a 180-second lease, and a 315-second dead-worker
threshold. Long-running handlers stay alive through heartbeat extension, which is why no lease window
needs inflating to accommodate them.

### Payload size guidance

Inline payloads are for durable instructions and small results, not file storage. The default
`MaxInlinePayloadBytes` is 1 MiB. It is the hard cap for caller-controlled inline writes such as
enqueue inputs, variables, progress, step results, and signal values. Handler results are measured
against the same cap, but one past it is dropped rather than thrown, because the handler has already
run: the job still succeeds, a warning is logged, and its events carry `job.result-oversized`.

For large files, exports, media, archives, reports, ML inputs, or sensitive data, store the body in
file/blob/object storage and enqueue a small reference that includes enough information to verify
what the handler reads, such as URI, checksum, byte length, and content type.

Avoid putting secrets and high-volume PII in job inputs, results, tags, or alert text. Acta state is
operator-visible SQL state.

### Dashboard exposure and auth

The rules are in [HTTP API and dashboard](#http-api-and-dashboard). A typical remote setup, with controls for one role:

```csharp
builder.Services.AddAuthorization(options =>
    options.AddPolicy("ActaOperators", policy => policy.RequireAuthenticatedUser().RequireRole("ActaOperator")));

app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.EnableControls = true;
    options.ConfigureEndpoints = group => group.RequireAuthorization("ActaOperators");
});
```

Behind a reverse proxy on the same host, `LocalOnly` protects nothing: it checks the TCP peer, which is the proxy. Gate remote access with host authorization, and configure `ForwardedHeaders` if the host needs the client's IP. Keep secrets and personal data out of payloads, since every reader sees them.

### Alert channel setup

Every worker namespace has an implicit `default` log alert channel. Add real channels in worker
startup configuration:

```csharp
j.Run("billing", w =>
{
    w.AddManifest<BillingJobs>();
    w.OwnerTeam = "payments";
    w.AddAlertChannel(
        "ops-oncall",
        AlertTransportKinds.SlackWebhook,
        builder.Configuration["Alerts:SlackWebhookUrl"]!);
});
```

Route a job with `[Job(AlertProfile = ..., AlertChannelName = "...")]`. `AlertProfile` decides what
events project alerts; `AlertChannelName` selects the configured channel. Keep endpoints and secrets
in configuration or a secret store, not in Acta SQL.

Use `AlertChannelValidationMode.Fail` in environments where a missing channel should block worker
startup.

### Retention

Set retention long enough for audit, incident response, and late restarts; the rules are in [retention and purge](#retention-and-purge).

### Process behavior on failure

| Surface | Failure shows as | Process | Action |
| --- | --- | --- | --- |
| Worker startup | A validation failure (options, clock skew, schedule time zone, contract drift or alert routing in `Fail` mode) throws from `StartAsync`. | Does not start. | Fix the configuration and restart. |
| Claimer, executors, finalizer, heartbeat | An Error log under `Acta.WorkerRuntime` (`claim iteration failed`, `heartbeat tick failed`, `executor faulted on job`, `finalizer faulted`). | Backs off and keeps running; one bad job never stops a loop. | Investigate the logged cause, usually the database; no restart needed. |
| `sys.alerts`, `sys.retention` | A failed pass is an ordinary job failure, retried on the next tick; a transport fault logs a Warning and retries. | Keeps running. | Usually none. |
| CLI | Exit code 1 rejected, 2 not found, 64 usage, 130 cancelled. | Exits. | Read the code and stderr, then retry. |
| Dashboard and API | An Error log under `Acta.AspNetCore` and a 503 for a database or network fault, 500 otherwise, 400 for bad input. | Keeps serving. | A run of 503s means the database is down. |

### Backup and restore expectations

Acta state is application state. Back up Acta tables with the application database they coordinate
with, and restore them together when possible.

A database restore is a state rewind. Jobs that were terminal after the backup may become runnable
again after restore, while external side effects may already have happened. This is another reason
handlers need deduplication keys and reconciliation paths for irreversible side effects.

After restore, start workers normally and let recovery handle expired leases. Validate this in a
staging restore drill before relying on it operationally.

Avoid partial restores of only some Acta tables. Every table in the Acta schema, migration history
included, is one durable system.

## Alerting

`sys.alerts` runs every minute in each worker namespace. It turns failure events into alert rows, then delivers due alerts to channels, so an alert appears up to about two minutes after the failure. Falling behind during a burst is lag, not loss.

### Profiles

`[Job(AlertProfile = ...)]` decides what a job's failures raise. `OnFailure` is the default.

| Profile | Retryable failure | Terminal `Failed` | Success |
| --- | --- | --- | --- |
| `None` | nothing | nothing | nothing |
| `OnFailure` (default) | `FirstFailure` at `Warning`; `ThresholdReached` at `Error` on the Nth | `FinalFailure` at `Error` | resolves |
| `Info` | nothing | `FinalFailure` at `Info` | resolves |
| `OnTerminal` | nothing | `FinalFailure` at `Error` | resolves |
| `SysCritical` | as `OnFailure`, at `Critical` | `FinalFailure` at `Critical` | resolves |

- `SysCritical` is for Acta's own jobs. `None` turns off automatic alerts only; `ctx.AlertAsync` still raises manual ones.
- A `Cancelled` job never alerts, whether an operator, `ctx.CancelAsync`, or a deadline cancelled it. Budget-neutral re-arms (a held concurrency key, a rate limit, a step retry) do not alert either.
- At `AuditLevel = Failures` a one-shot job's retries are not recorded, so only its terminal `Failed` alerts. A recurring job's failed fire and a worker that died holding a job still do.
- For recurring work use `OnFailure`: a recurring job's failures never land `Failed`, so `OnTerminal` and `Info` stay silent for them.

### Incidents

An automatic alert is an incident: one open row per job, kind (`FirstFailure`, `ThresholdReached`, `FinalFailure`), and reason. A repeat folds into it and raises `occurrence_count`; the job's next success resolves it, and a later failure opens a new incident. `ThresholdReached` fires once, when the incident's count reaches `AlertFailureThreshold` (default 3).

A one-shot job raises at most three alerts, and its incidents end with it. A recurring job's incident stays open until a fire succeeds or an operator steps in, and is delivered again every `AlertReminderInterval` (default 24 hours): an alert still open is the sign that nobody fixed it.

### Channels and delivery

```csharp
w.AddAlertChannel("ops-slack", AlertTransportKinds.SlackWebhook,
    endpoint: builder.Configuration["Alerts:SlackWebhookUrl"]!,
    o => o.MinSeverity = AlertSeverityCode.Warning);
```

- Route a job with `[Job(AlertChannelName = "ops-slack")]`. Without one it goes to the namespace's implicit `default` channel, which logs, so failures reach the logs with no setup. `RunbookUrl` on the job travels with the notification.
- The built-in transports are `log` and `slack-webhook`; a custom one registers an `IAlertTransport`. Endpoints and credentials stay in configuration and are never stored in the database.
- A channel's `MinSeverity` and its `Disabled` or `Deprecated` status suppress delivery. A channel the namespace does not configure fails delivery, and `AlertChannelValidationMode` checks every definition's routing at startup.
- A failed send retries for a few minutes, about five attempts, and then settles `Failed`. Delivery is at least once. Resolving an alert before it is sent cancels the send.

### Operator surface

`IActaOperations.Alerts` lists, reads, acknowledges, and resolves alerts, as do the dashboard and `POST /alerts/{alertRef}/acknowledge|resolve`. Acknowledging does not stop reminders; resolving does. Neither changes the job. Alerts age out on `AlertRetention`, independently of their job. The concepts `402-alerts`, `403-alert-channel`, `405-real-alert-routing`, `406-automatic-failure-alerts`, and `411-alert-escalation` run each part.

## What happens if…

Failure-mode answers for operators and handler authors. Start a single-job investigation with
`<app> jobs explain <job-ref>` or the dashboard Explain panel; use this page to understand the
expected transition and the application responsibility behind it.

### …a worker crashes before starting the handler?

The claim already moved the job to `Executing` under the worker lease. After the lease expires,
the next recovery pass returns it to `Ready` while retry budget remains, or marks it `Failed` when the budget
is exhausted (a recurring slot always returns to `Ready`, for its next occurrence). No handler side
effect occurred, but the lost attempt counts against the budget like any other crashed attempt.

### …a worker crashes while the handler is running?

The lease eventually expires and recovery makes the job runnable again within budget. Bare handler
code may run again. Succeeded durable steps return their stored results; an in-progress step whose
outcome was not recorded follows its configured semantics.

External systems are not rolled back. Use deduplication keys, reconciliation, or carefully selected
`AtMostOnce()` steps when duplicate execution is worse than an ambiguous outcome.

### …the external call succeeds but Acta cannot record completion?

The same attempt keeps writing the completion, on a one-second-to-thirty-second curve, until it lands
or the worker stops; the row stays Executing under a lease the heartbeat renews, and the external call
is not repeated by that worker. A worker that dies mid-repeat hands the row to recovery for a new
attempt, and that attempt can repeat the call. So does a database outage longer than the lease (180 s
by default): the heartbeat cannot renew either, and when the database returns, recovery reclaims every
expired row, finished or not, and re-runs it under the failure budget, which on its last attempt can
land a job Failed whose work had succeeded. This is the central at-least-once boundary. Pass a
stable business deduplication key to the external system or reconcile its state before repeating the
side effect.

### …the database is unavailable?

Workers cannot claim, heartbeat, checkpoint, or complete durable work without SQL. A handler already
inside an external call may still affect that external system before its next Acta write fails.
After SQL returns, inspect expired leases and let recovery reconcile them; do not assume database
recovery also reversed external effects.

### …a signal arrives before the handler starts waiting?

The signal value is stored in the named checkpoint. When the handler later calls
`WaitSignalAsync` with the same name, it consumes the stored value without suspending. A signal sent
to a terminal job is rejected; a signal sent to a paused job is stored but does not resume the job
until the job itself is resumed.

### …a signal never arrives?

An unbounded `WaitSignalAsync` suspends the job indefinitely, and retention never touches a
suspended job — only terminal ones. When "never" is a real possibility, bound the wait:
`WaitSignalAsync(name, timeout)` cancels the job on expiry, budget-neutral, with reason
`job.wait-timed-out`, so the row lands terminal and ages out normally; `TryWaitSignalAsync`
resumes the handler with a `TimedOut` result instead, for workflows that expire or compensate
explicitly. The deadline is stamped on the slot when the wait first arms and is never extended —
worker downtime, restarts, and replays all count against it.

### …a child never finishes and the parent is waiting on it?

`TryWaitChildAsync(childJobId, timeout)` resumes the parent with `TimedOut` and cancels the
unfinished child and its descendant subtree; the parent stays active and decides what happens
next. A group is the same story under one clock: `TryWaitChildrenAsync` (and the bounded
`Join`/`Parallel`/`Map` overloads) spends a single persisted deadline across all members —
completed children keep their outcomes, only the unfinished ones are cancelled, and the
at-least-once shape below covers each of those cancellations the same way. Two limits remain. The subtree cancellation is initiated by the parent's resumed attempt, so
it is at-least-once like everything else: a crash between the durable expiry and the cancellation
re-runs it on replay, and a parent that goes terminal without ever replaying leaves its children
running — which is what parent death has always meant in Acta. And an expired wait slot is
terminal for its name: a replay that reaches an unbounded `WaitChildAsync` over an already-expired
latch cancels the job rather than parking it forever on a wait that can no longer complete.

### …a sleeping or signal-waiting job has no worker?

The wait occupies no executor. A timer remains durable until due; a signal wait remains durable
until raised. Once the job becomes `Ready`, a live worker registered for the namespace and definition
must claim it. Explain and the Workers page show whether that capacity exists.

### …the application deploy removes a handler that queued jobs still need?

Stored rows keep their durable job name. They are not rewritten to another handler. Keep the old
definition registered until runnable/waiting rows drain or expire, or deploy a compatible/versioned
migration plan. Search retained failed jobs too: an operator restart can make them runnable again.

A worker that claims such a job hands it straight back. Claims are selected by namespace, not by
what a process can run, so during a rolling deploy either version can claim either version's jobs.
A worker with no handler for a claimed job returns it to `Ready` with the lease cleared, no failure
charged and no retry consumed, due again one safety-poll interval later, and stops claiming that
definition for the rest of its process life; the first hand-back logs a warning naming the job ref
and definition id, the rest of the same claimed batch log at debug. Under audit level `Audit` the
hand-back writes one `job.execution-finished` row with reason `job.unclassified` whose message names
the definition; under `Failures` it writes nothing, because nothing failed. A job whose definition
no live worker carries sits `Ready` without progressing, and its definition page shows it `Active`
with queued work and no worker that carries it; the fix is to redeploy a build that carries the
handler, or to retire the definition from the dashboard, which cancels its parked jobs. Removing a
handler from a deploy never cancels anything by itself; see
[Production § rolling deploys](#rolling-deploys).

A recurring job follows the same rule. A deploy that drops every schedule of one pauses its slot with
no next run, and a deploy that brings a schedule back makes it `Ready`. A deploy that adds a
`[JobSchedule]` refuses to start, naming the recurring job, while an ordinary job of the namespace
holds the slot's deduplication key (the job name): let that job finish and purge it, or enqueue its
work under another key. See [Schedule operations](./guide.md#schedule-operations).

### …a checkpoint name changes during a deployment?

The new name is a different durable slot. A step can repeat, a variable can appear absent, or a
signal can wait on a name that old producers never raise. Follow
[Durable slot evolution](./guide.md#durable-slot-evolution) and treat the change as a data
migration, not a refactor.

### …the service is down across scheduled occurrences?

The schedule's misfire policy decides the cursor when the schedule reloads or resumes. `Skip` moves
past missed occurrences; `CatchUpOnce` produces one coalesced catch-up execution. Neither policy
creates one historical job per missed period. A worker start counts an occurrence less than a minute
overdue as due rather than missed, and leaves a slot with an attempt in flight, or parked part-way
through an occurrence, to the running fleet. See [Schedule operations](./guide.md#schedule-operations).

### …an operator restarts a failed or cancelled job?

Restart re-arms the same job and keeps its input and event history. Succeeded durable steps still do
not rerun unless state was explicitly reset, but bare handler code and incomplete steps can run
again, and an exhausted step runs again with a fresh budget. An interrupted at-most-once step still
throws until the handler reconciles it. Fix or understand the original cause before confirming
restart.

### …an operator purges a job?

Purge permanently removes the terminal job, result, checkpoints, steps, tags, alerts, and prior
events, then emits a standalone `job.purged` audit event. It rejects non-terminal jobs, parents
with child jobs, and children whose parent is not terminal; finish or cancel the tree, then purge
from the leaves up. See [Retention and purge](#retention-and-purge).

### …the dashboard backend goes offline?

The dashboard shows its offline banner and cached rows may remain visible, but controls and fresh
reads cannot complete. Treat SQL/API availability as unknown until the backend recovers; do not infer
that a clicked action applied unless its response said `applied` and the refreshed timeline confirms
the event.

### …an alert cannot be delivered?

The alert row remains queryable independently of transport delivery. Inspect `delivery_status`, the
configured channel registry, and worker logs. Acknowledge means an operator has seen the alert;
resolve means the incident is considered settled. Neither action changes the underlying job.

### …the database is restored from backup?

Acta state rewinds to the backup point; external systems do not. Work that completed after the backup
can appear runnable again, and signals or operator actions after the backup can disappear. Run a
reconciliation before releasing workers, using stable external deduplication keys and the restored
timeline as evidence, not as proof that external effects did or did not happen.

## Troubleshooting

Use this section when a job, worker, or the dashboard is not behaving as expected. The sections above
have fuller SQL examples; this one points to the first checks.

For consequence-first answers: worker crash, database outage, early signal, removed handler,
schedule downtime, purge, or restore: see [What happens if…](#what-happens-if).

### Start With `jobs explain`

For any single stuck, waiting, or failed job, run `<app> jobs explain <job-ref|id>` first. It reads
the same `runtimes` / lease / worker / checkpoint / step / event rows the checks below describe, in
one consistent snapshot, and states in plain English where the work is, why it moved there, and the
next action (raise a signal, wait for crash recovery, resume, restart, or inspect the timeline). The
manual checks below are for triaging many jobs at once, confirming what `explain` reports, or when
you only have SQL access. The dashboard's per-job Explain panel renders the same account.

`jobs explain` diagnoses Acta's durable state. It can tell you which durable slots completed, which
wait is active, what the latest recorded reason was, and what operator actions are available. It
cannot predict arbitrary user code, external system state, or whether a non-idempotent side effect
already happened outside Acta.

Sample outputs for a suspended, an expired-lease, a failed, and a succeeded job are in
[Operator guide § built-in control CLI](#built-in-control-cli).

### A Job Did Not Start

Start with `acta.jobs_view` so names and status values are decoded:

- `status`: only `ready` jobs are claimable. A `blocked` job waits behind the earlier unfinished job in
  its `lane` and goes `ready` when that one settles.
- `next_run_at_utc`: delayed, slept, rescheduled, and recurring jobs may be ready but not due yet.
- `namespace`: workers only claim inside their own namespace.
- `job_name`: the worker must have registered the matching definition in that namespace.

If you need raw storage state, `jobs_view` is built from the job's `runtimes` row (1:1 with `jobs` by `job_id`).

Then check workers:

- `acta.workers_view.status`
- `acta.workers_view.last_seen_at_utc`
- the worker process logs
- whether the process used `Run<TManifest>(...)` instead of only `Reference<TManifest>(...)`

For query shapes, see [Common queries](#common-queries).

### A Job Is Stuck In Executing

`executing` is a lease-owned state. Check `acta.jobs_view.leased_by_worker_id`
and `lease_expires_at_utc`, then check `acta.workers_view.last_seen_at_utc`.

If the worker is gone, crash recovery reclaims expired leases on its next pass and moves
the job back to `Ready` while retry budget remains. If the worker is alive, inspect handler logs and
the job's event timeline.

For the live crash/reclaim path, run [`../anvil/Anvil/`](../anvil/Anvil/).

### A Job Is Suspended

`Suspended` usually means the handler is waiting on `ctx.WaitSignalAsync(...)`. Check
pending signal `checkpoints` slots (kind `signal`) and raise the expected signal through `IJobs.RaiseSignalAsync`, the
CLI, or the HTTP signal endpoint if controls are enabled.

Sleep and reschedule waits are timer-based. Check timer `checkpoints` (kind `timer`) and `next_run_at_utc` for the due
instant.

A bounded wait tells you when it gives up: `Explain` prints "times out at" with the deadline, the
slot's `due_at_utc` carries it, and the suspended row's `next_run_at_utc` is non-null — the job
will wake itself. A suspended row with `next_run_at_utc` NULL is an unbounded wait and wakes only
on a raise. To put a bound on a job stranded under code that has since gained one, reschedule it
once: the re-entered wait arms the new deadline on a slot that never had one.

### A Job Is Paused

Pause is sticky. A paused job does not auto-resume unless the paused object is a schedule pause with
an `until` timestamp. Resume a paused job with `IJobs.ResumeAsync`, the CLI, or an enabled dashboard
control endpoint.

Schedules have their own control surface through `operations.Schedules`; pausing a schedule is distinct
from pausing the job itself.

A recurring job whose schedules are all paused, exhausted, or removed by a deploy is Paused with no next
run. A job resume answers 409 until a schedule offers a run, and the job goes back to Ready by itself
once one does. See [Schedule operations](./guide.md#schedule-operations).

### A Job Keeps Retrying Or Failed

Read the event timeline. Job status tells you where the row landed; `events` explains why it
moved. For step-specific failures, inspect `steps` as well.

Common causes:

- the handler threw and retry budget remains
- `MaxAttempts` was exhausted
- `ExecutionTimeout` cancelled the attempt
- a handler called `ctx.FailAsync(...)`
- a worker lost its lease and maintenance reclaimed the attempt
- an external cancel cascaded from a parent

The code tables in [`code-families.md`](./reference/code-families.md) define status, event, reason, and
execution outcome codes.

### A Signal Did Not Release A Job

Check the signal name and target identity:

- Signal names are kebab-case and scoped to one job.
- A terminal job rejects new signals.
- A paused job records the signal but stays paused.
- A raise that arrives after a bounded wait timed out releases nothing; its event reads "Signal not
  applied: the wait had already expired."
- HTTP signal endpoints require controls to be enabled and the same `X-Acta-Control` confirmation
  header as other control requests. The header is the cross-site request forgery barrier, not authentication.

The handler resumes when it is claimed again, so also check worker liveness and `next_run_at_utc`.

### An Outbox Row Is Not Reaching Acta

If a producer staged an `acta_outbox` row and no Acta job appeared, check the relay before the row:

- A worker must register the source with `worker.AddOutboxRelay(...)`; without it, `sys.outbox` never
  runs and no row is claimed.
- `sys.outbox` runs on a five-second cadence and sends no cross-database wakeup, so a few seconds of delay
  is normal. After a worker crash a claimed row can stay invisible until its lease (`LeaseTtlSeconds`,
  180 s default) expires and another relay reclaims it.
- Connectivity and table-shape problems fail inside `sys.outbox` and raise a `SysCritical` alert rather
  than blocking unrelated jobs. Check `acta.alerts_view` and the `sys.outbox` event timeline.
- A row the relay could not deliver is quarantined in place (`status_code = 90`) and excluded from claims.
  Requeue or discard it through `IActaOperations.Outbox`, `POST /outbox/{jobNamespace}/requeue` or
  `/discard`, or the dashboard's outbox panel; the
  [quarantine SQL recipes](#quarantined-outbox-rows) are the unaudited fallback. Recoverable rejections (for example
  an unknown route) quarantine after the failure threshold; malformed or oversize rows quarantine at once.
- Resolve the resulting job by `(namespace, deduplication key)` with `IJobs.GetJobIdAsync`; a null
  result means the request has not been relayed yet, not that it was lost.

Background: [Transactional enqueue and the external outbox](./guide.md#transactional-enqueue-and-the-external-outbox).

### An Enqueue Throws About An Ambient TransactionScope

An Acta-owned call (a normal enqueue, a maintenance write, any owned path) opening a connection inside an
active `System.Transactions.TransactionScope` fails fast:

```text
An ambient System.Transactions.TransactionScope is active, and Acta-owned connections never enlist in one.
```

Acta-owned connections never enlist in an ambient transaction, because a second connection in the scope
would force distributed-transaction escalation the providers cannot honor. Rewrite to one of the two atomic
paths: pass the open transaction to the transactional `IJobs` enqueue overload (same database), or stage
through `AddToActaOutboxAsync` on your provider transaction (different database). If you deliberately want
an independent Acta commit inside a scope, wrap the call in `TransactionScope(TransactionScopeOption.Suppress)`.
The caller-transaction overloads and the staging primitives are unaffected: their transaction is supplied
explicitly. See [Transactional enqueue and the external outbox](./guide.md#the-external-outbox).

### The Dashboard Or API Is Not Reachable Remotely

`MapActa(...)` and `MapActaApi(...)` are local-only by default. Remote clients receive 403 until the
host sets `LocalOnly = false` and adds ASP.NET Core authorization through `ConfigureEndpoints`. A
browser on the same machine also gets 403 when it names the machine rather than `localhost` or an IP
address: local-only accepts only those host names.

Mutating controls are also disabled by default. Enable them explicitly with `EnableControls = true`
and keep authorization on the mapped endpoint group.

### The CLI Did Not Run A Worker

When an Acta host starts with `jobs` as the first argument, it runs the built-in CLI verb and exits.
No worker loop starts. This is intentional.

Use `j.DisableCli()` only for applications that own their own command-line surface. The CLI expects
an already-configured provider and schema; the normal worker owns migration and catalog setup.

### The Generated Manifest Type Does Not Resolve

`CS0246: The type or namespace name 'ShippingJobs' could not be found` on the `j.Run<...>` line
means the generated manifest exists but is not in scope. The generator names it from the last
segment of the project's `RootNamespace` plus `Jobs` (`Shipping` gives `ShippingJobs`,
`Acme.Shipping.Web` gives `WebJobs`) and emits it into that root namespace, while top-level
statements live in the global namespace.

Add `using <RootNamespace>;` at the top of `Program.cs`, or fully qualify the type. If the name
still surprises you, check the `RootNamespace` property in the project file; a project whose
namespace already ends in `Jobs` gets `Manifest` appended instead, so `Acme.Jobs` gives
`JobsManifest`. A build with no `[Job]` methods at all generates no manifest, and the same
error then means the attribute is missing, not the `using`.

## SQL recipes

These recipes are Postgres-flavored against the default `acta` schema; on SQL Server, bracket
identifiers as needed and replace interval arithmetic with `DATEADD`. The first recipe enqueues a job
(a write, through the installed routine) and the outbox recipes repair the producer's table; the rest
are operator/debugging reads.

For reads, start with the curated operator views: `acta.jobs_view`, `acta.events_view`,
`acta.checkpoints_view`, `acta.steps_view`, `acta.schedules_view`, `acta.alerts_view`,
`acta.workers_view`, `acta.definitions_view`, and `acta.tags_view`. Views expose name-only decodes for common job
codes (`status = 'failed'`) and keep the raw `*_code` columns beside them. They are stable enough for
operator use, but they are not the storage contract. Runtime code does not depend on them, and
migrations do not derive from them.

[`data-model.md`](./reference/data-model.md) documents every storage table, and
[`code-families.md`](./reference/code-families.md) remains the full decoder ring for descriptions and
less common codes.

### Foreign-key policy

Acta enforces DB foreign keys in exactly two shapes:

- CASCADE from `jobs` to its transactional substrate children (runtimes, checkpoints, results,
  steps, schedules): these rows' physical lifetime is owned by the job row. Tags carry no foreign
  key; purge and retention delete a job's tags with it.
- RESTRICT (no action) from transactional tables to dimension rows (schedules → definitions;
  workers → namespaces; runtimes → lanes): definitions and namespaces have no delete surface, and
  retention deletes a lane only once no job references it. The `jobs` dimension FKs (jobs → namespaces / tenants /
  definitions) were cut by the 2026-07-11 bench gate after they regressed enqueue-batch, so those
  references are write-time validated and conformance-tested instead.

Never enforced, by design:

- Audit/history rows (`events`, `alerts`): audit outlives its subjects. Queries against them must
  use their denormalized columns (e.g. `alerts.job_ref`), never joins to possibly-purged rows.
- Ephemeral/operational references (`locks.job_id`, `runtimes.leased_by_worker_id`): reaped by
  their own lifecycles.
- Lineage self-references (`jobs.parent_id`, `jobs.lineage_root_id`): retention deletes parents in
  retention order and dangling lineage refs are tolerated everywhere; a RESTRICT self-FK would fail
  routine retention.

Every unenforced reference must be write-time validated by its operations and covered by
conformance tests. New hot-path tables must justify any FK in review.

### Enqueue A Job

Creating work from SQL is a write, so call the installed `enqueue_job` routine instead of hand-writing
`INSERT`s. You pass the **namespace name and the job name** (the `[Job("...")]` name); the routine
resolves the definition, stamps the job ready, and writes the `jobs` and `runtimes` rows in one
transaction.

**Postgres**: a set-returning function; call it with named arguments:

```sql
SELECT job_id, job_ref, action  /* action: 1 inserted, 2 idempotency-deduped */
  FROM acta.enqueue_job(
    p_namespace_name => 'billing',
    p_job_name       => 'send-invoice',
    p_input          => convert_to('{"invoiceId":42}', 'UTF8')
  );
```

**SQL Server**, a stored procedure; tags arrive through a table-valued parameter that has no default,
so declare an empty one even for a tag-free enqueue:

```sql
DECLARE @tags acta.enqueue_tags_batch;
DECLARE @payload varbinary(max) = CAST('{"invoiceId":42}' AS varbinary(max));
EXEC acta.enqueue_job
    @p_namespace_name = 'billing',
    @p_job_name       = 'send-invoice',
    @p_input          = @payload,
    @p_tag_batch      = @tags;
```

SQLite has no callable enqueue routine; enqueue through the application (`IJobs.EnqueueAsync`).

### Recent Jobs

```sql
SELECT job_id, job_ref, namespace, job_name, status, failure_count, next_run_at_utc, input_format, input_text, last_result_format, last_result_text, created_at_utc
  FROM acta.jobs_view
 WHERE namespace = 'billing'
 ORDER BY created_at_utc DESC
 LIMIT 50;
```

`input_text` and `last_result_text` decode only built-in `json` and `text` payloads. Binary and custom
formats stay opaque: the format columns tell you why the text column is `NULL`; inspect those payloads
through application code.

### Backlog By Definition

```sql
SELECT namespace, job_name, COUNT(*) AS ready_count, MIN(next_run_at_utc) AS oldest_due_at_utc
  FROM acta.jobs_view
 WHERE status = 'ready'
 GROUP BY namespace, job_name
 ORDER BY ready_count DESC, oldest_due_at_utc;
```

Use this when work is not draining. If backlog exists but workers are alive, compare executor count,
claim batch, DB capacity, and whether jobs are due yet.

### In-Flight Jobs And Owners

```sql
SELECT job_id, job_ref, namespace, job_name, status, leased_by_worker_id, leased_by_worker_host, lease_expires_at_utc
  FROM acta.jobs_view
 WHERE status = 'executing'
 ORDER BY lease_expires_at_utc, job_id;
```

If the worker is gone and the lease has expired, crash recovery reclaims the job on its next pass,
within one more lease.

### Event Timeline

```sql
SELECT event_id, created_at_utc, job_id, job_ref, event, actor_kind, actor, from_status, to_status, execution_status, reason, reason_message, detail_format, detail_text, duration_ms
  FROM acta.events_view
 WHERE job_id = :job_id
 ORDER BY created_at_utc, event_id;
```

`events_view` is a readable ledger, not a rollup. The `jobs_view` row tells you where work is now;
events tell you why it moved. `detail_text` decodes built-in `json` and `text` event details; binary
and custom detail payloads stay opaque.

### Recent Terminal Failures

```sql
SELECT job_id, job_ref, namespace, job_name, failure_count, modified_at_utc
  FROM acta.jobs_view
 WHERE status = 'failed'
   AND modified_at_utc > now() - interval '24 hours'
 ORDER BY modified_at_utc DESC;
```

Follow with the event timeline for the specific `job_id`.

### Suspended Jobs Waiting On Signals

```sql
SELECT c.job_id, c.job_ref, c.namespace, c.job_name, c.checkpoint_name AS signal_name, c.state, c.value_format, c.value_text, c.modified_at_utc
  FROM acta.checkpoints_view c
  JOIN acta.jobs_view j ON j.job_id = c.job_id
 WHERE j.status = 'suspended'
   AND c.kind = 'signal'
 ORDER BY c.modified_at_utc DESC;
```

Raise the matching signal with `IJobs.RaiseSignalAsync`, the CLI, or the HTTP signal endpoint.
`value_text` decodes built-in `json` and `text` signal values; presence-only, binary, and custom
values stay opaque.

### Durable Sleeps And Timers

```sql
SELECT job_id, job_ref, namespace, job_name, checkpoint_name AS timer_name, state, due_at_utc
  FROM acta.checkpoints_view
 WHERE kind = 'timer'
 ORDER BY due_at_utc, job_id;
```

Timers are job-internal slots. The owning job becomes claimable when the timer is due and consumed.

### Step State

```sql
SELECT job_id, job_ref, namespace, job_name, step_name, state, attempt_number, next_retry_at_utc, reason, reason_message, result_format, result_text
  FROM acta.steps_view
 WHERE job_id = :job_id
 ORDER BY step_name;
```

A succeeded step replays from stored result. A failed step may re-arm independently of the parent
job's attempt budget until its own step budget is exhausted.

### Recurring Schedules

```sql
SELECT namespace, job_name, schedule_name, status, origin, expression_effective, time_zone_id_effective, next_run_at_utc, paused_until_utc, description
  FROM acta.schedules_view
 ORDER BY namespace, next_run_at_utc NULLS LAST, schedule_name;
```

The schedule row carries the per-schedule cursor. The recurring slot job uses the minimum live
schedule cursor as its `next_run_at_utc`.

### Job Definitions And Effective Policy

```sql
SELECT namespace, job_name, status, input_type_name, input_format_name, output_type_name, output_format_name, priority, max_attempts, execution_timeout_seconds, deadline_seconds, retention_seconds, audit_level, alert_profile, alert_channel_name, runbook_url
  FROM acta.definitions_view
 WHERE namespace = 'billing'
 ORDER BY job_name;
```

Use this when a job behaves differently than expected after a deploy or operator override.

### Worker Liveness

```sql
SELECT namespace, worker_id, host, status, deployment_version, max_concurrency, last_seen_at_utc
  FROM acta.workers_view
 ORDER BY last_seen_at_utc DESC;
```

Crash recovery marks Active and Draining workers Dead when `last_seen_at_utc` falls past
`JobsOptions.WorkerDeadAfter`. A Dead worker that heartbeats again, after a database outage for
example, turns Active again.

### Pending Or Failed Alert Delivery

```sql
SELECT alert_id, namespace, job_id, job_ref, severity, kind, delivery_status, title, occurrence_count, retry_count, retry_after_utc
  FROM acta.alerts_view
 WHERE delivery_status IN ('pending', 'retry-after', 'failed')
 ORDER BY created_at_utc DESC;
```

Alert rows are projections of the event stream or manual handler alerts. Delivery retries are driven
by `sys.alerts`.

### Retention Candidates

```sql
SELECT namespace, status, COUNT(*) AS jobs_to_purge, MIN(retention_until_utc) AS oldest_retention_until_utc
  FROM acta.jobs_view
 WHERE retention_until_utc IS NOT NULL
   AND retention_until_utc <= now()
 GROUP BY namespace, status
 ORDER BY jobs_to_purge DESC;
```

`sys.retention` deletes expired terminal jobs and cascading substrate rows. Three kinds stay and still
count here: a stopped recurring job whose definition is live (until the definition is retired), a job
that still has children (the tree drains from the leaves), and a completed child whose parent is not
terminal. Event and alert
retention have their own windows in `JobsOptions`.

### Tags

`tags_view` decodes the exact target scope while retaining `scope_code` and `scope_id`. Join job-scoped
tags to `jobs_view` when you need runtime context:

```sql
SELECT t.scope_id AS job_id, j.job_ref, j.namespace, j.job_name, j.status, t.tag_name, t.tag_value
  FROM acta.tags_view t
  JOIN acta.jobs_view j ON j.job_id = t.scope_id
 WHERE t.scope = 'job'
   AND j.namespace = 'billing'
   AND t.tag_name = 'tier'
   AND t.tag_value = 'gold';
```

### Quarantined Outbox Rows

These recipes run against the **producer** database's external-outbox table, not the `acta` ledger
schema. The default physical name is `acta_outbox` in the provider's default schema; substitute your
configured table/schema. Status codes are `10` Pending, `20` Claimed, `90` Quarantined. Only requeue or
delete rows the relay has quarantined (`status_code = 90`); leave Pending and Claimed rows to the relay.
Prefer the audited verbs, `IActaOperations.Outbox.RequeueAsync` and `DiscardAsync` or
`POST /outbox/{jobNamespace}/requeue` and `/discard`, which write `outbox.requeued` and
`outbox.discarded` events; these statements leave no audit trail.
Background: [transactional enqueue and the external outbox](./guide.md#relay-behaviour).

Inspect quarantined rows:

```sql
SELECT outbox_id, job_namespace, job_name, deduplication_key, failure_count, last_error, created_at_utc
  FROM acta_outbox
 WHERE status_code = 90
 ORDER BY created_at_utc;
```

Requeue a quarantined row: reset it to Pending, clear the failure budget, make it immediately eligible on
the database clock, and clear the claim and error fields. Requeue only after fixing the underlying cause
(for example registering the missing job definition).

**PostgreSQL**:

```sql
UPDATE acta_outbox
   SET status_code = 10, failure_count = 0, next_attempt_at_utc = now(),
       claim_token = NULL, claim_until_utc = NULL, last_error = NULL
 WHERE status_code = 90
   AND outbox_id = :outbox_id;
```

**SQL Server**:

```sql
UPDATE acta_outbox
   SET status_code = 10, failure_count = 0, next_attempt_at_utc = SYSUTCDATETIME(),
       claim_token = NULL, claim_until_utc = NULL, last_error = NULL
 WHERE status_code = 90
   AND outbox_id = @outbox_id;
```

**SQLite** (its clock is already UTC):

```sql
UPDATE acta_outbox
   SET status_code = 10, failure_count = 0, next_attempt_at_utc = datetime('now'),
       claim_token = NULL, claim_until_utc = NULL, last_error = NULL
 WHERE status_code = 90
   AND outbox_id = :outbox_id;
```

Delete a quarantined row that should never be delivered (the payload is wrong and there is no fix):

```sql
DELETE FROM acta_outbox
 WHERE status_code = 90
   AND outbox_id = :outbox_id;
```

The `status_code = 90` guard keeps a requeue or delete from racing a row the relay currently owns: a
Claimed row is finalized by token CAS, so a stale operator write against it would not match anyway, but
scoping to Quarantined makes the intent explicit and the statement idempotent.

### Identifier Casing In Raw SQL

Names (namespace, job, schedule, signal, channel, lane, tenant, rate meter) are stored lowercase: Acta
trims and lowercases them on input, so a name compares equal under any collation. Keys (deduplication,
concurrency, lock, correlation) are stored exactly as the caller gave them and compared bytewise; on
SQL Server the compared key columns carry a binary collation for that, on PostgreSQL and SQLite equality
is already exact. Display text (`display_name`, `message`, `actor`) is stored verbatim and never compared.

For raw SQL this means: compare a name with a lowercase literal, and supply lowercase when you insert a
name out of band, since a mixed-case name never matches Acta's equality, uniqueness, or joins; compare a
key with the exact casing the caller supplied.

```sql
-- finds the tenant regardless of how the caller originally cased the name
SELECT id, tenant_name, description FROM acta.tenants WHERE tenant_name = 'acme-corp';
```
