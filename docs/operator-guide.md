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

Every Acta host doubles as a control CLI. Pass `jobs` as the first argument and the process runs
the verb against the database, then exits: no worker loops start, no migrations run, and nothing is
written to the catalog. The schema must already exist (the worker owns migrations and definitions).
Call `j.DisableCli()` on the builder to opt out.

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

A `job_...` target resolves as a job ref, any other non-numeric target as an deduplication key, and a
bare integer as the internal job id (the advanced/debug path). The target may be omitted
(`<app> jobs debug`): the CLI then reads it from the clipboard, accepting a single-line value up to
the DeduplicationKey size (128 chars) and reporting a usage error if the id is missing. For `signal`,
a lone positional is the signal name and the target comes from the clipboard. When the process
registers more than one namespace, add `--ns <ns>` to select which namespace to search for an
deduplication-key lookup (not needed for refs or numeric ids). Add `--json` to any verb to receive a
JSON object instead of plain key-value lines.

`cancel`, `pause`, `resume`, `restart`, and `signal` correspond directly to the `IJobs` control
verbs above; the CLI is a thin shell over the same call path.

`explain` is the diagnostic verb: it reads the job's `runtimes` row, execution lease and owning
worker, step and checkpoint slots, and the latest reason on the timeline in one consistent snapshot,
then prints a plain-English account of where the work is, why it moved there, and the operator's next
action, the same durable rows you could `SELECT`, read for you. Start here for any "why is this
job stuck / waiting / failed" question before dropping to `info` (the raw row) or `events` (the full
timeline). It is read-only and backs the same `IJobs.ExplainAsync` the dashboard's Explain panel
renders.

`jobs explain` diagnoses Acta's durable state. It can tell you which durable slots completed, which
wait is active, what the latest recorded reason was, and what operator actions are available. It
cannot predict arbitrary user code, external system state, or whether a non-idempotent side effect
already happened outside Acta.

Suspended signal example:

```
job_01H8ZKX…  payments/checkout
Suspended, waiting for signal "fraud-review".

Last activity:
- Last executed on worker payments-v42 (17).

Durable work:
- Step "reserve-inventory" succeeded and will not rerun.

Next actions:
- Raise signal "fraud-review".
- Cancel the job.
```

Expired lease example:

```
job_01H8ZKX…  payments/checkout
Executing, but its lease expired 2m ago.

Worker:
- Worker payments-v17 (17), lease expired at 2026-07-04T11:58:00.0000000Z.
- Last heartbeat at 2026-07-04T11:56:00.0000000Z.
- Recovery should return it to Ready on the next maintenance tick.

Next actions:
- Wait for crash recovery to reclaim the job on its next pass.
- Cancel the job if it should not continue.
```

Failed job example:

```
job_01H8ZKX…  payments/checkout
Failed.

Reason:
- Report store unavailable.

Durable work:
- Step "reserve-stock" succeeded and will not rerun.
- Step "charge-card" exhausted after 3 attempts: provider timeout.

Next actions:
- Inspect the timeline with 'jobs events'.
- Restart the job only after the underlying cause is fixed.
```

Completed durable step example:

```
job_01H8ZKX…  payments/checkout
Succeeded.

Durable work:
- Step "reserve-stock" succeeded and will not rerun.

Next actions:
- View the result with 'jobs result' if the job stores one.
- Inspect the event timeline if needed.
```

`debug` is the exception: it initializes the worker's catalog (definitions, schedules), resets a
non-Ready job to Ready using restart semantics, claims exactly the targeted id, and runs the handler
in this process through the normal durable pipeline. Set a breakpoint in the handler and invoke
`<app> jobs debug <id>` to step through execution locally. Lease heartbeats extend throughout the
session, including during breakpoint stops. A live worker can still steal the job between the reset
and the claim; in that case the CLI reports the job was not claimable and exits 1. The exit code
reflects the handler outcome: a thrown handler exits 1 even when the retry budget re-arms the job.
An Executing job is refused. A laned job that restarts behind a running member is
reported Blocked and exits 0 without running. `--break` raises the debugger at the handler seam
instead of relying on a breakpoint; `jobs signal --value <json>` raises the signal with a payload.

Exit codes:

| Code | Meaning                              |
|------|--------------------------------------|
| 0    | Applied / found / debug run succeeded |
| 1    | Rejected or handler failed           |
| 2    | Job not found                        |
| 64   | Usage error                          |
| 130  | Cancelled (Ctrl-C)                    |

See `concepts/000-fundamentals/021-jobs-cli` for a worked example.

## HTTP API and dashboard

`IJobs` is the read-only operator surface as well as the control surface. Root reads cover jobs,
events, namespaces, and overview counters; domain reads hang off `operations.Definitions`, `operations.Schedules`,
`operations.Workers`, `operations.Alerts`, `operations.Tenants`, and `operations.Namespaces`. Every list read is keyset-paginated and answers
its own `ListXResponse`, an `IPagedResponse<T>`: pass its `NextCursor` back as `Cursor` for the next page. Cursors are opaque and
bound to the operation, ordering, and filters that issued them; a stale or foreign cursor is
rejected with `InvalidPageCursorException` rather than returning wrong pages. Page sizes default to
50 and clamp at 100; `IncludeTotal` opts into a filter-wide count (job-scoped only for events). List
reads never expose job input or result payloads; text fields return the stored value in full.

The optional `Acta.AspNetCore` package serves that surface over HTTP plus an embedded
single-page dashboard:

```csharp
app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.ConfigureEndpoints = group => group.RequireAuthorization("ActaOperators");
});
```

The dashboard has a top-level Events page for the latest retained audit timeline, optionally scoped
by namespace; the JSON endpoint is `GET /acta/api/v1/events?jobNamespace=...`. Jobs are addressed
in the URL by their public ref (`GET /acta/api/v1/jobs/{jobRef}`, `/jobs/{jobRef}/events`,
`POST /jobs/{jobRef}/{verb}`); the numeric id never appears in a route or in the JSON. A job can
also be looked up by deduplication key (`GET /jobs/by-key?jobNamespace=&deduplicationKey=`).
For the rare debug case where you have only an internal id (from a log line), set
`EnableNumericIdLookup = true` and address the read endpoints as `/jobs/id:{n}` (for example
`/jobs/id:12345`); off by default, an `id:` target answers 400 like any malformed ref, so numeric
ids never become a second default identity. The dashboard's jump box accepts the same three forms:
a `job_...` ref, an deduplication key, or `id:123` / `#123`; the control POSTs stay ref-only.

The API can also map the job-control verbs as POST endpoints (`/jobs/{jobRef}/pause`, `resume`,
`restart`, `cancel`, `reschedule`, `reprioritize`, `purge`, and `input` for the amend), thin wrappers over the `IJobs` verbs above: the framework still stamps actor
and reason code. The non-purge verbs accept an optional `reasonMessage`; purge retains no caller reason because it removes the job's
event history. The outcome maps to 200
(applied), 409 (rejected), or 404 (not found) — and that shape is the whole API's rule, not this
family's: every control family answers 200, 404 and 409 (202 for the accepted-then-applied outbox
verbs) with its own envelope, and `ProblemDetails` is reserved for malformed input, authorization,
and server faults. The one deliberate exception is the enqueue guard's 409: `EnqueueJobAction` has no
rejected value and a refused enqueue mints no job ref for the envelope to name, so that rejection
stays a problem document with its `reasonCode`. Controls are
opt-in (`EnableControls = true`)
because they mutate jobs; enable them alongside your authorization, never on an open surface.
Unmapped controls answer 404. Control requests must send the `X-Acta-Control: true` header,
the API's barrier against cross-site request forgery (a page on another origin cannot send it
without a CORS preflight), not authentication; the dashboard sends it automatically. A host that turns
it off needs antiforgery of its own. Body members are
camelCase and exact: a member the endpoint does not take answers 400 with its path, so a misspelled
scope never widens a verb to its default. The job detail
screen surfaces all seven actions with state-aware availability and confirmation for destructive
changes. Explain links directly to the applicable signal/control/timeline action and shows the
durable evidence behind its recommendation. The dashboard also exposes schedule
pause/resume/trigger/preview/overrides, alert acknowledge/resolve, and tenant/namespace
administration when controls are enabled. The CLI above intentionally has a smaller verb set.

Signals are also raisable over HTTP at `POST /jobs/{jobRef}/signals/{name}`, the inbound counterpart
to `IJobs.RaiseSignalAsync`. Signals are operator control: the endpoint is mapped only when
`EnableControls = true` and, like the destructive verbs, requires the `X-Acta-Control: true`
confirmation header (missing or wrong header answers 400). An empty body raises a presence-only
signal; a non-empty `application/json` body is stored verbatim and a handler reads it via
`ctx.WaitSignalAsync<T>(name)`. A signal name is validated as a name at the edge, with the `sys.`
prefix reserved, which is what rejects forged internal names such as the `sys.child.` child-latch
names. The outcome maps to 200, 409 (terminal job), or 404.

A read-side `GET /capabilities` reports `{ controlsEnabled, version, provider, schema, confirmationHeader }`
so dashboards can show or hide edit UI without probing a control route; it is always mapped and
never gated.

The surface is local-only by default (`LocalOnly = true`): requests from non-loopback remote
addresses are rejected with 403, so the dashboard works with zero setup from the same machine. A
local request must also name the host as `localhost` or an IP address, which shuts out a web page
that rebinds its own DNS name to 127.0.0.1; browse to `localhost` or `127.0.0.1`, not the machine
name. The
package ships no authentication; exposing the surface remotely means setting `LocalOnly = false` and
wiring host authorization through `ConfigureEndpoints`, which covers the HTML, the hashed assets,
the query API, and the control API together. Mapping fails closed: `LocalOnly = false` without
`ConfigureEndpoints` throws at startup unless the host explicitly opts into anonymous exposure with
`UnsafeAllowAnonymousRemoteAccess = true`. CORS is not authorization and plays no part in this
guard; it never prevents direct access to the URL. `MapActaApi` maps the JSON endpoints alone
and carries the same local-only default. The dashboard works under any base path.

Payload bodies are part of the read surface, not a gated extra. Anyone who can reach the reads can
see a job's input, its result, and its checkpoint values in full: json and text render inline, and
any other format renders as a hex preview with base64 copy and a file download. The read surface is
mapped regardless of `EnableControls` and never passes through `IActaControlAuthorizer`, so the only
thing that withholds a body is size: past `JobsOptions.MaxInlinePayloadBytes` the read ships the
format identity and byte length with `truncated: true` and no body. With controls enabled, a json or
text input is also editable in place unless the job is executing. Treat authorization
for the read surface as authorization to read every payload the ledger holds. See
`concepts/000-fundamentals/022-dashboard` for a runnable host.

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

Every terminal landing stamps `retention_until_utc` from the definition's `JobRetention` policy
(default 90 days); the framework `sys.retention` job purges terminal rows past that deadline in
batches, along with `events` / `alerts` / terminal-worker rows past their `JobsOptions` windows
(`JobEventsRetention`, `AlertRetention`, `WorkerRetention`). No index finds expired jobs: each hourly
tick walks `runtimes` down by job id from where the last tick stopped (its `purge-cursor` job
variable), up to two million rows, and deletes the namespace's expired jobs it passes, so a job goes
within one walk of its deadline rather than at it. A row with a NULL
`retention_until_utc` is never purged; only terminal rows ever carry one. Substrate rows
(checkpoints, steps, results) delete with their job.

A running recurring slot is never terminal, so retention never reaches it. One a cancel or a failure
stopped stays as it was left while its definition is live, because deleting it would let the next
start register the declaration as a new job; it drains once the definition is retired. Its result
rows are bounded instead by the definition's `RecurringResultCap`, which defaults to 1: every
recurring completion trims that job's results to the newest N in the same statement. Raise the cap
on the definition to keep a deeper history; there is no window in which an uncapped recurring slot
grows without bound.

Retention purge and manual purge are different operations with different guards. The `sys.retention`
sweep is deadline-driven: it deletes terminal rows past `retention_until_utc` in batches and emits no
per-job event. The manual verb (`IJobs.PurgeAsync`, `POST /jobs/{jobRef}/purge`, or the confirmed
dashboard action) hard-deletes one terminal job immediately, refuses a job that has child jobs (purging the
parent would orphan the children's lineage: purge the children first), and always emits a
`job.purged` audit event carrying the purged job's ref and name, since the row itself is gone.
Manual purge also explicitly deletes the job's `events` and `alerts` rows (both are FK-less, so
nothing cascades), scrubbing the job completely. Retention purge is more conservative: it deletes
only the terminal `jobs` row (S1) on its own deadline; alerts age out separately on
`AlertRetention` (S3), so an alert row (which keeps its own copy of the job ref) can outlive
the job it was raised for.

`AlertRetention` is a hard cap, not a settled-only window: past it an alert row is deleted whether
delivery settled or not, so a row stuck `Pending` or `RetryAfter` — or an incident still open — is
never immortal. Deleting an open incident frees its deduplication identity, so a still-failing job
opens a fresh incident on its next failure. The undelivered rows are counted apart from the settled
ones and a pass that purged any logs a single warning (`reason=alert-retention-cap`), because an
alert aged out before it ever reached a channel is a signal nobody received: if that line appears,
look at why delivery was not settling rather than at the purge. The same window prunes the
`alerts-skip-*` poison variables `sys.alerts` leaves on its own slot; nothing else pruned them, and
they are forensics the projector never reads back.

At a glance:

| | Retention sweep | Manual purge |
| --- | --- | --- |
| Trigger | `sys.retention` finds terminal jobs past `retention_until_utc` | Operator calls `IJobs.PurgeAsync` or the enabled HTTP/dashboard control |
| Eligible job | Terminal and past its retention deadline, unless it is a stopped recurring job whose definition is live | Terminal now |
| Child-job guard | Keeps a completed child while its parent is live; a terminal tree drains leaves first | Rejects a parent that still has child jobs and a child whose parent is live; finish the tree, then purge the leaves first |
| Job event | No per-job purge event | Emits `job.purged` after removing the job history |
| Existing job events | Age out on their own event-retention window | Deleted immediately for that job |
| Existing alerts | Age out on their own alert-retention window | Deleted immediately for that job |
| Intended use | Routine bounded storage | Immediate removal of one known terminal job |

Before a manual purge: confirm the job is terminal, has no child jobs, and has no live parent; copy any incident summary
or required audit evidence; confirm no caller still needs `GetResultAsync`; record a reason that
explains the administrative decision; remember that purge removes Acta state only (related business
data and database backups are outside its reach). Do not use manual purge as backlog control:
pausing, cancelling, rescheduling, or fixing worker capacity addresses runnable work without
deleting evidence.

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

Use SQLite when the database is embedded in one process or one node. SQLite is useful for concepts,
local exploration, tests, and small single-node tools, but it has concurrency limits compared with
server databases.

Redis wakeup is optional. SQL polling is the correctness baseline; a wakeup transport only lowers
pickup latency.

### Migration ownership

Acta schema migrations are durable DDL and should be owned by deployment, not by every application
boot. In production:

```csharp
j.UsePostgres(pg =>
{
    pg.ConnectionString = builder.Configuration.GetConnectionString("acta")!;
    pg.Schema = "acta";
    pg.ApplyMigrationsOnStartup = false;
});
```

Run migration SQL from your release pipeline or database migration process, then start workers.
`ApplyMigrationsOnStartup = true` is a development/sample convenience.

For pipelines and DBAs that run reviewed SQL rather than application code, the repository publishes
complete provisioning scripts at
[`schema-pg.sql`](./reference/schema-pg.sql), [`schema-mssql.sql`](./reference/schema-mssql.sql),
[`schema-sqlite.sql`](./reference/schema-sqlite.sql): one file per provider carrying the migration
history and every migration, routines and views inside them, ready to review and run under a
DDL-capable principal (replace the default schema name throughout to relocate). With the database
provisioned that way, the application principal needs only DML and EXECUTE on the Acta schema and
never issues DDL. The scripts record the migration history themselves, so a bootstrap accepts the
result and applies nothing; advanced deployments can even add site-specific physical tuning
(partitioning, tablespaces) as long as the logical shape stays intact. The SQLite script opens with
`PRAGMA journal_mode = WAL;` for the same reason the SQL Server one sets READ_COMMITTED_SNAPSHOT
below — the certified configuration should not depend on which path provisioned the database — and
because journal mode lives in the file header, running it a second time changes nothing.

The SQL Server script sets READ_COMMITTED_SNAPSHOT itself, the way a self-applying bootstrap does.
It is the file's first batch, on its own outside the transaction because `ALTER DATABASE` cannot run
inside one, and it is guarded on `sys.databases.is_read_committed_snapshot_on`: a database that
already has it on skips the statement, so install and re-run behave the same. When it does have to
run it disconnects the database's other sessions (`WITH ROLLBACK IMMEDIATE`), which is one more
reason to run the file in a maintenance window. Job correctness does not depend on it — Acta's
mutations are fenced by lock hints and compare-and-swap, not snapshots — but without it the
dashboard's and operators' read queries take shared locks and trade blocking with the claim path,
which is not the configuration Acta certifies.

Migrations run under an exclusive schema lock in one transaction, so applying
a later migration on a live system briefly blocks statements that collide with the objects it touches
and resumes them when it commits; in-flight jobs are not failed by a migration, they wait it out.

Whether or not a host applies migrations itself, every start runs a read-only migration-history
preflight: the database must carry a history row for every migration this build ships, under the
name this build ships it. A database that was never provisioned fails at `StartAsync` with
instructions instead of failing later at its first query. History rows this build has never heard of are fine — an older worker against a
newer database is a supported rolling-upgrade shape.

Routine and view bodies travel inside migrations, after the tables of the migration that changed
them, so the history rows say exactly which bodies a database holds: there is no separate install
step and nothing at startup rewrites a body on its own. Running the current full provisioning script
as part of every upgrade is what keeps bodies and binary together; it applies only the migrations the
database lacks, so re-running it is always safe. Run it with a client that stops at the first error
(`psql -v ON_ERROR_STOP=1`, `sqlcmd -b`, `sqlite3 -bail`): a client that runs on past a failed
statement would record a migration whose bodies did not all land. Stopping at the error is what turns
the script's transaction into the guarantee.

Know what this still does not prove. A history row records that a migration ran, not the bodies now
in the database, so an operator who hand-edits an object keeps a row that no longer describes it.
The preflight also reads the database once at startup, so a worker already running when someone
applies an incompatible change finds out at its next affected call.

See [schema migrations](../CONTRIBUTING.md#schema-migrations) for the migration model and `tools/Acta.Emit`
commands.

### Rolling deploys

Two builds of the application share a namespace while a deploy is in progress: the old build keeps
working while the new one starts, and the old one stops once the new one is up. A rollback is the
same picture in reverse. Acta needs nothing from the orchestrator beyond that order.

What holds while both builds run:

- Claims are by namespace, so either build can claim a job of a definition only the other carries.
  The worker without the handler hands the job back to `Ready` one `SafetyPollInterval` later with
  no failure charged, and stops claiming that definition for the rest of its process life. Every row
  of the batch it already claimed is handed back the same way; only the first hand-back logs a
  warning. A worker with the handler picks the job up next.
- Registration never retires. A definition the new build does not carry stays `Active` with its
  jobs, so the old fleet finishes them and a rollback finds nothing missing. Policy still moves only
  forward: an older `ManifestGenerationUtc` cannot overwrite a newer build's definition row.
- Schedules follow the last build that registered. A schedule the new build no longer declares is
  orphaned when the new build registers and comes back when a build that declares it registers
  again, which is what a rollback does. An old worker that starts while the rollout is under way
  registers too, so a schedule the new build added reads Orphaned until the next new worker starts.
  Orphaning drops an operator's pause on the schedule, so one that comes back comes back Active;
  pause the job rather than the schedule to hold it across a rollback.
- Migrations are forward-only and additive, so an older worker runs against a newer schema. Apply
  the migration before the new build starts.
- Nothing older overwrites a database a newer build upgraded. An older build's bootstrap applies only
  the migrations the database lacks, and an older release's provisioning script stops before
  changing anything, so a rollback never rewrites what the newer build calls. On SQLite the script
  stops only in a client that halts on an error, such as `sqlite3 -bail`.

Deploy: apply migrations, start the new workers, stop the old ones once the new ones are healthy.
Rollback: start the old build, stop the new one. Jobs of a definition only the withdrawn build
carried stay `Ready`, excluded from every running worker's claims; they run when a build with the
handler returns, or an operator retires the definition.

Retiring a definition is an operator decision: `IActaOperations.Definitions.RetireAsync`,
`POST /definitions/{namespace}/{name}/retire`, or the button on the dashboard definition page. It
marks the definition `Retired`, cancels its parked (`Ready`, `Blocked`, `Suspended`, `Paused`) jobs with reason
`job.definition-retired`, releases a parent waiting on a cancelled child, leaves executing jobs to
finish their attempt, and rejects new enqueues. Descendants of a cancelled job are not cancelled. A
build that carries the handler at an equal or newer manifest generation re-activates the definition
when it registers; an older build cannot. A retire only holds once no running or restartable worker
carries the definition: during a rolling deploy, an old-build worker that restarts registers the
definition at its own generation, which equals the stored one, and makes it Active again. Retire
after the old fleet is gone.

One cost to know: while a worker's exclusion set is non-empty, each of its claims looks up
`jobs.definition_id` for every excluded row ahead of the first claimable one. A long run of jobs no
running worker can execute, such as a rollback after many enqueues of a new definition, makes every
old worker's claim walk it. Retire the definition, or redeploy the build that carries it.

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

The dashboard and JSON API are local-only by default. `LocalOnly = true` rejects non-loopback remote
requests with 403, and a loopback request too unless it names the host as `localhost`, a `.localhost`
name, or an IP address: browse to `localhost` or `127.0.0.1`, not the machine name.

Acta ships no login system. The host application owns authentication and authorization. To expose
the dashboard/API remotely, set `LocalOnly = false` and require authorization through
`ConfigureEndpoints`.

Use the normal ASP.NET Core authentication/authorization middleware for your host, for example
`app.UseAuthentication(); app.UseAuthorization();` when your authentication scheme requires it.

Controls are opt-in. `EnableControls = true` maps mutating HTTP endpoints such as pause, resume,
restart, cancel, and signal. Enable controls only behind authorization.

Operator dashboard with controls:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ActaOperators", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("ActaOperator");
    });
});

app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.EnableControls = true;
    options.ConfigureEndpoints = group =>
        group.RequireAuthorization("ActaOperators");
});
```

Read-only remote dashboard:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ActaReaders", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("ActaReader");
    });
});

app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.EnableControls = false;
    options.ConfigureEndpoints = group =>
        group.RequireAuthorization("ActaReaders");
});
```

Behind a reverse proxy on the same host, do not rely on `LocalOnly`: it checks the TCP peer address
and the host name the request carries, not the original client, so the proxy's loopback address
passes every forwarded request that keeps a local host name. Gate remote access with host
authorization, and configure `ForwardedHeaders` if the host needs the true client IP.

The `X-Acta-Control: true` header on control POSTs is the cross-site request forgery barrier, not
authentication: a cross-origin page cannot send it without a preflight. Keep it on, unless the host
supplies antiforgery of its own and no ambient credentials (integrated Windows auth, `SameSite=None`
cookies) reach the API.

Anyone authorized for the read surface can read every job input, result, and checkpoint value in
full; there is no per-payload redaction. Treat read authorization as the data-classification
boundary, and keep secrets and PII out of payloads (see [Payload size guidance](#payload-size-guidance)).

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

Terminal jobs receive `retention_until_utc` from the job definition's `JobRetention` policy. The
system `sys.retention` job purges terminal rows past that deadline and also purges old event,
alert, and terminal (Stopped or Dead) worker rows according to:

- `JobEventsRetention`
- `AlertRetention`
- `WorkerRetention`

Set retention long enough for audit, incident response, delayed restarts, and operational analysis.
Shorter retention reduces storage growth but removes old job inputs, results, checkpoints, steps,
and lineage. A completed child outlives its deadline while its parent is not terminal, and a
stopped recurring job while its definition is live; the rules, and how the sweep differs from a
manual purge, are in [Operator guide § retention and purge](#retention-and-purge).

### Process behavior on failure

What an operator should expect to see, per surface, when something goes wrong, and whether the
process degrades in place or exits.

| Surface | Failure shows as | Process behavior | Operator action |
| --- | --- | --- | --- |
| Worker startup | Any init-time validation failure (invalid `JobsOptions`, clock skew past the fail threshold, an invalid `Backoff` DSL, an unresolvable schedule time zone, payload-contract drift or alert-routing misconfiguration under `Fail` mode) throws out of `IHostedService.StartAsync` with no catch. | Fails to start. Non-zero process exit; no partial startup. | Fix the configuration or manifest and restart. A `*Mode`/`Allow*` option (`PayloadContractDriftMode`, `AlertChannelValidationMode`, `AllowClockSkew`) can deliberately downgrade a check to `LogWarning` + continue instead of failing startup. |
| Claimer / executor / finalizer / heartbeat loops | A per-tick fault logs at Error under the `Acta.WorkerRuntime` category, e.g. `"claim iteration failed; backing off {DurationMs}ms before retry."`, `"heartbeat tick failed; retrying next tick."`. A per-job fault logs `"executor faulted on job {JobId}."`, and a fault in the finalizer `"finalizer faulted on {Count} completions."`; a completion whose row another actor took logs `"completion skipped"` at Information, and a wake or follow-up that fails after the write landed logs `"follow-ups for job {JobId} failed after its completion landed"`. | Degrades. The failing loop backs off (claim: a full `SafetyPollInterval`; heartbeat: the next `PeriodicTimer` tick, which also carries the definition-policy reload) and keeps running; one bad job never tears down the loop. Shutdown (host cancellation) exits the loop cleanly with no error log (a provider that reports the cancelled command as a non-cancellation error may emit one final tick-failure line). | Investigate the logged exception (commonly a transient DB outage or command timeout); no restart needed; the loop self-heals once the dependency recovers. |
| Framework jobs (`sys.retention`, `sys.alerts`) | Alert delivery transport faults log at Warning under the `AlertsJob` category (`"ACTA sys.alerts: transport kind ({Detail}) threw delivering alert ({Ref}); will retry."`) and retry on a DB-backed backoff curve, terminal `Failed` past the fifth delivery attempt. `RecoveryJob`/`RetentionJob` carry no catch of their own; a pass failure is an ordinary job outcome retried by the executor on the job's next scheduled tick. | Degrades. Each is a bounded recurring job (about once a minute or once an hour), never a hot loop. | Usually none; a persistent failure shows up as retries/failures and events on the system job's own row. |
| CLI mode | A rejected verb prints its message to stderr and exits 1; job-not-found exits 2; a usage error exits 64; Ctrl-C during a verb exits 130. `jobs debug`'s lease-heartbeat pump writes one diagnostic line to stderr on a failed tick (`"debug: heartbeat pump tick failed (...); lease may lapse."`) and keeps pumping. | Always exits; the CLI is a one-shot verb, never a long-running host. | Read the exit code and any stderr line, then retry the verb. A missed heartbeat-pump tick during `debug` only risks the job's lease being stolen by another worker, not data loss. |
| Dashboard / API | An unhandled route exception logs at Error under the literal category `Acta.AspNetCore` (`"Unhandled Acta API exception."`) and returns a fixed, safe ProblemDetails (no driver text or stack): 503 when the cause chain is the known transient family (database/network/timeout, including a provider command timeout surfacing as a non-abort cancellation), 500 for any other server fault. A client disconnect or host shutdown mid-request (`HttpContext.RequestAborted` cancelled) produces no log and no 503: the framework reports the aborted request instead. Known input errors (invalid cursor, malformed query parameters, most `ArgumentException`s) map to 400 via the read-endpoint `Guard`; a server-side `ArgumentException` also logs at Warning before returning 400 so it still leaves a trace. | Degrades. The dashboard process stays up; one bad or aborted request never takes down the host. | A sustained run of 503s with `"database is unreachable"` in the response detail means the database is down; otherwise check the host log for the exception logged at that timestamp. |

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

How a job failure becomes an alert row, how that row reaches a channel, and what an operator
configures. The organising principle is that **alerting works the same for every job**: one
`AlertProfile` means one thing whatever the job is, and an alert's lifecycle does not depend on how
the job was started. Two places today's behaviour does not honour that principle, and both are named
in [Where the principle breaks](#where-the-principle-breaks) rather than smoothed over.

### One recurring job does all of it

`sys.alerts` is a framework recurring job registered per worker namespace: `Priority = Critical`,
`AuditLevel = Failures`, `AlertProfile = SysCritical`, `Cron.EveryMinute`
(`AlertsJob.Handle`), competitively claimed once per namespace (`AlertsJob`). One pass is
generate, then deliver, in that order (`AlertsJob.Handle`).

Four consequences an operator sees:

- **Alerts are not written at failure time.** The row appears on the next tick that finds the
  event past the projection horizon: generate only reads events older than twice the provider
  `CommandTimeout` (one minute on defaults). An event id can commit out of order, and the horizon
  is sized so that a writer which abandons its statement at its own command timeout has either
  committed or given up before the projector walks past that point. The horizon is a heuristic on
  that behaviour, not a proof of it. A command timeout is the client giving up its wait, not the
  server aborting the transaction, so a writer host that freezes can still commit after the horizon
  has passed its event, and that event is then skipped;
  [known limitations](./known-limitations.md) covers what that costs. Where writers do
  honour their own timeouts, the worst case on defaults is about two minutes from failure to row:
  up to a minute maturing past the horizon, plus the tick cadence. The horizon scales with
  `CommandTimeout` if you raise it.
- **Generate and deliver share a pass**, so a row raised in a pass is normally sent in the same pass.
- **Generate drains up to 10,240 events per pass; deliver stays at 256.** Generate reads batches of
  256 in an inner loop bounded by 40 batches or 30 seconds of elapsed time, whichever comes first
  (`AlertsJob.GenerateMaxBatches`, `AlertsJob.GenerateTimeBudget`, `AlertsJob.GenerateAsync`), so a
  burst of a few thousand failures clears in one tick. The time budget is checked *between* batches,
  so the batch in flight always finishes. Deliver is deliberately not drained the same way and stays
  at 256 transport attempts per pass (`AlertDelivery.DeliverBatchSize`): pushing ten thousand webhooks
  through one tick would trade a database problem for an outage on the operator's own channel. A
  namespace that outruns either bound falls behind. Falling behind is lag, not loss, for two
  reasons that guard two different failure shapes: the projection horizon keeps generate from
  walking past an event still waiting on an open transaction, and the durable cursor — written
  after **every** completed batch — keeps everything a pass already projected when that pass is cut
  short by a bound, a crash, or the framework's 300 s execution timeout. The lag grows until the
  burst clears. Neither guard covers the two skips named in
  [known limitations](./known-limitations.md), a writer that commits past the horizon
  and a database clock that steps backwards; both cost a missed alert, never a wrong ledger.
- **Generate walks `events` forward from a durable cursor** kept on the slot's own variable bag
  (`AlertsJob.CursorVariableName`, `AlertsJob.GenerateAsync`). The cursor is the pair
  `(created_at_utc, id)` in one variable, never the id alone: the stamp is set inside the writing
  transaction and the id at insert, so a slow writer commits a low stamp under a high id, and a
  cursor keyed on id would step over its neighbour for good. One variable, because two writers can
  overlap when a pass outlives its lease, and a whole older checkpoint only replays where mixed
  halves would skip. A short batch is the proof that nothing alertable is left behind the horizon,
  so it advances the cursor instant to the horizon and the next pass never rescans the history this
  one walked. The walk assumes the database clock does not step backwards; what a step costs and how
  an operator rewinds the cursor by hand are in
  [known limitations](./known-limitations.md).

A deterministic poison event — a channel name or deduplication key that fails canonicalization, or a
job row purged between the event write and its projection — is recorded as a durable variable on the
projector job and skipped, so one malformed event cannot wedge the cursor
(`AlertsJob.GenerateAsync`, `AlertsJob.RecordProjectionSkipAsync`, `AlertsJob.EmitAsync`). A transient
failure retains the cursor and retries the pass. Those variables are forensics, never read back, so
`sys.retention` prunes them off the projector's slot once they are older than `AlertRetention`.

### What projects an alert

Only `job.execution-finished` events, and only four shapes of them (`GetAlertableEvents.sql`):

| Event shape | `to_status` | Reason code | Treated as |
| --- | --- | --- | --- |
| Terminal failure | `Failed` (200) | any | Terminal failure |
| Re-arm after a failed attempt | `Ready` (10) | `job.unhandled-exception` (20), `job.lease-expired` (21), `job.execution-timeout` (22) | Non-terminal failure |
| Reclaim's uncharged arm, a lease lost while an expired wait was resolving | `Suspended` (20) | `job.lease-expired` (21) | Non-terminal failure |
| Successful execution | any | any | Resolution |

All of this rides the event stream, so the job's `AuditLevel` decides whether alerting can see the job
at all. The default `Audit` writes every `job.execution-finished`. **`AuditLevel = Failures` writes it
for a failed attempt that carries no reschedule** (`CompleteExecutions.*.sql`): not a success,
not an attempt the job is being re-armed from, and not a handler that cancelled or paused its own
job. Two things follow, and the first one surprises people.

A one-shot job's retries are invisible at this level. An in-budget retry re-arm is a reschedule as
far as completion is concerned, so a job that throws four times and succeeds on the fifth writes
nothing at all, and one that throws until its budget is gone writes a single terminal `Failed`,
which raises `FinalFailure`. `AlertProfile.FirstFailure` and `AlertProfile.ThresholdReached` never
fire for a one-shot's own throws here. Two shapes do reach alerting: a **recurring** slot's failed
fire carries no reschedule, so its `Ready` rollover under `job.unhandled-exception` is written and
raises `FirstFailure`, which is the unwatched-nightly-job case working as intended; and
`reclaim_stuck_jobs` writes its lease-expiry event at `Failures` as well as at `Audit`, so a worker
that dies holding a job is heard. Everything else stays quiet: a handler that sleeps, waits on a
signal, or reschedules itself, and a handler that cancels or pauses its own job.

A success at `Failures` is written when it answers a recorded failure: completion reads the job's
newest `job.execution-finished` row and writes the success only when that row is not itself a
success. So a job that never failed writes nothing, a healthy recurring slot writes nothing
occurrence after occurrence, and the one success that follows a recorded failure is on the stream
for the projector to resolve the incident from. That covers a `FinalFailure` for a job someone later
restarts to success, a recurring slot's `FirstFailure` once the next fire succeeds, and `sys.alerts`'
own incident, since it runs at `Failures` itself. The evidence is the failure event, so
`AlertRetention` may not exceed `JobEventsRetention`; startup refuses the pair otherwise.

Everything else is invisible to alerting. Two exclusions are worth stating outright:

- **A `Cancelled` landing never alerts, under any profile.** An operator cancel, `ctx.CancelAsync`
  (`HandlerInvocation`), and a whole-job deadline — both the "overdue at admission" and the
  "next retry would exceed the deadline" paths (`ExecutionAdmission`, `WorkerFinalizer`) — all land the job
  `Cancelled`, and the alertable set has no `Cancelled` branch.
- **Not every re-arm alerts.** A budget-neutral re-arm carrying `job.concurrency-key-held` (62),
  `job.rate-limited` (66), `job.step-retry-scheduled` (61), `job.attempt-aborted` (25), or
  `job.unclassified` (10, an admission that failed on a provider error) falls
  outside the three reason codes above, so the attempt ends without producing an alert even though it
  did not succeed (`EventReasonCode.JobConcurrencyKeyHeld`, `EventReasonCode.JobRateLimited`,
  `EventReasonCode.JobStepRetryScheduled`, `EventReasonCode.JobAttemptAborted`).

A recurring slot's rollover carries the failed attempt's reason through onto its `Ready` event
(`WorkerFinalizer.ComputeRecurringOutcome`). That is the only reason a nightly job that throws every
night is visible to the projector at all.

### Profiles

`JobAttribute.AlertProfile` defaults to `OnFailure` (`JobAttribute.AlertProfile`), so a job that
declares nothing about alerting is opted in.

| Profile | Non-terminal failure | Terminal `Failed` | Success |
| --- | --- | --- | --- |
| `None` | nothing | nothing | nothing |
| `OnFailure` (default) | `FirstFailure` at `Warning`; `ThresholdReached` at `Error` on the Nth | `FinalFailure` at `Error` | resolves |
| `Info` | nothing | `FinalFailure` at `Info` | resolves |
| `OnTerminal` | nothing | `FinalFailure` at `Error` | resolves |
| `SysCritical` | `FirstFailure` at `Critical`; `ThresholdReached` at `Critical` | `FinalFailure` at `Critical` | resolves |

The branches are one method: `None` returns immediately (`AlertsJob.ProjectAsync`); a success resolves
and writes no alert of its own (`AlertsJob.ProjectAsync`); `to_status = Failed` emits `FinalFailure` for
every remaining profile (`AlertsJob.ProjectAsync`); anything else is a non-terminal failure and only
`OnFailure` and `SysCritical` continue past the gate (`AlertsJob.ProjectAsync`).

`SysCritical` is reserved for framework jobs. It raises severity and nothing else: routing is uniform
across every profile — the declared `AlertChannelName`, else the configured `default` channel — so
system-job failures reach the logs with no operator configuration (`AlertsJob.ProjectAsync`).

`None` suppresses automatic alerts only. `ctx.AlertAsync` still writes rows from a `None` job
(`AlertProfileCode.None`).

### Identity and deduplication

An automatic alert's deduplication key is

```
auto:{DefinitionId}:{JobId}:{kind}:{reasonCode ?? "none"}
```

(`AlertsJob.EmitAsync`). Retention applies to incidents like everything else: a row older than
`AlertRetention` is purged even while unresolved, so a condition that has been failing for the
whole window reopens as a fresh incident counting from one — the ledger's events, not the alert
row, are the long-horizon record. This key names an **incident**, not a time bucket: `alerts` carries a unique
index on `(namespace_id, deduplication_key)` filtered to unresolved rows, so at most one OPEN row exists per
key at any moment (`AlertEntity.cs`). A repeat of the same condition while that row is open folds onto it
— `occurrence_count + 1`, title/message/severity refreshed — with no time component at all
(`RaiseAlert.*.sql`). `resolved_at_utc` is terminal for a row (see [Resolution](#resolution)):
once a success stamps it, the row never reopens, and the next failure on the same key opens a fresh
incident with a fresh ref and fresh delivery state.

Four things follow from the key's shape:

- **Job-scoped.** A fan-out of sibling jobs of one definition each gets its own row, and a success
  resolves only that job's failures.
- **Kind-scoped.** `FirstFailure`, `ThresholdReached`, and `FinalFailure` are separate rows, not
  states of one row.
- **Reason-scoped.** A job alternating between `job.unhandled-exception` and `job.lease-expired`
  opens a separate incident for each reason; both can be open at once.
- **Stable across occurrences of a recurring job.** A recurring schedule is one job row re-armed, not
  a row per occurrence ([concepts](./guide.md#concepts-and-glossary), glossary and "Schedules, workers, and providers"),
  so the job id — and therefore the alert identity — does not move from occurrence to occurrence.

Automatic writes are replay-safe. Each carries the projecting event's id, and the upsert increments and
re-stamps only when that id is strictly newer than the row's `last_projected_event_id`
(`AlertsJob.EmitAsync`, `RaiseAlert.*.sql`). Because resolution is terminal, a failure event
replayed after the incident it belongs to has already closed must not reopen it: the insert arm is
guarded against every row of the identity, resolved ones included, not just the open one, so a raise
whose event the identity has already absorbed at or past that id opens nothing — the ghost-incident
guard (`RaiseAlert.*.sql`). The public `alert_ref` is applied on the INSERT arm only, and is
absent from the `DO UPDATE SET` list (`RaiseAlert.*.sql`), so a row keeps the ref its first
firing minted for as long as the incident stays open, and the next incident on the same key gets a ref
of its own.

### Resolution

A successful execution resolves that job's open automatic alerts and writes nothing
(`AlertsJob.ProjectAsync`, `ResolveAlerts.*.sql`). It is:

- **Job-instance-scoped** — `job_id` and `origin = Automatic`, kinds `FirstFailure` /
  `ThresholdReached` / `FinalFailure`, and only rows still unresolved (`ResolveAlerts.*.sql`).
- **Ordered.** Resolution applies only to alerts whose `last_projected_event_id` precedes the success
  event (`ResolveAlerts.*.sql`), so a replayed success cannot close an alert that a later failure
  opened.
- **Idempotent, and run on every success.** Within one batch a job can go fail → success → fail →
  success, where the second failure opens a fresh incident on the same key (the first is already
  resolved); resolving each success is what keeps that second incident from lingering unresolved
  (`AlertsJob.ProjectAsync`).
- **Dependent on the success event existing.** `AuditLevel = Failures` writes a success only when
  it answers a recorded failure (see [What projects an alert](#what-projects-an-alert)), which is
  exactly the success this path needs; the healthy successes it drops had nothing to resolve.

Manual alerts are a separate path throughout — separate by key, not by wall. `ctx.AlertAsync`
writes `origin = Manual`, `kind = Manual` (`JobContext.AlertAsync`,
`RaiseAlertCommand.Manual`), which the automatic resolve never matches. A caller that
deliberately reuses an automatic `auto:` key merges its raise onto that incident and takes over
its lifecycle; Acta does not police key choice, it deduplicates on it. The operator verbs `IAlerts.AcknowledgeAsync` and `IAlerts.ResolveAsync`
(`IAlerts.AcknowledgeAsync`, `IAlerts.ResolveAsync`) are idempotent, emit `alert.acknowledged` (140) /
`alert.resolved` (141) regardless of the job's audit level (`EventCode.AlertAcknowledged`,
`EventCode.AlertResolved`), and leave `last_projected_event_id` untouched
(`ResolveAlert.*.sql`). Neither verb changes the underlying job.

Reminders follow the same split. A manual alert that delivers is notified once and never again: Acta
has no way to tell whether one handler's statement still holds, so it schedules no reminder and the
caller owns resolving the row (`AlertDelivery.SettleAsync`). A manual alert whose *send* failed is the
exception — nobody has been told yet, so it is re-attempted on the reminder cadence until it lands,
and stops there.

### How much a broken job actually sends

These numbers are measured against the code, not estimated.

`ThresholdReached` fires when the `FirstFailure` raise applies and the row's post-upsert occurrence
count **equals** `AlertFailureThreshold` (`AlertsJob.ProjectAsync`; default 3,
`JobsOptions.AlertFailureThreshold`). "Applies" is read off the raise's returned
`last_projected_event_id`: only the event the row just absorbed may escalate, so a crash-replay —
whose held raises all return the stored, already-at-threshold count — re-fires the escalation only
from the true crossing event, where the `ThresholdReached` row's own high-water guard settles it. The
count is monotonic **within one incident**: it starts at 1 when the incident opens and only climbs
until a success resolves it, so the threshold is crossed at most once per incident and reads as "N
failures since this broke" whatever the job's cadence — a slow job just takes longer to reach it, it
never becomes unreachable. Only the next incident, opened by the next failure after a resolution,
restarts the count at 1 (`JobsOptions.AlertFailureThreshold`).

#### Volume

Delivery selects an unresolved incident whose delivery is `Pending`/`RetryAfter` due now, or whose
delivery already settled (`Delivered`/`Failed`) and whose scheduled reminder has come
(`GetDeliverableAlerts.sql`; see [Delivery](#delivery)). Every newly inserted row is born `Pending`
(`AlertsJob.EmitAsync`, `RaiseAlert.*.sql`), so a new incident's first notification is
immediate.

**A job that is permanently broken produces exactly one open incident row per (kind, reason) — one for
`FirstFailure`, one for `ThresholdReached` once the count crosses it — no matter how fast or how long
it keeps failing.** `occurrence_count` on that row climbs with every repeat; no new row appears until
the incident resolves. A 10,000-job outage is 10,000 open rows (one per failing job, per kind and
reason it hits), not 2–3 accumulated rows per job per rolling window under the retired windowed model.

| | One-shot job | Recurring slot |
| --- | --- | --- |
| Failures before it stops | at most `MaxAttempts` (default 15, `JobAttribute.MaxAttempts`) | unbounded; the slot re-arms forever (`WorkerFinalizer.ComputeRecurringOutcome`) |
| Alert rows on defaults | at most three: `FirstFailure`, `ThresholdReached`, `FinalFailure` | two, for as long as the slot stays broken |
| Ends by itself | yes, at the terminal `Failed` | only on a success, or operator intervention |

Row count does not grow with time or failure rate; what changes with time is how often an open
incident is *delivered* again. `AlertReminderInterval` is that lever (default 24h,
`JobsOptions.AlertReminderInterval`): settling an incident's delivery to `Delivered` or `Failed` —
`Suppressed` is not reminded — schedules the next pass an interval out, so a permanently broken job
pages at most once per incident per interval rather than once per failure. A manual alert that
delivered schedules nothing (see [Resolution](#resolution)). Widening the interval slows the
reminder cadence; it does not change how many rows exist or whether `ThresholdReached` is reachable.

### Delivery

The deliver phase selects due rows and settles each one (`AlertDelivery.DeliverAsync`). Due means an
unresolved incident (`resolved_at_utc IS NULL`) and one of two arms, both reading `retry_after_utc`
as the row's "not before" instant: `Pending`/`RetryAfter` with it null or past — a first attempt or a
due retry — or `Delivered`/`Failed` with it past — a reminder, at the instant that settlement
scheduled. `Suppressed` rows are never reminded: that status recorded a routing decision about the
channel, and re-sending would only re-take it. Ordered by id, 256 per pass
(`GetDeliverableAlerts.sql`).

The reminder is scheduled rather than inferred from `modified_at_utc`, and that is not a detail: every
repeat an open incident absorbs re-stamps that column, so an age rule would leave a job failing faster
than the interval permanently too young to remind — silencing exactly the outage worth re-notifying.
Nothing but a settlement or a resolution moves the instant, so neither a repeat nor an operator's
acknowledge can postpone a reminder.

For each row it resolves the channel by name in the firing namespace, resolves a transport by the
channel's kind, and decides in this fixed order (`AlertChannelVerdict.Decide`):

| Condition | Outcome | Delivery status |
| --- | --- | --- |
| No channel of that name configured for the namespace | Failed | `Failed` (200) |
| Channel `Disabled` | Suppressed | `Suppressed` (30) |
| Channel `Deprecated` | Suppressed | `Suppressed` (30) |
| Alert severity below the channel's `MinSeverity` | Suppressed | `Suppressed` (30) |
| No transport registered for the channel's kind | Failed | `Failed` (200) |
| Otherwise | Send | per the transport's outcome |

A transport that throws is treated as retryable and logged at Warning, never propagated
(`AlertDelivery.SendAsync`). Settlement (`AlertDelivery.SettleAsync`) writes a compare-and-swap against the
`version` the row carried at selection (`UpdateAlertDelivery.*.sql`): a row that moved in the
meantime — an operator resolved it, or a competing worker already settled the same attempt — matches
no version, and the write is skipped with a Debug log line rather than retried, because the newer state
is the one that should stand (`AlertDelivery.WriteSettlementAsync`).

- `Delivered` (100) — terminal, and `retry_count` resets to 0: the send series that just landed is
  over.
- `Retryable` — `retry_count + 1`; below `AlertDeliveryMaxRetries` the row becomes `RetryAfter` (20)
  with `retry_after_utc` set from a `30s..1h` doubling curve with 10% jitter, parsed once and
  independent of any job's backoff policy (`AlertDelivery.RetryBackoff`, `AlertDelivery.SettleAsync`).
- At the cap — `Failed` (200), terminal.

`retry_count` is the budget for one **send series**, not a lifetime count on the row
(`AlertEntity.RetryCount`). `AlertDeliveryMaxRetries` is `internal` and fixed at 5
(`JobsOptions.AlertDeliveryMaxRetries`): five send attempts and four waits of roughly 30s, 1m, 2m, and
4m (`BackoffSchedule.ComputeDelaySeconds`), so the one-hour ceiling in the curve is never reached and a
series that cannot be delivered goes terminal `Failed` in under ten minutes of tick time. Because
delivery runs on the one-minute tick, each wait rounds up to the next tick boundary. `Delivered`
resets the budget to 0, so a reminder for a still-open incident starts with the whole curve again
rather than inheriting whatever the previous series spent — a row that took four attempts to land does
not walk into its next reminder one throw from the cap.

Delivery is at least once: a crash after send but before settlement can resend a duplicate
(`AlertsJob`).

The delivery guarantee:

> An alert resolved before delivery selection is not sent. Resolution suppresses further pending and
> retry attempts. A transport attempt already in progress may still complete.

Both resolve paths settle the delivery they close (`ResolveAlerts.*.sql`,
`ResolveAlert.*.sql`): a `Pending` or `RetryAfter` row moves to `Suppressed` and its
retry timer clears, so a notification queued for a condition that has cleared is cancelled rather than
sent. An already-settled row (`Delivered`, `Failed`, `Suppressed`) keeps its status — resolving does
not edit the record of what the send actually did. Selection itself excludes every resolved row
(`GetDeliverableAlerts.sql`), and settlement's compare-and-swap is what makes a resolve race-safe
against a send already in flight: the resolve writes unconditionally and wins, and the in-flight
attempt's own settlement finds its expected version gone and quietly loses, per the guarantee above.

### Channels and routing

A channel is **process startup configuration**, declared on the worker builder and resolved from an
in-memory registry at delivery time (`WorkerBuilder.AddAlertChannel`, `AlertChannelRegistry`):

```csharp
j.Run("billing", w =>
{
    w.AddManifest<BillingJobs>();

    w.AddAlertChannel(
        "ops-slack",
        AlertTransportKinds.SlackWebhook,
        endpoint: builder.Configuration["Alerts:SlackWebhookUrl"]!,
        o =>
        {
            o.MinSeverity = AlertSeverityCode.Warning;
        });
});
```

- **Name and transport kind are operator-stable kebab identifiers; the endpoint is free-form**
  (a URL, ARN, or address) and only checked non-empty (`WorkerBuilder.AddAlertChannel`). Re-declaring
  a name replaces the earlier declaration rather than throwing (`WorkerBuilder.AddAlertChannel`).
- **Every worker namespace gets an implicit `default` channel** on the log transport, `Active`, with
  `MinSeverity = Info` (`AlertChannelRegistry.RegisterDefault`). Declaring `default` yourself
  overrides it. This is why failures reach the logs out of the box.
- **`MinSeverity` and `Status` are the two per-channel policies** (`AlertChannelOptions.MinSeverity`,
  `AlertChannelOptions.Status`). `Disabled` and `Deprecated` both suppress; the difference is what the
  log line says (`AlertDelivery.LogSuppressedDecision`).
- **Built-in transport kinds are `log` and `slack-webhook`** (`AlertTransportKinds.Log`,
  `AlertTransportKinds.SlackWebhook`). A custom transport defines its own kebab kind and registers an
  `IAlertTransport`.
- **Acta SQL stores the channel *name* only.** Endpoints, webhook URLs, credentials, and routing keys
  are never persisted: the delivery target is assembled at send time from the registry declaration
  (`AlertDelivery.SendAsync`). Keep real endpoints in configuration or a secret store.

Route a job with `[Job(AlertChannelName = "ops-slack")]` (`JobAttribute.AlertChannelName`); a job that
declares none routes to `default`. `RunbookUrl` on the same attribute (`JobAttribute.RunbookUrl`) is
resolved at delivery time from the definition and carried on the notification
(`GetDeliverableAlerts.sql`, `AlertDelivery.SendAsync`); it is not on the list-query projection.

`AlertChannelValidationMode` checks at worker startup that every alerting definition routes to a
channel configured in its namespace: `Off` skips, `Warn` (default) logs each unroutable definition,
`Fail` throws out of worker initialization (`JobsOptions.AlertChannelValidationMode`,
`AlertRoutingCheck.ValidateRouting`). Disabled and deprecated channels count as configured — that is a
delivery decision, not a routing one. The check covers **definitions only**: a definition with
`AlertProfile = None` is skipped (`AlertRoutingCheck.ValidateRouting`), and a `channelName` passed to
`ctx.AlertAsync` is not validated at startup at all. A manual alert naming a channel the namespace does
not configure settles `Failed` with no retry on its first delivery pass.

### Operator surface

Alert rows are queryable like everything else. `IActaOperations.Alerts` offers `ListAsync` (filtered
by namespace, job, resolution, acknowledgement, severity, delivery status, and tags), `GetAsync`,
`AcknowledgeAsync`, and `ResolveAsync` (`IAlerts`, `ListAlertsQuery`). The dashboard and HTTP API
expose the two verbs at `POST /alerts/{alertRef}/acknowledge|resolve`, 200 or 404 and never 409 —
answering `AcknowledgeAlertResponse` and `ResolveAlertResponse` — behind
`EnableControls` and the `X-Acta-Control` header (the gate lives in `MapActa`, the header
name in `ActaEndpointOptions.ControlConfirmationHeaderName`; the verbs themselves in
`AlertsEndpoints`). In SQL, `acta.alerts_view` renders the coded columns beside `deduplication_key`
(`AlertsView.*.sql`); see [SQL recipes](#sql-recipes) for ready queries.

Acknowledge means an operator has seen the alert; resolve means the incident is considered settled.
Neither changes the job. Acknowledging also does not quiet the alert: a reminder already due still
fires, because seeing an outage is not fixing one. Resolving is what ends the reminders — it clears
the row's scheduled instant along with everything else it settles
(`ResolveAlert.*.sql`).

Retention is separate from the job's. `sys.retention` deletes alerts older than `AlertRetention`
(default 90 days, `JobsOptions.AlertRetention`) **whether or not delivery settled**
(`PurgeExpiredData.*.sql`). Settled rows — `Suppressed`, `Delivered`, `Failed` — and rows still
stuck `Pending` or `RetryAfter` are counted separately, and a pass that aged out an undelivered row
logs one warning naming how many: an alert deleted before it ever reached a channel is a signal
nobody got. Nothing is immortal under this rule, an open incident included — the deduplication index
covers unresolved rows only, so deleting the row frees the identity and the next failure opens a
fresh incident. The projector's `alerts-skip-*` poison variables age out on the same window. Because
an alert keeps its own copy of the job ref, it can outlive the job it was raised for; a manual
`PurgeAsync` on the job deletes its alerts immediately. See
[Retention and purge](#retention-and-purge).

### Where the principle breaks

The intended rule is that one profile means one thing for every job and the alert lifecycle is
independent of how the job was started. Two divergences remain, and both trace to a single root
cause: **a recurring slot has no terminal state.** `MaxAttempts` is the one-shot retry budget, and a
recurring slot re-arms instead of terminalizing however many consecutive runs throw
(`WorkerFinalizer.ComputeRecurringOutcome`). That is deliberate — a nightly job must not die permanently
after three bad nights — but the alerting consequences are real.

#### 1. `OnTerminal` and `Info` are silent for a recurring job's ordinary failures

Both wait for the terminal transition, and a rollover is not one, so a recurring job that throws or
loses its lease alerts nothing under either profile. Recorded in
[Known limitations § execution model](./known-limitations.md); the recommendation there —
choose `OnFailure` for recurring work you want to hear about — is the right one.

They do still fire on the terminal `Failed` shapes a recurring slot can reach, all three of which
route through the handler-status completion path and stop the whole slot
(`WorkerFinalizer`):

| Shape | Reason code | Where |
| --- | --- | --- |
| Handler declares failure with `ctx.FailAsync` | `job.handler-failed` (52) | `HandlerInvocation` |
| Handler throws `NotImplementedException` / `NotSupportedException` | `job.non-retryable-exception` (23) | `HandlerInvocation` |
| An `AtMostOnce()` step was re-entered before its outcome was recorded, uncaught | `job.step-interrupted` (63) | `HandlerInvocation` |

A whole-job deadline is **not** one of them: the deadline lands the job `Cancelled`, not `Failed`
(`ExecutionAdmission`, `WorkerFinalizer`, `EventReasonCode.JobDeadlineExceeded`), and the alertable set has no
`Cancelled` branch (`GetAlertableEvents.sql`). No profile alerts on a deadline — the `on-terminal`
`[Code]` description (`AlertProfileCode.OnTerminal`) and
[Known limitations](./known-limitations.md) both say the same.

#### 2. A one-shot job's alerting ends by itself; a recurring job's does not

A one-shot job fails at most `MaxAttempts` times and then terminalizes, so its `FirstFailure` /
`ThresholdReached` / `FinalFailure` incidents resolve or age out with the job. A permanently broken
recurring slot never produces the success that would resolve its incident, so `FirstFailure` (and,
once crossed, `ThresholdReached`) stay open indefinitely: one row each, `occurrence_count` climbing
with every re-arm, delivered again on `AlertReminderInterval` rather than replaced by a fresh row.
Same profile, same configuration, a lifecycle that ends by itself in one case and only on success or
operator intervention in the other.

Neither of these is a feature. They are the current behaviour, and they are the two places where
reading the profile name does not tell you what the job will do.

### Choosing a profile

**One-shot work.** Keep `OnFailure`, the default. You get a `FirstFailure` warning
on the first retryable failure, an `Error` escalation if the same reason recurs to the threshold
before the job exhausts its retries, and a `FinalFailure` when the budget is exhausted — at most three
rows, and the sequence ends by itself. Pick `OnTerminal` when the intermediate retries are noise and
only the final outcome should page; it costs you the early warning, and for a one-shot job it costs
nothing else. `Info` is `OnTerminal` at a severity most channels will not page on — useful for work
whose failure is a note rather than an incident, but check the routed channel's `MinSeverity` first,
because `Info` alerts are exactly what a `Warning` floor suppresses.

**Recurring work.** `OnFailure`, or you will hear nothing. `OnTerminal` and `Info` are silent for the
rollover case, which is how a recurring job fails nearly all the time. Accept that the incident does
not close on its own: it opens once, reminds on `AlertReminderInterval` (once a day by default) for as
long as the slot stays broken, and treat "this alert is still open" — not "a new alert appeared" — as
the signal that nobody has fixed it. `ThresholdReached` is reachable at every cadence, because the
count is per incident rather than per window: it fires once the Nth failure since the incident opened
lands, however slowly that takes. If you want a recurring job to stop rather than keep failing, that
is a handler decision — `ctx.FailAsync` — not a profile one.

**System work.** `SysCritical` is reserved for Acta's own jobs (`sys.alerts`, `sys.retention`,
`sys.outbox`) and behaves as `OnFailure` pinned to `Critical` severity. There is no
reason for an application job to declare it: the same emissions are available from `OnFailure`, and
taking `Critical` for application work removes the one severity that distinguishes framework failure
from application failure on a shared channel.

**Handler-owned alerting.** `None` turns off automatic alerting without turning off
`ctx.AlertAsync`, so a handler that wants to alert on its own terms — its own key, its own severity,
its own channel — can, while
the framework stays quiet. It also removes the job from the startup routing check, so a manual alert
from a `None` job naming an unconfigured channel is not caught until it fails delivery.

### Runnable examples

Runnable examples: `concepts/400-observability-and-alerts/402-alerts` (manual alert),
`403-alert-channel` (a declared channel with a severity floor), `405-real-alert-routing` (Slack
webhook and a shared deduplication key), `406-automatic-failure-alerts` (no `AlertAsync` at all), and
`411-alert-escalation` (`FirstFailure` → `ThresholdReached` → `FinalFailure`).

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
explicitly. See [Transactional enqueue and the external outbox](./guide.md#ambient-systemtransactions-scopes-are-rejected).

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
Background: [transactional enqueue and the external outbox](./guide.md#retry-and-quarantine).

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
