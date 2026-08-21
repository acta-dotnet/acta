# Operator guide

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

Reference docs: [`data-model.md`](../reference/data-model.md) (every table and column),
[`code-families.md`](../reference/code-families.md) (every status / reason / event code),
[`conformance-contracts.md`](../reference/conformance-contracts.md) (the tested guarantees).
For a visual map of the job lifecycle, maintenance flow, and operator surfaces, see
[`architecture-diagrams.md`](../technical/architecture-diagrams.md).

## Status quick reference

Live statuses: `Ready = 10`, `Suspended = 20`, `Paused = 30`, `Dispatched = 40`, `Executing = 50`.
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
| `Dead = 200` | Heartbeat went stale past the liveness window; `sys.recovery` flipped it | `122 worker.died` / `101 worker.heartbeat-stale` |

On a terminal worker, `modified_at_utc` is the instant it ended: both transitions stamp it. A worker
killed outright never reaches the clean path, so it stays `Active` until recovery marks it `Dead` -
which is why a kill and a crash are indistinguishable here, and correctly so.

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

-- One tenant's work, by the business key the caller enqueued with (keys fold lowercase).
SELECT job_id, job_ref, namespace, job_name, status
  FROM acta.jobs_view
 WHERE tenant_key = :tenant_key;

-- Backlog per tenant, busiest first. Untenanted work groups under NULL.
SELECT tenant_key, COUNT(*) AS ready
  FROM acta.jobs_view
 WHERE status = 'ready'
 GROUP BY tenant_key
 ORDER BY ready DESC;

-- In-flight work and who holds it.
SELECT job_id, status, leased_by_worker_id, leased_by_worker_host, lease_expires_at_utc
  FROM acta.jobs_view
 WHERE status IN ('dispatched', 'executing');

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
await jobs.RestartAsync(job);                           // resets failure budget + retention, runs now
await jobs.RaiseSignalAsync(job, "approval", payload);  // releases a Suspended WaitSignalAsync
await jobs.RescheduleAsync(job, inTwoHours);             // move a waiting job's next run (re-arms Ready)
await jobs.ReprioritizeAsync(job, JobPriorityCode.High); // change claim priority in place
await jobs.PurgeAsync(job);                              // hard-delete a terminal job (audited as job.purged)
```

Cancel of a parent cancels the whole non-terminal subtree (descendants carry reason
`parent-cancelled`). Restart leaves a terminal row's history intact and re-arms the same id; there
is never a replacement row. Reschedule applies to Paused/Suspended/Ready rows and re-arms them
Ready; purge refuses a non-terminal job and a job that has child jobs (purge the children first).
Every verb returns Applied / Rejected / NotFound rather than throwing on an illegal transition.

### Schedule controls

Address a schedule by its recurring slot job plus schedule name. Definition-backed slot jobs use the
job name as their deduplication key.

```csharp
var nightly = new ScheduleLookup(
    JobLookup.ByDeduplicationKey("billing", "reconcile-ledger"),
    "nightly");

await operations.Schedules.TriggerNowAsync(nightly, note: "validate repaired upstream");
await operations.Schedules.PauseAsync(nightly, untilUtc: maintenanceEndsUtc, note: "maintenance");
await operations.Schedules.ResumeAsync(nightly, note: "maintenance complete");
```

Trigger now, misfire catch-up, normal cursor movement, and historical backfill are deliberately
different operations. Use [Schedule operations](./schedule-operations.md) before choosing a control.

### What each verb does from each status

Read from the provider routines, not from intent. Every verb answers one of three ways: **applied**
(`200`), **rejected** (`409`, the job exists but its status forbids the transition), or **not found**
(`404`). All three carry the same `JobControlResponse` body, so a client reads `action` and the
resulting `status` without special-casing the code.

`R` Ready · `S` Suspended · `P` Paused · `D` Dispatched · `X` Executing · `✓` Succeeded · `F` Failed ·
`C` Cancelled. **A** applied, **409** rejected.

| Verb | R | S | P | D | X | ✓ | F | C | Result on applied |
|---|---|---|---|---|---|---|---|---|---|
| `pause` | A | A | A | 409 | 409 | 409 | 409 | 409 | Paused |
| `resume` | 409 | 409 | A | 409 | 409 | 409 | 409 | 409 | Ready |
| `restart` | A | A | A | A | 409 | A | A | A | Ready, failure budget and retention reset |
| `cancel` | A | A | A | A | A | 409 | 409 | 409 | Cancelled, cascading to non-terminal descendants |
| `purge` | 409 | 409 | 409 | 409 | 409 | A | A | A | Row hard-deleted, `job.purged` event kept |
| `reschedule` | A | A | A | 409 | 409 | 409 | 409 | 409 | Ready at the new instant |
| `reprioritize` | A | A | A | A | A | 409 | 409 | 409 | Priority changed, status untouched |
| `input` (amend) | A | A | A | 409 | 409 | A | A | A | Input replaced, format round-tripped |
| `signal` | A | A | A | A | A | 409 | 409 | 409 | Slot set; a Suspended job waiting on that name goes Ready |

Repeating a verb is a no-op only where the table says so. **`pause` is idempotent** (Paused stays
Paused, applied). **`resume` is not**: it accepts only Paused, so resuming an already-running job is
a 409 rather than a silent success. That asymmetry is deliberate - pausing twice expresses the same
intent, resuming something that was never paused does not.

Three rules the table cannot show:

- **`purge` also rejects a job that has children**, whatever its status. `parent_id` carries no
  database cascade, so purging a parent would leave a child pointing at a row that no longer exists.
  Purge the leaves first.
- **`cancel` on an Executing job** marks the row Cancelled and cancels the running attempt's token;
  the handler still has to return. The row is terminal before the process notices.
- **`signal` on a Paused job** records the slot and leaves the job Paused. The signal is not lost;
  it is waiting for a `resume`.
- **`input` on a terminal job is allowed**, which looks odd until you want it: amend the input of a
  failed job, then `restart` it. Only Dispatched and Executing reject, because those are the two
  states where a worker may already have read the payload. A job that stored no input has nothing to
  amend and answers `409` from the endpoint before the routine is reached.

## Tenant and namespace administration

Tenants and namespaces carry an operator-controlled status: `active` resolves at enqueue, `suspended`
withdraws admission. Tenant suspension is admission control, not work closure: new enqueues naming the
tenant key are rejected once the suspend has committed, while jobs already admitted keep running and a
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
edited. Every applied transition is audited on the events timeline in the 15xx admin band
(`tenant.suspended` 10 through `namespace.updated` 22); tenant events land on the seeded
`sys` namespace, namespace events on the namespace itself.

Over HTTP the same verbs are control-gated (`EnableControls` + confirmation header):
`POST /tenants/{key}/suspend|resume`, `PATCH /tenants/{key}`, `POST /namespaces/{name}/suspend|resume`,
`PATCH /namespaces/{name}`. Reads are ungated: `GET /tenants` pages the catalog and
`GET /tenants/{key}` is the point read (`ITenants.GetAsync` in code).

An enqueue that trips a tenant or namespace guard throws a typed `EnqueueRejectedException` whose
`Reason` is machine-readable: `NamespaceSuspended`, `TenantSuspended`, `TenantUnknown`,
`TenantRequired` / `TenantForbidden` (the definition's `TenantRequirement` policy), and
`TenantMismatch` (a child named a different tenant than its parent without the explicit override).
Guard-wrapped HTTP handlers map it to 409 with the reason in the ProblemDetails. Other enqueue
failures (unknown namespace/job, retired definition, missing parent) still surface as raw provider
errors. The same guards apply when the external outbox relays a record: a record that trips one
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
<app> jobs signal  <job-ref|deduplication-key|id> <name>
<app> jobs debug   <job-ref|deduplication-key|id>
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
- Wait for sys.recovery to reclaim the job on the next maintenance tick.
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
`operations.Workers`, `operations.Alerts`, `operations.Tenants`, and `operations.Namespaces`. Every list read is keyset-paginated: pass the
returned `PagedResult<T>.NextCursor` back as `Cursor` for the next page. Cursors are opaque and
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
`restart`, `cancel`, `reschedule`, `reprioritize`, `purge`), thin wrappers over the `IJobs` verbs above: the framework still stamps actor
and reason code. The non-purge verbs accept an optional `reasonMessage`; purge retains no caller reason because it removes the job's
event history. The outcome maps to 200
(applied), 409 (rejected), or 404 (not found) — and that shape is the whole API's rule, not this
family's: every control family answers 200, 404 and 409 (202 for the accepted-then-applied outbox
verbs) with its own envelope, and `ProblemDetails` is reserved for malformed input, authorization,
and server faults. The one deliberate exception is the enqueue guard's 409: `JobEnqueueAction` has no
rejected value and a refused enqueue mints no job ref for the envelope to name, so that rejection
stays a problem document with its `reasonCode`. Controls are
opt-in (`EnableControls = true`)
because they mutate jobs; enable them alongside your authorization, never on an open surface.
Unmapped controls answer 404. Control requests must send the `X-Acta-Control: true` header,
an anti-accident guard (not authentication) the dashboard sends automatically. The job detail
screen surfaces all seven actions with state-aware availability and confirmation for destructive
changes. Explain links directly to the applicable signal/control/timeline action and shows the
durable evidence behind its recommendation. The dashboard also exposes schedule
pause/resume/trigger/preview/overrides, alert acknowledge/resolve, and tenant/namespace
administration when controls are enabled. The CLI below intentionally has a smaller verb set.

Signals are also raisable over HTTP at `POST /jobs/{jobRef}/signals/{name}`, the inbound counterpart
to `IJobs.RaiseSignalAsync`. Signals are operator control: the endpoint is mapped only when
`EnableControls = true` and, like the destructive verbs, requires the `X-Acta-Control: true`
confirmation header (missing or wrong header answers 400). An empty body raises a presence-only
signal; a non-empty `application/json` body is stored verbatim and a handler reads it via
`ctx.WaitSignalAsync<T>(name)`. Signal names are validated as kebab at the edge, which is what
rejects forged internal names: the `sys.`-prefixed internal names (such as the `sys.child.`
child-latch names) are rejected as reserved, and their dotted shape is not valid kebab either. The outcome
maps to 200, 409 (terminal job), or 404.

A read-side `GET /capabilities` reports `{ controlsEnabled, version, provider, confirmationHeader }`
so dashboards can show or hide edit UI without probing a control route; it is always mapped and
never gated.

The surface is local-only by default (`LocalOnly = true`): requests from non-loopback remote
addresses are rejected with 403, so the dashboard works with zero setup from the same machine. The
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
text input is also editable in place unless the job is dispatched or executing. Treat authorization
for the read surface as authorization to read every payload the ledger holds. See
`concepts/000-fundamentals/022-dashboard` for a runnable host.

## Retention and purge

Every terminal landing stamps `retention_until_utc` from the definition's `JobRetention` policy
(default 90 days); the framework `sys.retention` job purges terminal rows past that deadline in
batches, along with `events` / `alerts` / terminal-worker rows past their `JobsOptions` windows
(`JobEventsRetention`, `AlertRetention`, `WorkerRetention`). A row with a NULL
`retention_until_utc` is never purged; only terminal rows ever carry one. Substrate rows
(checkpoints, steps, results) delete with their job.

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
| Eligible job | Terminal and past its retention deadline | Terminal now |
| Child-job guard | Normal deadline-driven deletion behavior | Rejects a parent that still has child jobs; purge children first |
| Job event | No per-job purge event | Emits `job.purged` after removing the job history |
| Existing job events | Age out on their own event-retention window | Deleted immediately for that job |
| Existing alerts | Age out on their own alert-retention window | Deleted immediately for that job |
| Intended use | Routine bounded storage | Immediate removal of one known terminal job |

Before a manual purge: confirm the job is terminal and has no child jobs; copy any incident summary
or required audit evidence; confirm no caller still needs `GetResultAsync`; record a reason that
explains the administrative decision; remember that purge removes Acta state only (related business
data and database backups are outside its reach). Do not use manual purge as backlog control:
pausing, cancelling, rescheduling, or fixing worker capacity addresses runnable work without
deleting evidence.

## Migrations

Schema lives in `src/Acta.{Postgres,SqlServer,Sqlite}/Schema/Migrations/M001_init.sql` (generated; never hand-edit).
`ApplyMigrationsOnStartup = true` applies it on boot, which is the right dev-mode default and the
wrong production one: apply the script from a deployment step instead. Operation routines install
and curated operator views re-apply on bootstrap after migrations under the same schema lock.

## Alerts

Alert channels are worker startup configuration. Acta SQL may persist alert channel_name as routing
metadata. Acta SQL must never persist alert transport endpoint, webhook URL, credential, routing key,
or opaque transport config. Delivery configuration is process startup configuration resolved through
IAlertChannelRegistry. Every worker namespace has an implicit `default` log channel unless overridden
with `w.AddAlertChannel(...)`. Store real endpoints/secrets in appsettings, environment variables, or a
secret store, then pass them into `AddAlertChannel` at startup. Route a job with
`[Job(AlertChannelName = "...")]`; `AlertProfile` decides what fires, incident identity collapses
repeats onto one open row per job and condition, `AlertReminderInterval` (24 hours, settable) governs
how often a still-open incident pages again, and delivery retries are capped at five per send series.
Alert rows are queryable like everything else:
`SELECT * FROM acta.alerts_view WHERE delivery_status = 'pending'` shows what has not delivered yet.

## What maintenance does for you

Workers heartbeat their leases (`HeartbeatInterval`); `sys.recovery` reclaims jobs whose lease
lapsed (back to Ready in budget, terminal Failed past `MaxAttempts`, with retention stamped),
marks silent workers Dead after `WorkerDeadAfter`, and re-raises any child-completion latch a crash
left pending; `sys.retention` runs the purge. If a job looks stuck in `Dispatched`/`Executing`, check the
worker's `last_seen_at_utc` first; recovery is automatic once the lease lapses.

## Security and exposure

Acta ships no login system; the dashboard and JSON API are local-only by default (`LocalOnly = true`, non-loopback requests get 403). Exposing the surface remotely means setting `LocalOnly = false` and wiring host authorization through `ConfigureEndpoints`, as in the `MapActa` example above. Beyond that:

- `LocalOnly` checks the TCP peer address, not the original client: behind a reverse proxy or gateway on the same host, the peer is the proxy's loopback address, so the check passes for every forwarded request. Behind a proxy, do not rely on `LocalOnly`: gate on real authorization through `ConfigureEndpoints`, and configure `ForwardedHeaders` if the host needs the true client IP.
- The `X-Acta-Control: true` header on control POSTs is an anti-accident guard, not auth; enable controls (`EnableControls = true`) only behind authorization.
- Keep secrets and PII out of job input, result, and tags; store large or sensitive blobs externally and enqueue a reference (URI plus checksum and size). Payloads are readable in full by anyone authorized for the read surface (see the dashboard section above), so this is the data-classification boundary: there is no per-payload redaction or disclosure gate to fall back on.
- Every host doubles as its own control CLI (`<app> jobs info|pause|resume|restart|cancel|debug`); use `j.DisableCli()` only if the host owns its own command line.

## Production checklist

Use this for production-like evaluation, staging, and first production workloads on a release candidate.

Version and schema:
- The migration history freezes at 1.0.0. Before it, `M001` may be re-cut in any release, which means dropping and reprovisioning the database (bootstrap refuses to start on a baseline mismatch rather than applying it). From 1.0.0 schema changes ship as additive `Mnnn` migrations. Keep `ApplyMigrationsOnStartup = false` outside dev and apply migration SQL from a deploy step.
- Run `dotnet run --project tools/Acta.Emit -- check` in CI. Pin Acta versions across a namespace. Set `DeploymentVersion` to a build id; set `JobsOptions.ManifestGenerationUtc` only when deterministic definition promotion matters for your packaging/deploy shape.

Provider and database:
- SQL Server or Postgres for distributed multi-worker; SQLite for embedded single-node. Choose the schema name before installing. Size the connection pool for executors, claim loops, dashboard reads, and alerts. Keep the database clock healthy (`AllowClockSkew = true` bypasses the startup skew check). SQL polling is the correctness baseline; Redis wakeup only lowers pickup latency.

Worker coordination:
- Keep `HeartbeatInterval` identical across replicas; the lease (x4) and dead-worker (x7) windows derive from it, so the proportions cannot drift. Tune `MaxConcurrentExecutors`/`ClaimBatchSize` against database capacity. Keep system maintenance on unless you have a tested replacement.

Handlers:
- Stable kebab-case `[Job("...")]` names; treat `TIn`, `TOut`, name, and format as durable contract. Make external side effects idempotent (Acta is at-least-once). Steps for run-once internal slots; child jobs for independently visible, retryable work.

Validation and caveats:
- Run the conformance suite for your provider, the `anvil/Anvil` crash/reclaim flows, and the `anvil/Anvil.Bench` baselines. Test a rolling deploy with mixed old/new workers and queued rows. APIs, schema, and behavior may still change without deprecation before 1.0; hardening, authorization guidance, and capacity/retention/alerting playbooks still need real deployment feedback.
