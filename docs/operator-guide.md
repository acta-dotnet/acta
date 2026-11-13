# Operator guide

Running Acta: day-2 operations first, then the production guide, alerting, failure modes,
troubleshooting, and SQL recipes. Writing jobs is the [developer guide](./developer-guide.md).

Day-2 operations. The operator surface is the `IJobs` facade for per-job controls and
read-only list APIs (optionally surfaced by the embedded dashboard package), and plain SQL against
the ledger for everything else diagnostic. Queries below are
Postgres-flavored against the `acta` schema; on SQL Server bracket the identifiers and swap the
interval arithmetic (`DATEADD`), and on SQLite drop the `acta.` prefix, since the views live in the
database file itself. A SQLite view shows its `_utc` columns as ISO 8601 UTC text (`2026-11-08T09:30:00.123Z`),
which sorts in time order and compares against `strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '-1 hour')`; the ledger
tables keep Unix epoch milliseconds. A stored instant holds microseconds on PostgreSQL and milliseconds on SQL
Server and SQLite.

The job's `jobs_state` row stores current state. Job events explain transitions. Use the CLI, API,
dashboard, or a SQL query over the events timeline to find why.

Curated operator views cover the common read paths: `acta.jobs_view`, `acta.events_view`,
`acta.checkpoints_view`, `acta.steps_view`, `acta.schedules_view`, `acta.alerts_view`,
`acta.workers_view`, `acta.definitions_view`, `acta.calendars_view`, and `acta.tags_view`. The views
are the supported read surface. From 1.0 a view column is renamed or removed only in a major release,
and any release may add one, so name the columns you select. Storage tables are not a compatibility
contract. Runtime code does not depend on the views; they ship inside the migrations like a routine.

Reference docs: [`data-model.md`](./reference/data-model.md) (every table and column),
[`code-families.md`](./reference/code-families.md) (every status / reason / event code),
[`conformance-contracts.md`](./reference/conformance-contracts.md) (the tested guarantees).

## Status quick reference

Live statuses: `Ready = 10`, `Blocked = 15` (a laned job waiting behind its lane's head), `Suspended = 20`,
`Paused = 30`, `Idle = 40` (a permanent job between firings), `Executing = 50`.
Terminal statuses follow the band rule: `100` is success (`Succeeded`), `200+` is unsuccessful
(`Failed = 200`, `Cancelled = 220`). The reason for a transition lives on `events`, not on the
`jobs_state` row.

Workers use the same bands: `Active = 10`, `Draining = 80` (stopped polling, finishing in-flight),
`Stopped = 100`, `Dead = 200`.

## How a worker ended

There are exactly two ways a worker process ends, and the terminal status names which one:

| Status | What happened | Event |
|---|---|---|
| `Stopped = 100` | Exited cleanly through SIGTERM / `IHostedService.StopAsync` | `worker.stopped` (121) |
| `Dead = 200` | Heartbeat went stale past the liveness window; crash recovery flipped it | `worker.died` (122) |

A clean stop drains. The worker stops claiming, is stamped `Draining`, and lets its running handlers
finish; when the last one lands it is stamped `Stopped`. The host bounds that wait with
`HostOptions.ShutdownTimeout`. A handler still running when it passes has its token cancelled and the
process exits without a completion for it, so the job keeps its lease until the lease lapses, and crash
recovery then reclaims it and charges the attempt (`lease-expired`). Give a host whose handlers run
longer than the shutdown timeout a longer one, and have such handlers honour their token.

On a terminal worker, `modified_at_utc` is the instant it ended: both transitions stamp it. Crash
recovery stamps it again when it reclaims a job the worker still leased, because that writes an event
naming the worker. A worker
killed outright never reaches the clean path, so it stays `Active`, or `Draining` if the kill landed
mid-drain, until recovery marks it `Dead` - which is why a kill and a crash are indistinguishable
here, and correctly so. A worker cut off by a database outage longer than `WorkerDeadAfter` is
marked `Dead` too, and its first heartbeat after the outage makes it `Active` again and records
`worker.started`, so every `worker.died` of a worker that lived on has a start after it.

```sql
-- Workers that died in the last hour, and how long they had been running.
SELECT worker_id, host_name, deployment_version, created_at_utc, modified_at_utc
  FROM acta.workers_view
 WHERE worker_status_code = 200 AND modified_at_utc > now() - interval '1 hour'
 ORDER BY modified_at_utc DESC;
```

The same rows are readable through `IActaOperations.Workers.ListAsync`, filtered with
`new ListWorkersQuery(Status: WorkerStatusCode.Dead)`.

## Common queries

Read freely: the views exist for it. Repair through the control verbs, not through UPDATE. The
schema's own checks guard row *shape* (lease all-or-nothing, paired payload formats, non-negative
counters), but they cannot guard *history*: the runtime fences every mutation
on version counters and lease ownership, so a hand-edited `version`, `execution_number`, or lease
column produces a row the engine either refuses to touch or mistakes for another worker's, with no
constraint to stop you. Every control verb (restart, cancel, reschedule, purge) makes its change
under those fences and records it as an event at every recording level; a manual UPDATE does
neither.

Lanes make a hand edit worse. Setting a laned job back to Ready, or a Blocked job to Ready, puts two
jobs of one lane in flight, and no repair can take that back. Setting a lane's head to a terminal
status by hand releases nobody: the recovery sweep finds such a stranded lane and releases its next
job, with a `job.resumed` event whose details carry the reason `lane-repaired`. Each pass visits a bounded share of the namespace's lanes, so a
large namespace can take several passes. Restart a laned job with the restart verb, which reactivates it
under the lane lock and runs it right after the current head.

In SQL a ref is the UUID after `job_`: `job_01a14f4e954e7bf68ac609f3fcad8e3d` is `01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d`, and that lowercase dashed form matches on every provider.

- PostgreSQL stores a `uuid` and also parses the digits without dashes: `WHERE job_ref = '01a14f4e954e7bf68ac609f3fcad8e3d'`.
- SQL Server stores a `uniqueidentifier`, shows it in uppercase, and fails to convert a literal without dashes: `WHERE job_ref = '01A14F4E-954E-7BF6-8AC6-09F3FCAD8E3D'`.
- SQLite stores the lowercase dashed text and compares it exactly, so any other spelling matches nothing: `WHERE job_ref = '01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d'`.

```sql
-- One job, by public ref, written as above.
SELECT * FROM acta.jobs_view WHERE job_ref = :job_ref;

-- One job, by internal id (engine identity; logs and SQL joins use it).
SELECT * FROM acta.jobs_view WHERE job_id = :job_id;

-- By deduplication key (root jobs; children are keyed per parent instead).
SELECT * FROM acta.jobs_view
 WHERE namespace_name = 'billing' AND deduplication_key = :key AND parent_job_id IS NULL;

-- Failures in the last 24 hours, newest first.
SELECT job_id, job_ref, namespace_name, definition_name, failure_count, modified_at_utc
  FROM acta.jobs_view
 WHERE job_status = 'failed' AND modified_at_utc > now() - interval '24 hours'
 ORDER BY modified_at_utc DESC;

-- Queue depth: claimable backlog per namespace.
SELECT namespace_name, COUNT(*) AS ready
  FROM acta.jobs_view
 WHERE job_status = 'ready'
 GROUP BY namespace_name;

-- One tenant's work, by the tenant name the caller enqueued with (names fold lowercase).
SELECT job_id, job_ref, namespace_name, definition_name, job_status
  FROM acta.jobs_view
 WHERE tenant_name = :tenant_name;

-- Backlog per tenant, busiest first. Untenanted work groups under NULL.
SELECT tenant_name, COUNT(*) AS ready
  FROM acta.jobs_view
 WHERE job_status = 'ready'
 GROUP BY tenant_name
 ORDER BY ready DESC;

-- In-flight work and who holds it.
SELECT job_id, job_status, leased_by_worker_id, leased_by_worker_host_name, lease_expires_at_utc
  FROM acta.jobs_view
 WHERE job_status = 'executing';

-- The whole timeline for a job tree (parents + children share lineage_root_job_id).
SELECT created_at_utc, entity_id, event, execution_number, duration_ms, event_message, actor, details_text
  FROM acta.events_view
 WHERE lineage_root_job_id = :root_job_id
 ORDER BY created_at_utc, event_id;

-- Children of a parent.
SELECT job_id, job_ref, deduplication_key, job_status
  FROM acta.jobs_view
 WHERE parent_job_id = :job_id;

-- Suspended jobs and the signals they wait on.
SELECT c.job_id, c.job_ref, c.checkpoint_name, c.checkpoint_status, c.value_format, c.value_text
  FROM acta.checkpoints_view c
  JOIN acta.jobs_view j ON j.job_id = c.job_id
 WHERE j.job_status = 'suspended' AND c.checkpoint_kind = 'signal';

-- The steps of a job: which named steps ran and their outcome.
SELECT step_name, step_status, failure_message
  FROM acta.steps_view WHERE job_id = :job_id ORDER BY step_name;

-- Durable sleep timers for a job: when each named wait is due.
SELECT checkpoint_name, checkpoint_status, due_at_utc
  FROM acta.checkpoints_view
 WHERE job_id = :job_id AND checkpoint_kind = 'timer'
 ORDER BY due_at_utc;

-- Jobs carrying an exact tag, optionally narrowed by its preserved value.
SELECT t.entity_id AS job_id, j.job_status, t.tag_name, t.tag_value
  FROM acta.tags_view t JOIN acta.jobs_view j ON j.job_id = t.entity_id
 WHERE t.entity = 'job' AND j.namespace_name = :ns
   AND t.tag_name = :tag_name AND t.tag_value = :tag_value;
```

The reason for a transition lives on the `events` timeline (and per-step on `steps`); the
`jobs_state` row carries status only.

## Control verbs

All per-job actions go through `IJobs` (or your own endpoint that wraps it), which records the
actor and reason on the verb's event. Over HTTP the actor is the authenticated caller's name, never a
request field; a C# caller passes it.

```csharp
// Address the job by its public ref (what dashboards and clients hold), by deduplication key,
// or by internal id (advanced/debug); all three resolve through the same JobLookup.
JobLookup job = JobRef.Parse("job_01a14f4e954e7bf68ac609f3fcad8e3d");

await jobs.CancelAsync(job, "superseded by order-9");  // cascades to non-terminal descendants
await jobs.PauseAsync(job);                             // hold a not-yet-running job
await jobs.ResumeAsync(job);                            // Paused -> Ready at its kept next run (now if it was Suspended), or Idle for a permanent job with no firing outstanding
await jobs.RestartAsync(job);                           // resets failure budget + retention and Failed steps, runs now
await jobs.RaiseSignalAsync(job, "approval", payload);  // releases a Suspended WaitSignalAsync
await jobs.RescheduleAsync(job, inTwoHours);             // move a waiting job's next run (re-arms Ready)
await jobs.RescheduleAsync(job);                         // run now: an idle permanent job takes a manual firing
await jobs.ReprioritizeAsync(job, JobPriorityCode.High); // change claim priority in place
await jobs.PurgeAsync(job);                              // hard-delete a terminal job (audited as job.purged)
```

Cancel of a parent cancels the whole non-terminal subtree (descendants carry reason
`parent-cancelled`). Restart leaves a terminal row's history intact and re-arms the same id; there
is never a replacement row. Reschedule applies to Paused/Suspended/Ready/Blocked rows and re-arms them
Ready, or Blocked behind their lane, and gives an Idle permanent job a manual firing; purge refuses a non-terminal job, a job that has child jobs, and a child whose parent is not
terminal (finish or cancel the tree, then purge the leaves first).
Every verb returns Applied / Rejected / NotFound rather than throwing on an illegal transition.

Cancel, pause, resume, restart, reschedule, and reprioritize also take an optional `expectedVersion`
after `actor`. Left null, the verb writes unconditionally, which is what an operator acting on a
moving job wants. Pass the `Version` from a `GetJobResponse` (or from a previous control result) and the
write becomes a compare-and-set: a row that changed underneath you answers `VersionConflict` with its
current version instead of overwriting someone else's decision.

### Schedule controls

A schedule has verbs of its own, addressed by path through `operations.Schedules` and `/schedules/{jobNamespace}/{jobName}/{scheduleName}`: trigger, pause, resume, and preview. Trigger now, misfire catch-up, normal cursor movement, and historical backfill are deliberately different operations; [trigger, pause, resume, and preview](./scheduling.md#trigger-pause-resume-and-preview) says what each verb does, which job an applied trigger answers, and where a schedule's events are read.

### What each verb does from each status

Read from the provider routines, not from intent. Every verb answers one of three ways: **applied**
(`200`), **rejected** (`409`, the job exists but its status forbids the transition), or **not found**
(`404`); a verb given an `expectedVersion` has a fourth, **version conflict** (`409`, the row moved
since you read it), and nothing is written. All four carry the same body, so a
client reads `action`, the resulting `status`, and the row's `version` without special-casing the code.

`R` Ready · `B` Blocked · `S` Suspended · `P` Paused · `I` Idle · `X` Executing · `✓` Succeeded · `F` Failed ·
`C` Cancelled. **A** applied, **409** rejected.

| Verb | R | B | S | P | I | X | ✓ | F | C | Result on applied |
|---|---|---|---|---|---|---|---|---|---|---|
| `pause` | A | A | A | A | A | 409 | 409 | 409 | 409 | Paused |
| `resume` | 409 | 409 | 409 | A | 409 | 409 | 409 | 409 | 409 | Ready at the next run time it kept, or now when that has passed or the job was paused while Suspended on a signal or child wait, so its handler sees a signal raised meanwhile; or Blocked behind its lane; a permanent job with no firing outstanding returns to Idle |
| `restart` | A | A | A | A | A | 409 | A | A | A | Ready, or Blocked behind its lane, or Idle for a permanent job; failure budget, retention, and Failed steps reset |
| `cancel` | A | A | A | A | A | A | 409 | 409 | 409 | Cancelled, cascading to non-terminal descendants |
| `purge` | 409 | 409 | 409 | 409 | A | 409 | A | A | A | Row hard-deleted, `job.purged` event kept |
| `reschedule` | A | A | A | A | A | 409 | 409 | 409 | 409 | Ready at the new instant, or Blocked behind its lane; an Idle permanent job takes a manual firing |
| `reprioritize` | A | A | A | A | A | A | 409 | 409 | 409 | Priority changed, status untouched |
| `input` (amend) | A | A | A | A | A | 409 | A | A | A | Input replaced, format round-tripped |
| `signal` | A | A | A | A | A | A | 409 | 409 | 409 | Signal set; a Suspended job that waits on it goes Ready, and one that waits on something else stays Suspended |

Repeating a verb is a no-op only where the table says so. **`pause` is idempotent** (Paused stays
Paused, applied). **`resume` is not**: it accepts only Paused, so resuming an already-running job is
a 409 rather than a silent success. That asymmetry is deliberate - pausing twice expresses the same
intent, resuming something that was never paused does not.

Five rules the table cannot show:

- **`purge` also rejects a job that has children**, whatever its status. `parent_job_id` carries no
  database cascade, so purging a parent would leave a child pointing at a row that no longer exists.
  It also rejects a completed child whose parent is not terminal: the parent's replay dedupes onto
  that child and reads its result, so purging it would run the child again. Once the tree is
  terminal, purge the leaves first. A permanent job is purged only to decommission it: Idle,
  Failed, or Cancelled, with no firing outstanding and no live schedule pinning it. A job is also
  rejected while `sys.alerts` has not processed its latest events; purge it again after the next pass.
- **`cancel` on an Executing job** marks the row Cancelled and cancels the running attempt's token;
  the handler still has to return. The row is terminal before the process notices.
- **`signal` on a Paused job** records the signal and leaves the job Paused. The signal is not lost;
  it is waiting for a `resume`.
- **`input` on a terminal job is allowed**, which looks odd until you want it: amend the input of a
  failed job, then `restart` it. Only Executing rejects, because that is the state where a worker
  may already have read the payload. A job that stored no input has nothing to
  amend and answers `409` from the endpoint before the routine is reached.
- **`restart` has three more refusals.** A finished laned job is not restarted while an
  unfinished ancestor or descendant sits in its lane, a permanent job whose firing is still
  outstanding is not restarted around it, and a finished per-occurrence root is not restarted into a
  schedule that is full or gone. A restarted or resumed permanent job with no firing outstanding
  returns to `Idle`, where the next due schedule admits it; it does not run now.

## Tenant and namespace administration

Tenants and namespaces carry an operator-controlled status: `active` resolves at enqueue, `suspended`
withdraws admission. Tenant suspension is admission control, not work closure: new enqueues naming the
tenant name are rejected once the suspend has committed, while jobs already admitted keep running and a
running workflow may still create children that inherit the suspended tenant (an inherited tenant id
carries no key to re-check). Namespace suspension also holds the namespace's schedules: nothing is admitted
into it, by the admitter, a manual trigger, or a permanent job's Run now, and on resume the misfire policy decides the missed window
as it does after downtime. That reaches the namespace's own system jobs, so its alert projection, retention and
outbox relay stop firing until the resume. A running job's child start into it is rejected, which fails that attempt
unless the handler catches the rejection. An outbox row bound for it waits for the resume when the suspended
namespace's own relay would have moved it; a relay that still runs elsewhere refuses the row, and once its retries
run out quarantines it, to be requeued by hand. The jobs the namespace already holds, Ready or waiting on a retry,
are still claimed and run.
Suspension is reversible and status-only.

```csharp
await operations.Tenants.SuspendAsync("cust-4711", "billing hold");
await operations.Tenants.ResumeAsync("cust-4711");
await operations.Tenants.UpdateAsync("cust-4711", displayName: "ACME Corp", description: null, expectedVersion: version);
await operations.Namespaces.SuspendAsync("billing", "incident 1042");
await operations.Namespaces.ResumeAsync("billing");
await operations.Namespaces.UpdateAsync("billing", ownerTeam: "payments", description: null, expectedVersion: version);
```

Suspend/resume are idempotent (already-in-state succeeds as a no-op, reported as already-in-state, with no event); metadata updates are
version-CAS guarded, a stale `version` returns a conflict with the current row state instead of writing.
Tenant metadata is `displayName` + `description`; namespace metadata is `ownerTeam` + `description`
(namespaces have no display name). Null clears a field. A namespace value the build declares
(`w.OwnerTeam`, `w.Description`) is the build's, and every deploy rewrites it; one the build does not
declare is the operator's and survives every deploy. The seeded `sys` namespace cannot be suspended or
edited. Every applied transition is audited on the events timeline in the admin band
(`tenant.suspended` 10 through `namespace.updated` 22); tenant events land on the seeded
`sys` namespace, namespace events on the namespace itself.

A tenant is registered with `ITenants.RegisterAsync(tenantName, displayName, description)` or
`POST /tenants`; an enqueue that names an unregistered tenant is refused.

Over HTTP the same verbs are control-gated (`EnableControls` + confirmation):
`POST /tenants`, `POST /tenants/{tenantName}/suspend|resume`, `PUT /tenants/{tenantName}`, `POST /namespaces/{name}/suspend|resume`,
`PUT /namespaces/{name}`. A `PUT` replaces the metadata whole, so a field it leaves out is cleared. Reads are ungated unless `RequirePermissions` is on, where they need `acta.read`. `GET /tenants` pages the catalog and
`GET /tenants/{tenantName}` is the point read (`ITenants.GetAsync` in code); `GET /namespaces` and
`GET /namespaces/{name}` do the same for namespaces (`INamespaces.GetAsync` in code).

An enqueue that trips a tenant or namespace guard throws a typed `EnqueueRejectedException` whose
`Reason` is machine-readable: `NamespaceSuspended`, `TenantSuspended`, `TenantUnknown`,
`TenantRequired` / `TenantForbidden` (the definition's `TenantRequirement` policy), and
`TenantMismatch` (a child named a different tenant than its parent without the explicit override),
`RouteUnknown` (no definition by that namespace and name), `DefinitionRetired`, `AncestorLane` (a child
in the lane of an unfinished ancestor), `LaneIsolation` (a laned enqueue inside a caller
transaction whose isolation reads a snapshot older than the lane lock), and `ParentUnavailable` (a
child whose parent does not exist or has finished). Guard-wrapped HTTP handlers
map it to 409 with the reason in the ProblemDetails `reasonCode`, kebab-case (`route-unknown`).
The same guards apply when the external outbox relays a record: a record that trips one
(for example a Required definition staged without a tenant) lands in the outbox failure path
instead of enqueuing.

## Built-in control CLI

Every Acta host doubles as a control CLI: `<app> jobs <verb>` runs the verb against the database and exits, starting no worker; only `migrate` changes the schema ([Migration ownership](#migration-ownership)), and `deploy` and `migrate` write the host's job definitions and schedules ([Deploying jobs](#deploying-jobs)). `jobs` comes first, after any host switches such as `--environment Staging`, and is reserved: an app cannot take it as its own first argument.

```
<app> jobs help
<app> jobs info    <job-ref|deduplication-key|id:n>
<app> jobs status  <job-ref|deduplication-key|id:n>
<app> jobs result  <job-ref|deduplication-key|id:n>
<app> jobs explain <job-ref|deduplication-key|id:n>
<app> jobs events  <job-ref|deduplication-key|id:n> [--take <n>] [--after <cursor>]
<app> jobs cancel  <job-ref|deduplication-key> [--reason <msg>]
<app> jobs pause   <job-ref|deduplication-key> [--reason <msg>]
<app> jobs resume  <job-ref|deduplication-key> [--reason <msg>]
<app> jobs restart <job-ref|deduplication-key> [--reason <msg>]
<app> jobs signal  <job-ref|deduplication-key> <name> [--value <json>]
<app> jobs debug   <job-ref|deduplication-key> [--break]
<app> jobs deploy  [--force] [--json]
<app> jobs migrate [--force] [--json]
```

A `job_...` target is a job ref, `id:<n>` the internal id, and any other target, a bare integer included, a deduplication key. As on the HTTP API, `id:<n>` is for reads (`info`, `status`, `result`, `explain`, `events`), and a control (`cancel`, `pause`, `resume`, `restart`, `signal`, `debug`) given one exits 64, since an internal id names a job in any namespace. A read with no target takes it from the clipboard and prints what it took to stderr; a control with no target exits 64. Add `--ns <ns>` to pick the namespace for a deduplication-key lookup and `--json` for JSON output.

`explain` is where to start for any stuck, waiting, or failed job: it reads the job's status, lease, worker, newest 50 steps, checkpoints, and latest reason in one statement and says what happened and what to do next. It cannot know what your code or an external system did.

`debug` resets the job to Ready with restart semantics, claims exactly it, and runs the handler in this process, so a breakpoint in the handler stops it; leases keep renewing while it is stopped. A live worker can take the job first, and an Executing job is refused. `--break` raises the debugger at the handler.

| Exit code | Meaning |
|------|--------------------------------------|
| 0    | Applied, found, migrated, or debug run succeeded |
| 1    | Rejected, handler failed, or migration failed |
| 2    | Job not found                        |
| 64   | Usage error                          |
| 130  | Cancelled (Ctrl-C)                    |

## HTTP API and dashboard

`Acta.AspNetCore` serves the operator API over HTTP, and `Acta.Dashboard` serves the embedded dashboard beside it. `MapActa` from `Acta.Dashboard` maps the dashboard at its pattern and the API under `{pattern}/api/v1`; `MapActaApi` from `Acta.AspNetCore` maps the API alone. Call `MapActa` or `MapActaApi` for a mount, never both, or the API is mapped twice.

```csharp
app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.ConfigureEndpoints = group => group.RequireAuthorization("ActaOperators");
});
```

- **Local only by default.** Remote requests answer 403. A local request, one from loopback, from the machine's own address, or with no remote address at all, as on a Unix socket, must also address `localhost` or an IP. To expose it, set `LocalOnly = false` and either turn on [permissions](#permissions) or require your own authorization through `ConfigureEndpoints`, either of which covers the page, its assets, and the API; without one startup throws unless `UnsafeAllowAnonymousRemoteAccess = true`. Short of permissions or that acknowledgment, the guard stays on every endpoint that carries no authorization, so an empty `ConfigureEndpoints` hook, or one that adds `AllowAnonymous`, still answers a remote request 403; `RequireAuthorization` from a parent group counts. A fallback policy does not count, because the framework refuses to serve an endpoint that requires authorization when the authorization middleware is missing and makes no such check for a fallback policy. Neither package ships authentication, and CORS is not authorization.
- **Reads expose payloads.** Anyone who can read can see every job's input, result, and checkpoint values: `GET /jobs/{jobRef}/input` and `/result` serve the payload whole as the body, and the job detail and checkpoint reads carry each one up to `MaxInlinePayloadBytes`. Treat read access as access to every payload in the ledger.
- **Controls are opt-in.** `EnableControls = true` turns on every route that writes: the job controls, enqueue, the input update, signals, the schedule controls, definition overrides and retire, tenant and namespace changes, the alert and outbox controls, calendar changes, and tag writes. While it is off, each of them answers 403 with a problem whose detail names `EnableControls`, after the local-only guard and the permission check. Each control request must be confirmed by the header `X-Acta-Control: true`, a JSON content type, or a `Bearer` authorization header, none of which a page on another origin can send without a CORS preflight; a call with none of them answers 400. It is a guard against cross-site requests, not authentication. `ControlConfirmationHeaderName` renames the header, and mapping throws for an empty name or one of the five CORS-safelisted request headers (`Accept`, `Accept-Language`, `Content-Language`, `Content-Type`, `Range`), which any page may send without a preflight. A control's outcome is the `action` of its response body under a status that matches it: 200 for `applied`, and for `already-in-state` where the target already was as asked; 404 for `not-found`; and 409 for `rejected` or `version-conflict`. Two kinds of control answer otherwise: `POST /jobs` answers 201 when it inserts the job and 200 when a deduplication key matches an existing one, and a refused enqueue is a 409 problem document whose `reasonCode` says why; the outbox requeue and discard answer 202 `accepted`, since the next relay pass applies them. Problem details are kept for malformed input, authorization, and faults.
- **Jobs are addressed by ref** (`/acta/api/v1/jobs/{jobRef}`) or by deduplication key (`/jobs/by-deduplication-key?jobNamespace=&deduplicationKey=`). `EnableNumericIdLookup = true` also allows `/jobs/id:{n}` on reads, for debugging from a log line.
- **Lists page by cursor.** Pass a page's `nextCursor` back as `cursor` until `hasMore` is false; a cursor is tied to its query, and a stale one is rejected. Pages default to 50 and cap at 100, and `includeTotal=true` adds `totalCount`; on `GET /events` it needs `jobRef`, or `entityKind` with `entityId`, and a definition's events and the outbox sources take none.
- **A definition's performance is read from its events.** The definition page's Performance panel, and `GET /definitions/{jobNamespace}/{jobName}/performance?fromUtc=&toUtc=&intervalSeconds=` behind it, count the executions in the range that ended in success or in failure and give the p50, p95 and p99 of their durations, for each outcome, over the range and in each interval. A duration is one handler invocation as its worker timed it, so a job that waited and resumed counts only the execution that finished it, and a yielded or orphaned execution is not counted. The numbers are computed from retained events on each read, so event retention bounds how far back a window reads. A read takes the newest executions of its range up to a limit and says when it stopped there (`executionsHasMore`, `oldestAtUtc`). A definition that records failures only records a success when it follows a failure or closes an alert, so its succeeded durations are a partial set and the panel gives no failure rate. A window that spans a change of the definition's recording level mixes two recordings, which the panel cannot tell: its failure rate is comparable again once the window has passed the change. The trend marks each change of the namespace's deployment version in the window.
- `GET /capabilities` reports whether controls are enabled, so a client can hide edit UI.

The [OpenAPI document](./reference/openapi.json) is a file in the repository, not a route the app serves: a client is generated from it with standard OpenAPI tools and pointed at your mount's API root. It lists every route below its server, `/acta/api`, the default mount's API root (`/v1/jobs/{jobRef}`), each with its operation's name in camelCase as its id (`getJob`, `pauseJob`, `listJobTags`); its introduction states the confirmation rule with the mount's header name. Every code is an open string whose `x-acta-known-values` names the values this version writes, so a client keeps a value it does not know. The `kind` of a move a job explanation suggests is a plain string whose description lists its values, and a client keeps an unknown one the same way. Under `/v1` the contract changes by addition: a later 1.x adds routes, optional request members, response members, and code values, and never removes or renames one or adds a required member to a request, so the definition overrides update keeps requiring exactly the members it requires at 1.0.

### Permissions

`RequirePermissions = true` makes Acta authorize every request against the host's policy named for the operation's permission. Each route carries its permission in `ActaOperationAttribute`, and the OpenAPI document names it in `x-acta-permission`. The four permissions, whose names are the constants of `ActaPermissions`:

- `acta.read`: every GET, and the dashboard's pages.
- `acta.enqueue`: `POST /jobs`.
- `acta.control`: acting on one job, schedule, or alert, job tags, and requeueing quarantined outbox rows.
- `acta.admin`: deleting data, changing configuration, suspending or resuming a namespace or tenant, and tag writes on namespaces, tenants, and definitions.

The host defines one policy per permission, and mapping throws when one is missing:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActaPermissions.Read, policy => policy.RequireRole("ActaReader", "ActaOperator", "ActaAdmin"));
    options.AddPolicy(ActaPermissions.Enqueue, policy => policy.RequireRole("ActaOperator", "ActaAdmin"));
    options.AddPolicy(ActaPermissions.Control, policy => policy.RequireRole("ActaOperator", "ActaAdmin"));
    options.AddPolicy(ActaPermissions.Admin, policy => policy.RequireRole("ActaAdmin"));
});

app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.EnableControls = true;
    options.RequirePermissions = true;
});
```

- A call with no credential is challenged by the host's scheme. On an API route it answers 401 with no body and the scheme's `WWW-Authenticate` challenge, and Acta's check takes out a redirect, so a cookie scheme never sends a client to its login page; a dashboard page answers as the scheme does, so a cookie scheme sends a browser to sign in. A scheme whose challenge writes its own response body, such as OpenID Connect in form-post mode, answers as that scheme writes it, so give API clients a scheme that challenges with a status and headers.
- Authorization the host adds in front of Acta's check, `RequireAuthorization` on the mapped group or a fallback policy, runs first in the host's middleware and answers in its own way, which under a cookie scheme is a redirect to the login page, on an API route too.
- A caller the policy refuses gets a 403 problem whose detail names the permission.
- `GET /capabilities` answers `permissions`, the ones whose policy this caller satisfies, each probed from the caller's own credential; without `RequirePermissions` it lists all four. The dashboard shows a control only to a caller who holds its permission, reads them again when the window regains focus and after a 403, and shows the read-only mark to a caller who holds none that changes anything.
- Acta evaluates the policy in its own endpoint filter, authenticating with the policy's schemes or else the default one, so an `AllowAnonymous` the mapped group inherits does not switch the check off.
- A rule finer than a permission, such as one that reads the route's namespace, belongs to the host's own authorization on the mapped group through `ConfigureEndpoints`, where each endpoint's `ActaOperationAttribute` names its operation and permission.

## Retention and purge

A job landing terminal gets `retention_until_utc` from its definition's `JobRetention` (default 90 days). The hourly `sys.retention` job deletes terminal jobs past it, with their checkpoints, steps, and settled alerts, and deletes events and alerts past `JobEventsRetention` and `AlertRetention`, and stopped or dead workers once both `WorkerRetention` and `JobEventsRetention` have passed, so every event keeps its worker. The job sweep reads only its own namespace's jobs, starting at the newest one old enough to have expired under the namespace's shortest `JobRetention`, so a namespace whose definitions share a retention reads little beyond what it deletes. A pass deletes a bounded number of rows a section; one that reaches that with rows still expiring, or that deleted jobs and has more of its walk left, runs again at once instead of waiting for the next hour, so a busy namespace is swept back to back until it has caught up.

Two things follow from where the sweep starts. A namespace that mixes a short retention with long ones reads its long-kept jobs on the way to the expired ones, a bounded stretch of them each pass, so short-lived work beside a very large long-kept history is swept late: give that work a namespace of its own. And a job that landed under a retention you have since raised is kept until it is as old as the namespace's shortest retention now: a retention change deletes late, never early.

A permanent job is purged only by deliberate decommission: `Idle`, `Failed`, or `Cancelled`, no firing outstanding, and no live schedule pinning it (every schedule orphaned, or the definition retired); the retention sweep never deletes a permanent job whose definition is live. Like every job it holds at most one result: its latest successful firing's, cleared when the next firing is claimed.

A job is kept past its retention while one of its alerts is undelivered, since the failure it reports has reached nobody yet, and while the alert projector still owes one of its events; its settled alerts are deleted with it, an open incident included, so no alert outlives its job and its reminders end there. A paused or cancelled `sys.alerts` therefore holds retention back, by design: resume it rather than work around it.

`AlertRetention` is a hard cap: an alert past it is deleted even if it was never delivered or is still open, and a pass that deleted undelivered alerts logs one warning (`reason=alert-retention-cap`). If that line appears, find out why delivery is not settling.

| | Retention sweep | Manual purge |
| --- | --- | --- |
| Trigger | `sys.retention` finds terminal jobs past `retention_until_utc` | `IJobs.PurgeAsync`, `POST /jobs/{jobRef}/purge`, or the dashboard |
| Eligible job | Terminal and past its deadline, unless it is a stopped permanent job whose definition is live | Terminal now; a permanent job only when nothing pins it |
| Child jobs | Keeps a completed child while its parent is live; a finished tree drains leaves first | Refuses a parent with children and a child whose parent is live |
| Job event | None | `job.purged`, carrying the job's ref and name |
| The job's events and alerts | Events age out on their own window; alerts go with the job, which waits for an undelivered one | Deleted at once |

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
- Apply Acta migrations and deploy the app's jobs in a deployment step before workers start, with `<app> jobs migrate`
  ([Migration ownership](#migration-ownership), [Deploying jobs](#deploying-jobs)). Keep `MigrateOnStartup = false` outside development. A worker start writes no definition or schedule, and refuses to start in a namespace its build has not deployed in.
- Run one Acta package version across the workers of a namespace, apart from the overlap of a rolling deploy.
- Deploy with a rolling restart: run `<app> jobs migrate`, start the new build, stop the old one. See
  [Rolling deploys](#rolling-deploys) for what each build does with the other's jobs.
- Set `JobsOptions.DeploymentVersion` (in `ConfigureOptions`) to a build id or git SHA, or let it default to the entry assembly's informational version. Builds must differ in it to be told apart: an app that is `1.0.0` on every build is one version to Acta.
- Keep `HeartbeatInterval` identical across replicas of a namespace; the lease and dead-worker windows
  derive from it.
- Size total handler concurrency as `processes * workers per process * MaxConcurrentExecutors` (one
  worker per `Run(...)`), then validate against database capacity and downstream systems.
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
machine for a try-out, as the demos do, but that is not a supported deployment. On SQLite, Acta's tables
share `main` with the application's, so keep its table names free in that file; [known
limitations](./known-limitations.md#storage-and-providers) lists what a clash does.

Redis wakeup is optional. SQL polling is the correctness baseline; a wakeup transport only lowers
pickup latency.

### Migration ownership

In production a deploy step applies Acta's migrations, never application startup. Each way applies exactly what the database lacks:

| Where | How |
| --- | --- |
| Development | `MigrateOnStartup = true`: startup creates the database if it is missing, then applies. |
| Deploy step | `<app> jobs migrate`, or `{Provider}Migrations.ApplyAsync` from code. A server database must exist; SQLite creates the file. |
| A DBA | The script for your provider: [`schema-pg.sql`](./reference/schema-pg.sql), [`schema-mssql.sql`](./reference/schema-mssql.sql), [`schema-sqlite.sql`](./reference/schema-sqlite.sql). |

`jobs migrate` runs your app with its own `Use{Provider}` options, so it migrates the connection string and schema the workers use, and then deploys the app's jobs ([Deploying jobs](#deploying-jobs)). Hand it the deploy login through configuration and run it before the new build starts. The example assumes the app reads its connection string with `GetConnectionString("acta")`, as the [wiring shape](./developer-guide.md#wiring-shape) does:

```
$ ConnectionStrings__acta="<DDL login>" dotnet MyApp.dll jobs migrate
applied M20261111163142_init to schema acta
applied M20261112231452_index_lanes_by_namespace to schema acta
namespace billing: 1.6.1+b81d04e (Production)
  added    sys.alerts
  added    sys.retention
  added    send-invoice
  0 jobs unchanged
```

- A second run prints `schema acta is up to date` and each namespace with its jobs unchanged. It exits 0, or 1 with the database's error; `--json` prints `{"schema":"acta","applied":[...],"namespaces":[...]}`.
- It never creates a PostgreSQL or SQL Server database, never opens `master` or `postgres`, and never touches an outbox table.
- Locally it is `dotnet run -- jobs migrate`; on a container platform, run the same image with the arguments `jobs migrate` as a pre-deploy job.
- A verb runs while your host starts, and the process exits when it is done. A hosted service registered before `UseActa` has started by then and is ended without a stop, and one registered after it never starts, so register Acta before any service that should not run for a verb.
- A separate tool calls `await PostgresMigrations.ApplyAsync(connectionString, schema: "acta")`, or `SqlServerMigrations` or `SqliteMigrations`, which returns the names it applied.
- From 1.0.0 a rollback is safe: an older build runs against the newer schema, and its `jobs migrate` changes nothing.
- The provider's schema script re-creates every routine and view on each run, so re-running it is safe; run it with a client that stops at the first error (`psql -v ON_ERROR_STOP=1`, `sqlcmd -b`, `sqlite3 -bail`). An older release's script stops before it changes a database a newer release upgraded.
- On SQL Server, every install, the script included, refuses a system database and never changes the settings of a database that already exists. Only `MigrateOnStartup`, when it creates the database, turns `READ_COMMITTED_SNAPSHOT` on in it, as EF Core does. Acta runs with it on or off; turning it on yourself waits for every other session to leave the database, so do it in a maintenance window.
- A migration runs in one transaction under an exclusive schema lock: on a live system it briefly blocks the statements it collides with, and jobs wait rather than fail.
- Every start checks that the database has a history row for each migration the build ships, and fails naming the three ways above if not. Rows the build does not know are fine, so an older worker runs against a newer database. A hand-edited object is not detected.

Two logins keep DDL out of the runtime: a deploy login that runs `jobs migrate`, and the workers' login with no DDL right. On PostgreSQL, set default privileges once, before the first `jobs migrate`, so every table a migration adds is granted as it is created:

```sql
GRANT CREATE ON DATABASE "MyApp" TO acta_deploy;
-- in the database
ALTER DEFAULT PRIVILEGES FOR ROLE acta_deploy GRANT USAGE ON SCHEMAS TO acta_app;
ALTER DEFAULT PRIVILEGES FOR ROLE acta_deploy GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO acta_app;
```

The workers' login also uses what PUBLIC holds: PostgreSQL's defaults, TEMPORARY on the database and EXECUTE on functions, and the schema's own USAGE grant on the job id sequence. A database that revokes PUBLIC's privileges grants those to the workers' login instead.

On SQL Server, the deploy login needs DDL in the database. Grant the workers' login on the schema once the first `jobs migrate` has created it; a schema grant covers what later migrations add:

```sql
ALTER ROLE db_ddladmin ADD MEMBER acta_deploy;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::acta TO acta_app;
```

The external outbox's table lives in the producer's database, so Acta never creates it on its own:

- In the producer's own migrations, add `migrationBuilder.Sql(SqlServerMigrations.CreateOutboxScript(schema: "dbo"))` to one EF Core migration, or run the same script through DbUp, Flyway, or a DBA.
- Without a migration tool, call `await SqlServerMigrations.ApplyOutboxAsync(ordersDb, schema: "dbo")` from the deploy step, or at startup in development. It creates the table when it is missing and returns `true`; an existing table is left as it is.
- Name the schema whenever the deploy login differs from the relay's: an unqualified table resolves through the login's default schema on SQL Server and `search_path` on PostgreSQL.
- A table or schema other than the default must match `source.Table` and `source.Schema`.
- Give the producer's and the relay's logins SELECT, INSERT, UPDATE, and DELETE on the table; neither needs DDL.

### Deploying jobs

`<app> jobs deploy` writes the job definitions and schedules of every namespace the host runs a worker for, and records the app's version there. It applies no migration and needs no DDL right, so the workers' login can run it; `jobs migrate` applies the schema and then runs the same deploy.

```
$ dotnet MyApp.dll jobs deploy
namespace billing: 1.6.2+5c0ffee (Production)
  added    refund-invoice
  changed  send-invoice
  11 jobs unchanged
  not declared by this build: old-report
```

- The version is `JobsOptions.DeploymentVersion`, by default the entry assembly's informational version. A version's first deploy in a namespace adds a row to `deployments` (read it through `deployments_view`), and the version becomes the namespace's current one, `namespaces.deployment_version`.
- Each definition keeps the version whose deploy introduced it in `definitions.created_deployment_version` (in `definitions_view` and the definition read); no other deploy changes it.
- A version that deployed in the namespace earlier and is not its current one is an older build: its deploy changes nothing, says so, and exits 0. `--force` deploys it and makes it current, which is how a rollback restores the earlier definitions.
- Every run that deploys leaves a `namespace.deployed` event on the namespace, also a run that changed nothing: its message names the version and the one it replaced, and its details carry `deploymentVersion`, `previousDeploymentVersion`, `forced`, and the `added` and `changed` job names. An older build's skipped run leaves none.
- A job is `changed` when its policy, its contract, or a schedule it declares for this environment differs from what is stored: a new cron, an added or dropped `[Schedule]`, a changed bound. The line names the job; a retimed schedule also leaves its own `schedule.retimed` event.
- Every namespace is written in one transaction. A failure in any leaves all of them as they were, and the verb exits 1 with the error; after `jobs migrate`, the schema it applied stays applied.
- The name after the version is `JobsOptions.EnvironmentName`, which decides the schedules a `[Schedule(Environments = ...)]` declares. Run the verb with the workers' configuration.
- `not declared by this build` lists the active definitions the build does not carry. The deploy leaves them as they are.
- `--json` prints `{"namespaces":[...]}`, one object per namespace: `namespace`, `deploymentVersion`, `environment`, `currentDeploymentVersion`, `older`, `added`, `changed`, `unchanged`, `undeclared`.
- A schedule that names a calendar needs that calendar published first: the deploy fails on a family with no published version ([Calendars](./scheduling.md#calendars)).
- A host that runs no worker has nothing to deploy.

A worker start writes none of this. It reads its namespace and refuses to start, naming `jobs deploy`, when:

- nothing has been deployed in the namespace;
- the namespace has deployed versions and the worker's own is not one of them, which is what stops a local build pointed at production;
- the build declares a job with no definition there, which means the deploy ran with other options than the workers (`RunSystemJobs`, the outbox relay), or another build deployed under the same version.

Development needs no deploy step: with `MigrateOnStartup = true`, startup applies the schema and then writes the definitions and schedules before the workers start. It records no version, so the namespace stays open to any build, and it writes nothing in a namespace a deploy verb has deployed. That holds on a developer's machine too: after one `jobs migrate` or `jobs deploy` against a local database, startup no longer writes there, so keep using the verbs or delete the database. `jobs debug` runs a stored job from any build and writes no definition.

Running `jobs deploy`, or `jobs migrate` under the deploy login, as a step before every pod starts is safe: an older pod's run changes nothing.

### Rolling deploys

Deploy: run `<app> jobs migrate`, start the new workers, stop the old ones once the new ones are healthy. Rollback: start the old build, stop the new one; its version has deployed, so its workers start. To put the old build's definitions and schedules back as well, run its `jobs deploy --force`. While both builds run:

- Either build may claim a job only the other can run. The worker without the handler hands it back to `Ready` without charging a failure and stops claiming that definition.
- A deploy never retires a definition, so the old fleet finishes its jobs and a rollback finds nothing missing.
- Definitions and schedules are the current deployment's, and an older build cannot overwrite them: its workers write nothing, and its deploy changes nothing unless forced. Where an older build's own declaration of a job differs, its worker runs the job under the deployed values of the policy an operator can override (priority, attempts, limits and the rate meter they spend from, timeouts, retention, alert routing); the rest is that build's own.
- A schedule the new build dropped is orphaned (`schedule.orphaned`), and comes back, without its pause, when a build that declares it deploys (`schedule.retimed`). Pause the job, not the schedule, to hold it across a rollback.
- A schedule that holds a status, expression kind, misfire strategy, full behavior, or expire action the older build does not know is not that build's to admit: its workers leave the row alone and admit every other schedule. After a rollback no worker admits it, and it shows as overdue; the older build's `jobs deploy --force` puts its own declaration back, and `resume` clears a status it cannot read.
- Under `PayloadContractDriftMode.Fail` a deploy stops while a stored contract differs from the build's own. A worker start does not check it.
- Migrations are forward-only and additive, and an older build never rewrites what a newer one migrated.

Jobs of a definition only the withdrawn build carried stay `Ready` until a build with the handler returns or an operator retires the definition (`IActaOperations.Definitions.RetireAsync`, `POST /definitions/{namespace}/{name}/retire`, or the dashboard). Retiring cancels the definition's parked jobs with `definition-retired`, lets running attempts finish, and rejects new enqueues. A retired definition stays retired while workers that still carry it start; the next deploy by a build that declares it reactivates it. Until then, a long run of such jobs slows the claims of every worker that lacks the handler.

### Coexisting with your application's packages

Acta declares only minimum versions for its database drivers (`Npgsql`, `Microsoft.Data.SqlClient`,
`Microsoft.Data.Sqlite`): floors, never upper bounds. Your application stays free to pin the driver
version it needs, including through Central Package Management; NuGet resolves one driver for the
process and Acta uses whatever it resolves.

An upper bound in package metadata would not actually protect you: NuGet demotes a version-range
conflict to a warning that most builds never read. So the check runs at startup instead: the
provider compares the major version of the driver actually loaded against the major Acta was
tested with, and on a mismatch logs one warning naming both. The host keeps running, because a
driver often moves when another library pulls in its next major; the warning tells an operator which
version is running untested, and the conformance suite is what clears a new one.

### Worker count and executor count

Each `Run<TManifest>(...)` starts one worker in a process, and that worker owns exactly one namespace;
a process can run several. You can run several replicas of the same namespace for horizontal capacity;
every worker is a peer and claims work competitively from SQL.

Keep a namespace at 16 workers or fewer. Its workers all claim from one head of the claim index, which
leads with the namespace, so past that each worker added makes claims queue on the head and
completions wait on claims: on PostgreSQL a single namespace drained fastest at 16 workers and slower
at 24 and 32. Size a namespace by its work (peak jobs per second times average handler seconds,
divided by executors per worker) plus a spare for a crash or a deploy, and when it needs more than 16
workers, split its work across a second namespace, which has a claim head of its own.

`MaxConcurrentExecutors` is per worker, and every `Run(...)` in a process is its own worker: a process
running two workers at 32 executors has up to 64 executions in flight. If you run 6 replicas of a
namespace with 16 executors each, the namespace can have up to 96 handlers in flight before downstream
throttles or job-level limits apply.

Tune together:

- `MaxConcurrentExecutors`: handler concurrency per worker, and the most rows one claim takes.
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
  reclaim those rows until the oldest transaction that can still see them ends, and `jobs_state` grows
  meanwhile.

SQL Server:

- Put the transaction log on the fastest storage you have. Every claim, every completion batch, and
  every enqueue commits, so a busy server waits on log flushes (`WRITELOG`) before anything else.
- Consider `ALTER DATABASE ... SET AUTO_UPDATE_STATISTICS_ASYNC ON`. Acta's tables grow quickly, and
  a synchronous statistics refresh makes the query that triggered it wait
  (`WAIT_ON_SYNC_STATISTICS_REFRESH`); the asynchronous refresh lets it run on the old statistics.
- `jobs` and `jobs_state` take row locks only (`ALLOW_PAGE_LOCKS = OFF`), so index maintenance
  rebuilds their indexes: `ALTER INDEX ... REORGANIZE` refuses them, and `REBUILD` works.

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
enqueue inputs, variables, progress, and signal values. Handler results are measured
against the same cap, but one past it is dropped rather than thrown, because the handler has already
run: the job still succeeds, a warning is logged, and its events carry `result-oversized`. A step
result past it fails the step: uncaught, the job fails for good with `non-retryable-exception`, since its
body has run too and a retry would run it again. A restart runs an ordinary step again and finds an
at-most-once one interrupted.

For large files, exports, media, archives, reports, ML inputs, or sensitive data, store the body in
file/blob/object storage and enqueue a small reference that includes enough information to verify
what the handler reads, such as URI, checksum, byte length, and content type.

Avoid putting secrets and high-volume PII in job inputs, results, tags, or alert text. Acta state is
operator-visible SQL state.

### Dashboard exposure and auth

The rules are in [HTTP API and dashboard](#http-api-and-dashboard), and [`414-dashboard-sign-in`](../concepts/400-observability-and-alerts/414-dashboard-sign-in/) is a runnable host with a cookie sign-in and three roles. A typical remote setup, with controls for one role:

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

Behind a reverse proxy on the same host, `LocalOnly` protects nothing: the peer it checks is the proxy, over TCP or a Unix socket, and the host name it checks is whatever the proxy sends. Gate remote access with host authorization, and configure `ForwardedHeaders` if the host needs the client's IP. Keep secrets and personal data out of payloads, since every reader sees them.

With `LocalOnly = false` the control confirmation is no defense against DNS rebinding, since a page that rebinds its own name to the server is same-origin and may send any header; host filtering is, so set `AllowedHosts` to the names the server answers to.

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

### Logs

- Acta logs failures, misconfigurations and rare events, never a line per successful job, and it writes no Debug lines.
- Every field Acta adds to a line starts with `Acta`, so none clashes with your own fields. Every line of an attempt, your handler's included, carries `ActaJobId`, `ActaJobRef` (the ref the dashboard and the API show), `ActaJobName`, `ActaNamespace`, `ActaExecutionNumber`, `ActaWorkerId`, and `ActaCorrelationKey` when the job has one.
- With no Slack channel, alerts arrive as `Acta alert` lines carrying `ActaAlertRef`, `ActaJobRef`, and `ActaAlertKind`, so log-based alert rules can route on them.
- An outage downstream of your handlers logs one Warning, with its stack trace, per failed attempt. To quiet a storm without losing Acta's own errors, raise the category:

```json
{ "Logging": { "LogLevel": { "Acta.WorkerRuntime": "Error" } } }
```

### Metrics

Acta publishes on one .NET meter, `Acta`: add it to an OpenTelemetry pipeline with `AddMeter("Acta")`.
Every instrument but `acta.completions.unsettled` carries a `namespace` tag; the wakeup counters carry
it when the wake names one. A listener that throws loses that measurement and nothing else: the worker
keeps claiming, admitting, and settling. Each instrument declares its own unit: milliseconds for the timings, and
seconds for the two lags that can run to hours, `acta.alerts.lag` and `acta.schedule.due.age`.

| Instrument | Unit | What it measures |
| --- | --- | --- |
| `acta.executions` | execution | Completed job executions, tagged by outcome. |
| `acta.executions.duration` | ms | Handler execution duration. |
| `acta.claims` | claim | Claim attempts, tagged by result. |
| `acta.steps` | step | Durable step outcomes, tagged by outcome. |
| `acta.jobs.inflight` | job | Jobs this process holds, tagged by state: claimed, executing, or finalizing. |
| `acta.completions.batch` | completion | Completions the finalizer wrote in one call. |
| `acta.completions.unsettled` | completion | Completion writes still repeating after a failure. |
| `acta.executions.fenced` | execution | Attempts stopped because a write found their job held by another execution. |
| `acta.lease.headroom` | ms | Time an attempt had left on its lease when a renewal confirmed it. |
| `acta.recovery.passes` | pass | Recovery passes this process ran, tagged by outcome. |
| `acta.lock.release.failures` | failure | Lock releases that failed and were left to expire. |
| `acta.wakeup.publish.attempts` | publish | Wake publishes, tagged by channel and reason. |
| `acta.wakeup.publish.failures` | publish | Wake publishes the transport failed to deliver. |
| `acta.wakeup.waits` | wait | Idle claim-loop waits, tagged by how they returned. |
| `acta.alerts.lag` | s | How far the alert projector stood behind the database clock when a pass began. |
| `acta.alerts.projection.skips` | skip | Poison alert events the projector skipped. |
| `acta.schedule.decisions` | decision | Decisions an admission pass applied: admitted, skipped, expired, blocked, exhausted, or error. |
| `acta.schedule.lag` | ms | How far past its nominal instant an occurrence stood when it was admitted. |
| `acta.schedule.due.age` | s | How long past its evaluation instant a due schedule stood when a pass read it. |
| `acta.schedule.pass` | ms | Wall time of one admission pass. |
| `acta.schedule.stale` | group | Plan groups another writer moved first; each replans. |
| `acta.firing.start.delay` | ms | Time from a firing's admission to the first attempt that ran it, tagged by job name too. |

### Retention

Set retention long enough for audit, incident response, and late restarts; the rules are in [retention and purge](#retention-and-purge).

### Process behavior on failure

| Surface | Failure shows as | Process | Action |
| --- | --- | --- | --- |
| Worker startup | A validation failure (options, clock skew, schedule time zone, alert routing in `Fail` mode) or a refusal (nothing deployed in the namespace, a version that has not deployed there, a declared job with no definition) throws from `StartAsync`. | Does not start. | Fix the configuration, or run `jobs deploy`, and restart. |
| Claimer, executors, finalizer, heartbeat | An Error log under `Acta.WorkerRuntime` (`claim iteration failed`, `heartbeat tick failed`, `executor faulted on job`, `finalizer faulted`). | Backs off and keeps running; one bad job never stops a loop. | Investigate the logged cause, usually the database; no restart needed. |
| `sys.alerts`, `sys.retention`, `sys.outbox` | A failed pass is a job failure, retried after fifteen seconds, doubling to a minute, for up to fifteen attempts; the next scheduled pass then starts fresh. An alert transport fault logs a Warning, and that delivery retries. | Keeps running. | Usually none. |
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
- A `Cancelled` job never alerts, whether an operator, `ctx.CancelAsync`, or a deadline cancelled it. Budget-neutral re-arms (a held concurrency key, a rate limit) do not alert either.
- Automatic alerts do not depend on the recording level: `Failures` records the failed, orphaned, terminal, and recovery transitions as `Audit` does, and under an enabled alert profile also a one-shot's success past its first attempt, so every profile fires the same at every level, and resolves the same except where [known limitations](./known-limitations.md#execution-model) say a permanent job's does not.
- For recurring work use `OnFailure`: a permanent job's failures never land `Failed` (a spent budget returns it to `Idle`), so `OnTerminal` and `Info` stay silent for them.

### Incidents

An automatic alert is an incident: one open row per job, kind (`FirstFailure`, `ThresholdReached`, `FinalFailure`), and reason. A repeat folds into it and raises `occurrence_count`; the job's next success resolves it, and a later failure opens a new incident. `ThresholdReached` fires once, when the incident's count reaches `AlertFailureThreshold` (default 3). The pass that raises a job's `FinalFailure` resolves the job's open `FirstFailure` and `ThresholdReached` alerts, which said it was retrying, so a job that failed for good has one open alert; one not yet sent is not sent.

A one-shot job raises at most three alerts, and its incidents end with it. A permanent job's incident stays open until a retry of the failing firing succeeds, a later fire succeeds at `Audit`, or an operator resolves it, and is delivered again every `AlertReminderInterval` (default 24 hours): an alert still open is the sign that nobody fixed it.

### Channels and delivery

```csharp
w.AddAlertChannel("ops-slack", AlertTransportKinds.SlackWebhook,
    endpoint: builder.Configuration["Alerts:SlackWebhookUrl"]!,
    o => o.MinSeverity = AlertSeverityCode.Warning);
```

- Route a job with `[Job(AlertChannelName = "ops-slack")]`. Without one it goes to the namespace's implicit `default` channel, which logs, so failures reach the logs with no setup. `RunbookUrl` on the job travels with the notification.
- The built-in transports are `log` and `slack-webhook`; a custom one registers an `IAlertTransport`. Endpoints and credentials stay in configuration and are never stored in the database.
- A send has ten seconds. One that has not answered by then is cancelled, given up, and retried on the delivery backoff, and its channel is not asked again in that pass, so an endpoint that never answers costs a pass ten seconds however many alerts it has queued. A pass that has spent thirty seconds, as three such endpoints make it, leaves the alerts it did not reach for the next pass a minute later.
- A channel's `MinSeverity` and its `Disabled` or `Deprecated` status suppress delivery. A channel the namespace does not configure fails delivery, and `AlertChannelValidationMode` checks every definition's routing at startup.
- A failed send retries for a few minutes, about five attempts, and then settles `Failed`. Delivery is at least once. Resolving an alert before it is sent cancels the send.

### Operator surface

`IActaOperations.Alerts` lists, reads, acknowledges, and resolves alerts, as do the dashboard and `POST /alerts/{alertRef}/acknowledge|resolve`. Acknowledging does not stop reminders; resolving does. Neither changes the job. An alert is deleted with its job, by retention or a purge, and otherwise ages out on `AlertRetention`. The concepts `402-alerts`, `403-alert-channel`, `405-real-alert-routing`, `406-automatic-failure-alerts`, and `411-alert-escalation` run each part.

## What happens if…

Failure-mode answers for operators and handler authors. Start a single-job investigation with
`<app> jobs explain <job-ref>` or the job's page in the dashboard; use this page to understand the
expected transition and the application responsibility behind it.

### …a worker crashes before starting the handler?

The claim already moved the job to `Executing` under the worker lease. After the lease expires,
the next recovery pass returns it to `Ready` while retry budget remains, or marks it `Failed` when the budget
is exhausted (a permanent job returns to `Ready` for another attempt of its firing while budget remains,
and to `Idle` with the firing settled `failed` once it is spent). No handler side
effect occurred, but the lost attempt counts against the budget like any other crashed attempt.

### …a worker crashes while the handler is running?

The lease expires and recovery makes the job runnable again within budget, within two leases of the crash: six minutes at the default heartbeat. Bare handler
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
`WaitSignalAsync` with the same name, it reads the stored value without suspending, and the value stays
set: a replay or a later wait on that name reads it again, until a later raise replaces it. A signal sent
to a terminal job is rejected; a signal sent to a paused job is stored but does not resume the job
until the job itself is resumed.

### …a signal never arrives?

An unbounded `WaitSignalAsync` suspends the job indefinitely, and retention never touches a
suspended job, only terminal ones. When "never" is a real possibility, bound the wait:
`WaitSignalAsync(name, timeout)` cancels the job on expiry, budget-neutral, with reason
`wait-timed-out`, so the row lands terminal and ages out normally; `TryWaitSignalAsync`
resumes the handler with a `TimedOut` result instead, for workflows that expire or compensate
explicitly. The deadline is stamped on the checkpoint when the wait first arms and is never extended:
worker downtime, restarts, and replays all count against it.

### …a child never finishes and the parent is waiting on it?

`TryWaitChildAsync(child, timeout)` resumes the parent with `TimedOut` and cancels the
unfinished child and its descendant subtree; the parent stays active and decides what happens
next. A group is the same story under one clock: the bounded `JoinAsync`, `ParallelAsync`, and
`MapAsync` spend a single persisted deadline across all members:
completed children keep their outcomes, only the unfinished ones are cancelled, and the
at-least-once shape below covers each of those cancellations the same way. Two limits remain. The subtree cancellation is initiated by the parent's resumed attempt, so
it is at-least-once like everything else: a crash between the durable expiry and the cancellation
re-runs it on replay, and a parent that goes terminal without ever replaying leaves its children
running, which is what parent death has always meant in Acta. And an expired wait is
terminal for its name: a replay that reaches an unbounded `WaitChildAsync` over an already-expired
latch cancels the job rather than parking it forever on a wait that can no longer complete.

### …a sleeping or signal-waiting job has no worker?

The wait occupies no executor. A timer remains durable until due; a signal wait remains durable
until raised. Once the job becomes `Ready`, a live worker of the namespace whose build carries the handler
must claim it. Explain and the Workers page show whether that capacity exists.

### …the application deploy removes a handler that queued jobs still need?

Stored rows keep their durable job name. They are not rewritten to another handler. Keep the old
handler in the build until runnable/waiting rows drain or expire, or deploy a compatible/versioned
migration plan. Search retained failed jobs too: an operator restart can make them runnable again.

A worker that claims such a job hands it straight back. Claims are selected by namespace, not by
what a process can run, so during a rolling deploy either version can claim either version's jobs.
A worker with no handler for a claimed job returns it to `Ready` with the lease cleared, no failure
charged and no retry consumed, due again one safety-poll interval later, and stops claiming that
definition for the rest of its process life; the first hand-back logs a warning naming the job ref
and definition id, and the rest of the same claimed batch log nothing. Under `Audit` recording the
hand-back writes one `job.execution-yielded` row whose message names the definition; under
`Failures` it writes nothing, because nothing failed. A job whose definition
no live worker carries sits `Ready` without progressing, and its definition page shows it `Active`
with queued work and no worker that carries it; the fix is to redeploy a build that carries the
handler, or to retire the definition from the dashboard, which cancels its parked jobs. Removing a
handler from a deploy never cancels anything by itself; see
[Production § rolling deploys](#rolling-deploys).

A permanent job follows the same rule. A deploy that drops every schedule of one leaves it `Idle`
with nothing due, and a deploy that brings a schedule back puts the schedule in front of the admitter
again. A deploy that adds a `[Schedule]` registers the permanent job beside any ordinary job of that name: the
permanent job carries no deduplication key, so an ordinary job keyed by the job name is unrelated to it. See
[Scheduling and calendars](./scheduling.md).

Retiring a permanent job's definition leaves the job inert instead of cancelling it: a firing admitted and
not yet claimed settles cancelled and the job returns to `Idle`, a paused one stays paused, and a cancelled one
stays cancelled. An attempt already running returns the job to `Idle` when it succeeds or spends its failure
budget; any other landing (a retry, a wait, a handler's pause or failure) lands cancelled. The job refuses a
manual firing while the definition is retired. A deploy by a build that declares the definition again retimes its
schedules from now, so the job fires at the first occurrence after the comeback and the retired interval is not
caught up.

### …a checkpoint name changes during a deployment?

The new name is a different step, variable, signal, or timer. A step can repeat, a variable can appear absent, or a
signal can wait on a name that old producers never raise. Follow
[Renaming a step, variable, signal or timer](./developer-guide.md#renaming-a-step-variable-signal-or-timer) and treat the change as a data
migration, not a refactor.

### …the service is down across scheduled occurrences?

The schedule's misfire policy decides the missed occurrences when the admitter next evaluates it:
`Skip` moves past them, `CatchUpOnce` runs one coalesced catch-up firing, `CatchUpLatest` runs the
latest, and `CatchUpAll` admits every one in order: one per settlement on a permanent job, and under
`PerOccurrence` delivery as many root jobs at once as `MaxOutstanding` allows. An occurrence less than a minute overdue counts as due
rather than missed, and neither a deploy nor a worker start decides anything: a deploy writes the declaration and leaves every
occurrence to the running fleet's admitter. See [catch-up, misfire, and expiration](./scheduling.md#catch-up-misfire-and-expiration).

### …an operator restarts a failed or cancelled job?

Restart re-arms the same job and keeps its input and event history. Succeeded durable steps still do
not rerun unless state was explicitly reset, but bare handler code and incomplete steps can run
again, and a failed at-most-once step runs again. An interrupted at-most-once step still
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

For any single stuck, waiting, or failed job, run `<app> jobs explain <job-ref|id:n>` first. It reads
the same `jobs_state` / lease / worker / checkpoint / step / event rows the checks below describe, in
one consistent snapshot, and states in plain English where the work is, why it moved there, and the
next action (raise a signal, wait for crash recovery, resume, restart, or inspect the timeline). The
manual checks below are for triaging many jobs at once, confirming what `explain` reports, or when
you only have SQL access. A job's page in the dashboard opens with the same account.

`jobs explain` diagnoses Acta's durable state. It can tell you which steps completed, which
wait is active, what the latest recorded reason was, and what operator actions are available. It
cannot predict arbitrary user code, external system state, or whether a non-idempotent side effect
already happened outside Acta.

Sample outputs for a suspended, an expired-lease, a failed, and a succeeded job are in
[Operator guide § built-in control CLI](#built-in-control-cli).

### A Job Did Not Start

Start with `acta.jobs_view` so names and status values are decoded:

- `job_status`: a claim takes `ready` jobs, and a `suspended` job whose `next_run_at_utc`, its wait's timeout, is due. A `blocked` job waits behind the earlier unfinished job in
  its `lane_name` and goes `ready` when that one settles.
- `next_run_at_utc`: delayed, slept, rescheduled, and permanent jobs may be ready but not due yet.
- `namespace_name`: workers only claim inside their own namespace.
- `definition_name`: a deploy must have written the matching definition in that namespace (`jobs deploy`).

If you need raw storage state, `jobs_view` is built from the job's `jobs_state` row (1:1 with `jobs` by `job_id`).

Then check workers:

- `acta.workers_view.worker_status`
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
pending signal `checkpoints` (kind `signal`) and raise the expected signal through `IJobs.RaiseSignalAsync`, the
CLI, or the HTTP signal endpoint if controls are enabled.

Sleep and reschedule waits are timer-based. Check timer `checkpoints` (kind `timer`) and `next_run_at_utc` for the due
instant.

A bounded wait tells you when it gives up: `Explain` prints "times out at" with the deadline, the
checkpoint's `due_at_utc` carries it, and the suspended row's `next_run_at_utc` is non-null: the job
will wake itself. A suspended row with `next_run_at_utc` NULL is an unbounded wait and wakes only
on a raise. To put a bound on a job stranded under code that has since gained one, reschedule it
once: the re-entered wait arms the new deadline on a checkpoint that never had one.

### A Job Is Paused

Pause is sticky. A paused job does not auto-resume unless the paused object is a schedule pause with
an `until` timestamp. Resume a paused job with `IJobs.ResumeAsync`, the CLI, or an enabled dashboard
control endpoint.

Schedules have their own control surface through `operations.Schedules`; pausing a schedule is distinct
from pausing the job itself.

A permanent job whose schedules are all paused, exhausted, or removed by a deploy sits `Idle` with
nothing due, and fires again by itself once a schedule offers a run. A per-occurrence schedule has no
permanent job: its roots are ordinary jobs with the schedule as provenance, and `schedules_view` shows how many
are outstanding against `max_outstanding`. See
[Scheduling and calendars](./scheduling.md).

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

- A signal name is a name like any other: dotted kebab-case, trimmed and lowercased, so `Payment.Received` and `payment.received` are one signal. It is scoped to one job.
- A terminal job rejects new signals.
- A paused job records the signal but stays paused.
- A raise that arrives after a bounded wait timed out releases nothing; its event reads "Signal not
  applied: the wait had already expired."
- HTTP signal endpoints require controls to be enabled and the same confirmation as other control
  requests: the header `X-Acta-Control: true`, a JSON content type, or a `Bearer` authorization header.
  It is the cross-site request forgery barrier, not authentication.

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
- A row the relay could not deliver is quarantined in place (`outbox_status_code = 90`) and excluded from claims.
  Requeue or discard it through `IActaOperations.Outbox`, `POST /outbox/{jobNamespace}/requeue` or
  `/discard`, or the dashboard's outbox panel; the
  [quarantine SQL recipes](#quarantined-outbox-rows) are the unaudited fallback. A requeue without
  `outboxIds` takes every quarantined row of the namespace. A discard names its rows in `outboxIds` or
  sends `"all": true`, exactly one, and answers 400 for neither or both, so leaving the ids out never
  deletes every row; `IOutbox.DiscardAsync` takes the same choice, `outboxIds` or `all: true`, and
  throws `ArgumentException` otherwise. Recoverable rejections (for example
  an unknown route) quarantine after the failure threshold; malformed or oversize rows quarantine at once.
- Resolve the resulting job by `(namespace, deduplication key)` with `IJobs.GetIdAsync`; a null
  result means the request has not been relayed yet, not that it was lost.

Background: [Transactional enqueue and the external outbox](./developer-guide.md#transactional-enqueue-and-the-external-outbox).

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
explicitly. See [Transactional enqueue and the external outbox](./developer-guide.md#the-external-outbox).

### The Dashboard Or API Is Not Reachable Remotely

`MapActa(...)` and `MapActaApi(...)` are local-only by default. Remote clients receive 403 until the
host sets `LocalOnly = false` and either sets `RequirePermissions` or adds ASP.NET Core authorization
through `ConfigureEndpoints`. A 403 titled `This Acta endpoint carries no authorization` means the
endpoint has neither: check that the hook requires authorization, since a fallback policy alone does
not count, and that no `AllowAnonymous` covers the endpoint. A browser on the same machine also gets 403 when it names the machine rather than
`localhost` or an IP address: local-only accepts only those host names.

Mutating controls are also disabled by default. Enable them explicitly with `EnableControls = true`
and keep authorization on the mapped endpoint group.

### The CLI Did Not Run A Worker

When an Acta host starts with `jobs` as the first argument after any host switches, it runs the built-in
CLI verb and exits. No worker loop starts. This is intentional.

Every verb but `migrate` expects a provisioned schema, and only `deploy` writes the catalog: a worker start writes no definition or schedule.

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
identifiers as needed and replace interval arithmetic with `DATEADD`; on SQLite, drop the `acta.`
prefix and write `now()` as `strftime('%Y-%m-%dT%H:%M:%fZ', 'now')`, with an interval as its modifier
(`strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '-1 hour')`): a SQLite view shows its `_utc` columns as ISO 8601 UTC text,
which compares in time order, while the ledger tables keep Unix epoch milliseconds. The first recipe enqueues a job
(a write, through the installed routine) and the outbox recipes repair the producer's table; the rest
are operator/debugging reads.

For reads, start with the curated operator views: `acta.jobs_view`, `acta.events_view`,
`acta.checkpoints_view`, `acta.steps_view`, `acta.schedules_view`, `acta.alerts_view`,
`acta.workers_view`, `acta.definitions_view`, `acta.calendars_view`, and `acta.tags_view`. Views expose name-only decodes for common job
codes (`job_status = 'failed'`) and keep the raw `*_code` columns beside them. They are the stable read
surface; the storage tables are not. Runtime code does not depend on them.

[`data-model.md`](./reference/data-model.md) documents every storage table, and
[`code-families.md`](./reference/code-families.md) remains the full decoder ring for descriptions and
less common codes.

### Foreign-key policy

Acta enforces database foreign keys in one shape: NO ACTION from transactional tables to dimension
rows (schedules → definitions; workers → namespaces; jobs_state → lanes). Definitions and namespaces
have no delete surface, and retention deletes a lane only once no job references it.

A job's own rows carry no key to it: purge and retention delete `jobs_state`, `checkpoints`,
`steps`, `schedules`, and the job's `tags` and `alerts` with the job, and conformance checks that nothing is left.
Nor does `jobs` reference `namespaces`, `tenants`, or `definitions` by key; enqueue resolves those in
its own transaction. A key on `jobs_state` checked, on every enqueue, the new job row on the hottest page
of `jobs`, and dropping it raised SQL Server's single enqueues by about a sixth.

Never enforced, by design:

- Audit rows (`events`): audit outlives its subjects. Queries against them must use the event's own
  columns, never an inner join to a possibly-purged row.
- Ephemeral/operational references (`locks.job_id`, `jobs_state.leased_by_worker_id`): reaped by
  their own lifecycles.
- Lineage self-references (`jobs.parent_job_id`, `jobs.lineage_root_job_id`): retention deletes parents in
  retention order and dangling lineage refs are tolerated everywhere; a RESTRICT self-FK would fail
  routine retention.

Every unenforced reference must be write-time validated by its operations and covered by
conformance tests. New hot-path tables must justify any FK in review.

### Enqueue A Job

Creating work from SQL is a write, so call the installed `enqueue_job` routine instead of hand-writing
`INSERT`s. You pass the **namespace name and the job name** (the `[Job("...")]` name); the routine
resolves the definition, stamps the job ready, and writes the `jobs` and `jobs_state` rows in one
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
DECLARE @tags acta.enqueue_tags;
DECLARE @payload varbinary(max) = CAST('{"invoiceId":42}' AS varbinary(max));
EXEC acta.enqueue_job
    @p_namespace_name = 'billing',
    @p_job_name       = 'send-invoice',
    @p_input          = @payload,
    @p_tags           = @tags;
```

The optional parameters keep their column names: `p_deduplication_key`, `p_correlation_key`,
`p_job_priority_code_override`, `p_input_format_id`, `p_concurrency_key`, `p_next_run_at_utc` or
`p_delay_seconds`, `p_parent_job_id`, `p_tenant_name` with `p_tenant_override`, and `p_lane_name`.
With the default json format, `p_input` must be exactly one strict JSON value. The routine does not check it, so a
malformed row stores, fails its handler's deserialization, and reads back over HTTP as `base64` with `malformed: true`.
Postgres takes tags as the three arrays `p_tag_name`, `p_tag_value`, and `p_tag_value_search`.

The routine stores what it is given, where the API canonicalizes first. Pass every name (namespace, job, tenant, lane, tag) as it is stored, which is lowercase, and every key as the API would take it: printable ASCII with no whitespace, within its column's width, since SQL Server cuts a longer value to the width without an error, and for a deduplication key not starting with `sys.`, which the engine's own keys use. A key with whitespace or a character outside printable ASCII is stored, deduplicates, runs, and reads back as stored, but the API cannot look it up or filter by it. A key that is empty or only spaces is refused: pass `NULL` for no key. A priority must be one of the bands 0, 50, 70, 85, and 100; any other is refused, since a job between two bands would never be claimed. `p_tag_value_search` is the tag's value trimmed and upper-cased, which is what the API's tag filters compare. On PostgreSQL the two small integers need a cast, as in `p_job_priority_code_override => 70::smallint`.

SQLite has no callable enqueue routine; enqueue through the application (`IJobs.EnqueueAsync`).

### Recent Jobs

```sql
SELECT job_id, job_ref, namespace_name, definition_name, job_status, failure_count, next_run_at_utc, input_format, input_text, result_format, result_text, created_at_utc
  FROM acta.jobs_view
 WHERE namespace_name = 'billing'
 ORDER BY created_at_utc DESC
 LIMIT 50;
```

`input_text` and `result_text` decode only `json` payloads. Binary and custom
formats stay opaque: the format columns tell you why the text column is `NULL`; inspect those payloads
through application code.

### Backlog By Definition

```sql
SELECT namespace_name, definition_name, COUNT(*) AS ready_count, MIN(next_run_at_utc) AS oldest_due_at_utc
  FROM acta.jobs_view
 WHERE job_status = 'ready'
 GROUP BY namespace_name, definition_name
 ORDER BY ready_count DESC, oldest_due_at_utc;
```

Use this when work is not draining. If backlog exists but workers are alive, compare executor count,
claim batch, DB capacity, and whether jobs are due yet.

### In-Flight Jobs And Owners

```sql
SELECT job_id, job_ref, namespace_name, definition_name, job_status, leased_by_worker_id, leased_by_worker_host_name, lease_expires_at_utc
  FROM acta.jobs_view
 WHERE job_status = 'executing'
 ORDER BY lease_expires_at_utc, job_id;
```

If the worker is gone and the lease has expired, crash recovery reclaims the job on its next pass,
within one more lease.

### Event Timeline

```sql
SELECT e.event_id, e.created_at_utc, e.event, e.execution_number, e.duration_ms, e.event_message, e.actor, e.details_format, e.details_text
  FROM acta.events_view e
  JOIN acta.jobs_view j ON j.job_id = e.entity_id
 WHERE e.entity = 'job' AND j.job_ref = :job_ref
 ORDER BY e.created_at_utc, e.event_id;
```

`:job_ref` takes the form [Common queries](#common-queries) gives for each provider. The join needs the
job's row, and events outlive it: once job retention or a purge has removed the row, read the job's
remaining events by its internal id, which its event rows (`entity_id`) and its attempts' log scope
(`ActaJobId`) carry.

```sql
SELECT e.event_id, e.created_at_utc, e.event, e.execution_number, e.duration_ms, e.event_message, e.actor, e.details_format, e.details_text
  FROM acta.events_view e
 WHERE e.entity = 'job' AND e.entity_id = :job_id
 ORDER BY e.created_at_utc, e.event_id;
```

`events_view` is a readable ledger, not a rollup. The `jobs_view` row tells you where work is now;
events tell you why it moved. `actor` names who performed an operator action, and is null on what the
engine or a worker did. `details_text` decodes `json` event details, the machine-readable facts; binary
and custom details payloads stay opaque.

### Recent Terminal Failures

```sql
SELECT job_id, job_ref, namespace_name, definition_name, failure_count, modified_at_utc
  FROM acta.jobs_view
 WHERE job_status = 'failed'
   AND modified_at_utc > now() - interval '24 hours'
 ORDER BY modified_at_utc DESC;
```

Follow with the event timeline for the specific `job_ref`.

### Suspended Jobs Waiting On Signals

```sql
SELECT c.job_id, c.job_ref, c.namespace_name, c.definition_name, c.checkpoint_name AS signal_name, c.checkpoint_status, c.value_format, c.value_text, c.modified_at_utc
  FROM acta.checkpoints_view c
  JOIN acta.jobs_view j ON j.job_id = c.job_id
 WHERE j.job_status = 'suspended'
   AND c.checkpoint_kind = 'signal'
 ORDER BY c.modified_at_utc DESC;
```

Raise the matching signal with `IJobs.RaiseSignalAsync`, the CLI, or the HTTP signal endpoint.
`value_text` decodes `json` signal values; presence-only, binary, and custom
values stay opaque.

### Durable Sleeps And Timers

```sql
SELECT job_id, job_ref, namespace_name, definition_name, checkpoint_name AS timer_name, checkpoint_status, due_at_utc
  FROM acta.checkpoints_view
 WHERE checkpoint_kind = 'timer'
 ORDER BY due_at_utc, job_id;
```

Timers are job-internal checkpoints. The owning job becomes claimable when the timer is due and consumed.

### Step State

```sql
SELECT job_id, job_ref, namespace_name, definition_name, step_name, step_status, failure_message, result_format, result_text
  FROM acta.steps_view
 WHERE job_id = :job_id
 ORDER BY step_name;
```

A succeeded step replays from stored result. A step whose body throws fails the job's attempt and
stays `pending`, so the job's retry, under its own budget, runs it again; only an at-most-once step
ends `failed` or `interrupted`, with the failure on its row.

### Recurring Schedules

```sql
SELECT namespace_name, definition_name, schedule_name, schedule_status, expression, expression_kind, time_zone_id, misfire_strategy, next_occurrence_at_utc, next_evaluation_at_utc, evaluation_status, max_outstanding, outstanding_count, full_behavior, expire_after_seconds, expire_action, availability_calendar_name, start_at_utc, end_before_utc, paused_until_utc, description, pause_message
  FROM acta.schedules_view
 ORDER BY namespace_name, next_occurrence_at_utc NULLS LAST, schedule_name;
```

A schedule whose `expression_kind` is `calendar` names its calendar in `expression`, and one that fires only inside an availability calendar's windows names it in `availability_calendar_name`. The row keeps the name only; an `evaluation_status` of `evaluation-error` on such a schedule is a calendar its `schedule.evaluation-failed` event explains, and the other schedules of the namespace go on ([calendars](./scheduling.md#calendars)). The catalog itself is `calendars_view`: one row per version with its role, status, and hash.

`start_at_utc` and `end_before_utc` are the schedule's bounds, [start, end) on the occurrences' own instants; null is no bound, and a schedule past its end reads `exhausted` with no `next_occurrence_at_utc` ([finite schedules](./scheduling.md#finite-schedules)).

`expire_after_seconds` and `expire_action` are null together when occurrences never expire; a schedule paused
by its expiry carries the pause message `Paused by the expiration policy: an occurrence expired unadmitted.`
and `evaluation_status` reads `resumed` between an operator's resume and the admitter's next look.

Each schedule's cursor lives in `schedules_state`, beside its `schedules` row, and the view joins the
two. `next_occurrence_at_utc` is the next instant the schedule is due; an orphaned schedule, and every
schedule of a retired definition, has none. `next_evaluation_at_utc` and `evaluation_status` say when
and why the admitter next looks; the permanent job carries no cursor of its own and sits `idle` between firings.
`outstanding_count` is a per-occurrence schedule's roots not yet settled, bounded by `max_outstanding`,
and `full_behavior` says whether a due occurrence waits or is dropped at that bound. Each such root names
its schedule in `jobs_view.schedule_id`.

### Per-Occurrence Capacity Drift

```sql
SELECT s.namespace_name, s.definition_name, s.schedule_name, s.evaluation_status, s.outstanding_count, COUNT(j.job_id) AS live_roots
  FROM acta.schedules_view s
  LEFT JOIN acta.jobs_view j ON j.schedule_id = s.schedule_id AND j.job_status NOT IN ('succeeded', 'failed', 'cancelled')
 WHERE s.scheduled_delivery = 'per-occurrence'
 GROUP BY s.namespace_name, s.definition_name, s.schedule_name, s.evaluation_status, s.outstanding_count
HAVING s.outstanding_count <> COUNT(j.job_id)
 ORDER BY s.namespace_name, s.definition_name, s.schedule_name;
```

Every settlement path writes the count in the transaction that settles the root, so this answers no rows
unless something wrote around the engine: a manual UPDATE, a partial restore, a bug. A row whose count
sits at `max_outstanding` with fewer live roots is a schedule `waiting-for-capacity` for nothing. Run it
when a schedule looks stuck, not on a timer: it reads every live root of the namespace. There is no verb
that corrects the count; report a row as a defect with its evidence.

### Job Definitions And Effective Policy

```sql
SELECT namespace_name, definition_name, definition_status, input_type_name, input_format_name, result_type_name, result_format_name, job_priority_effective, max_attempts_effective, execution_timeout_seconds_effective, deadline_seconds_effective, retention_seconds_effective, event_recording_effective, alert_profile_effective, alert_channel_name_effective, runbook_url_effective
  FROM acta.definitions_view
 WHERE namespace_name = 'billing'
 ORDER BY definition_name;
```

Use this when a job behaves differently than expected after a deploy or operator override. Each
policy appears three times: the bare column is the default the code declares, `_override` is the
operator's edit (`NULL` when none), and `_effective` is the value that runs. The decoded columns
(`job_priority_effective`, `deadline_behavior_effective`, `event_recording_effective`,
`alert_profile_effective`) decode the effective value.

### Worker Liveness

```sql
SELECT namespace_name, worker_id, host_name, worker_status, deployment_version, max_concurrent_executors, last_seen_at_utc
  FROM acta.workers_view
 ORDER BY last_seen_at_utc DESC;
```

Crash recovery marks Active and Draining workers Dead when `last_seen_at_utc` falls past
`JobsOptions.WorkerDeadAfter`. A Dead worker that heartbeats again, after a database outage for
example, turns Active again.

### Pending Or Failed Alert Delivery

```sql
SELECT alert_id, namespace_name, job_id, job_ref, alert_severity, alert_kind, delivery_status, title, occurrence_count, retry_count, retry_after_utc
  FROM acta.alerts_view
 WHERE delivery_status IN ('pending', 'retry-after', 'failed')
 ORDER BY created_at_utc DESC;
```

Alert rows are projections of the event stream or manual handler alerts. Delivery retries are driven
by `sys.alerts`.

### Retention Candidates

```sql
SELECT namespace_name, job_status, COUNT(*) AS jobs_to_purge, MIN(retention_until_utc) AS oldest_retention_until_utc
  FROM acta.jobs_view
 WHERE retention_until_utc IS NOT NULL
   AND retention_until_utc <= now()
 GROUP BY namespace_name, job_status
 ORDER BY jobs_to_purge DESC;
```

`sys.retention` deletes expired terminal jobs and cascading substrate rows. Three kinds stay and still
count here: a stopped permanent job whose definition is live (until the definition is retired), a job
that still has children (the tree drains from the leaves), and a completed child whose parent is not
terminal. Event and alert
retention have their own windows in `JobsOptions`.

### Tags

`tags_view` decodes the tagged entity's kind as `entity` while retaining `entity_code` and `entity_id`. It
carries no namespace: join job tags to `jobs_view` for the job's namespace and runtime context:

```sql
SELECT t.entity_id AS job_id, j.job_ref, j.namespace_name, j.definition_name, j.job_status, t.tag_name, t.tag_value
  FROM acta.tags_view t
  JOIN acta.jobs_view j ON j.job_id = t.entity_id
 WHERE t.entity = 'job'
   AND j.namespace_name = 'billing'
   AND t.tag_name = 'tier'
   AND t.tag_value = 'gold';
```

### Quarantined Outbox Rows

These recipes run against the **producer** database's external-outbox table, not the `acta` ledger
schema. The default physical name is `acta_outbox` in the provider's default schema; substitute your
configured table/schema. Status codes are `10` Pending, `20` Claimed, `90` Quarantined. Only requeue or
delete rows the relay has quarantined (`outbox_status_code = 90`); leave Pending and Claimed rows to the relay.
Prefer the audited verbs, `IActaOperations.Outbox.RequeueAsync` and `DiscardAsync` or
`POST /outbox/{jobNamespace}/requeue` and `/discard`, which write `outbox.requeued` and
`outbox.discarded` events; these statements leave no audit trail.
Background: [transactional enqueue and the external outbox](./developer-guide.md#relay-behaviour).

Inspect quarantined rows:

```sql
SELECT outbox_id, namespace_name, job_name, deduplication_key, failure_count, failure_message, created_at_utc
  FROM acta_outbox
 WHERE outbox_status_code = 90
 ORDER BY created_at_utc;
```

Requeue a quarantined row: reset it to Pending, clear the failure budget, make it immediately eligible on
the database clock, and clear the claim and error fields. Requeue only after fixing the underlying cause
(for example deploying the missing job definition).

**PostgreSQL**:

```sql
UPDATE acta_outbox
   SET outbox_status_code = 10, failure_count = 0, next_attempt_at_utc = now(),
       claim_token = NULL, claim_until_utc = NULL, failure_message = NULL
 WHERE outbox_status_code = 90
   AND outbox_id = :outbox_id;
```

**SQL Server**:

```sql
UPDATE acta_outbox
   SET outbox_status_code = 10, failure_count = 0, next_attempt_at_utc = SYSUTCDATETIME(),
       claim_token = NULL, claim_until_utc = NULL, failure_message = NULL
 WHERE outbox_status_code = 90
   AND outbox_id = @outbox_id;
```

**SQLite** (its clock is already UTC):

```sql
UPDATE acta_outbox
   SET outbox_status_code = 10, failure_count = 0, next_attempt_at_utc = datetime('now'),
       claim_token = NULL, claim_until_utc = NULL, failure_message = NULL
 WHERE outbox_status_code = 90
   AND outbox_id = :outbox_id;
```

Delete a quarantined row that should never be delivered (the payload is wrong and there is no fix):

```sql
DELETE FROM acta_outbox
 WHERE outbox_status_code = 90
   AND outbox_id = :outbox_id;
```

The `outbox_status_code = 90` guard keeps a requeue or delete from racing a row the relay currently owns: a
Claimed row is finalized by token CAS, so a stale operator write against it would not match anyway, but
scoping to Quarantined makes the intent explicit and the statement idempotent.

### Identifier Casing In Raw SQL

Names (namespace, job, schedule, signal, channel, lane, tenant, rate meter) are stored lowercase: Acta
trims and lowercases them on input, so a name compares equal under any collation. Keys (deduplication,
concurrency, lock, correlation) are stored as the caller gave them, case included, with surrounding whitespace trimmed, and compared bytewise; on
SQL Server the compared key columns carry a binary collation for that, on PostgreSQL and SQLite equality
is already exact. Display text (`display_name`, `alert_message`, `actor`) is stored verbatim and never compared.

For raw SQL this means: compare a name with a lowercase literal, and supply lowercase when you insert a
name out of band, since a mixed-case name never matches Acta's equality, uniqueness, or joins; compare a
key with the exact casing the caller supplied.

```sql
-- finds the tenant regardless of how the caller originally cased the name
SELECT tenant_id, tenant_name, description FROM acta.tenants WHERE tenant_name = 'acme-corp';
```
