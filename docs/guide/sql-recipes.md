# SQL recipes

These recipes are Postgres-flavored against the default `acta` schema; on SQL Server, bracket
identifiers as needed and replace interval arithmetic with `DATEADD`. The first recipe enqueues a job
(a write, through the installed routine); the rest are operator/debugging reads.

For reads, start with the curated operator views: `acta.jobs_view`, `acta.events_view`,
`acta.checkpoints_view`, `acta.steps_view`, `acta.schedules_view`, `acta.alerts_view`,
`acta.workers_view`, `acta.definitions_view`, and `acta.tags_view`. Views expose name-only decodes for common job
codes (`status = 'failed'`) and keep the raw `*_code` columns beside them. They are stable enough for
operator use, but they are not the storage contract. Runtime code does not depend on them, and
migrations do not derive from them.

[`data-model.md`](../reference/data-model.md) documents every storage table, and
[`code-families.md`](../reference/code-families.md) remains the full decoder ring for descriptions and
less common codes.

## Foreign-key policy

Acta enforces DB foreign keys in exactly two shapes:

- CASCADE from `jobs` to its transactional substrate children (runtimes, checkpoints, results,
  steps, tags, schedules): these rows' physical lifetime is owned by the job row.
- RESTRICT (no action) from transactional tables to dimension rows (schedules → definitions;
  workers → namespaces): dimensions have no delete surface, and any future hard-delete design must
  confront referencing rows explicitly. The `jobs` dimension FKs (jobs → namespaces / tenants /
  definitions) were cut by the 2026-07-11 bench gate after they regressed enqueue-batch, so those
  references are write-time validated and conformance-tested instead.

Never enforced, by design:

- Audit/history rows (`events`, `alerts`): audit outlives its subjects. Queries against them must
  use their denormalized columns (e.g. `alerts.job_ref`), never joins to possibly-purged rows.
- Ephemeral/operational references (`leases.job_id`, `runtimes.leased_by_worker_id`): reaped by
  their own lifecycles.
- Lineage self-references (`jobs.parent_id`, `jobs.lineage_root_id`): retention deletes parents in
  retention order and dangling lineage refs are tolerated everywhere; a RESTRICT self-FK would fail
  routine retention.

Every unenforced reference must be write-time validated by its operations and covered by
conformance tests. New hot-path tables must justify any FK in review.

## Enqueue A Job

Creating work from SQL is a write, so call the installed `enqueue_one` routine instead of hand-writing
`INSERT`s. You pass the **namespace name and the job name** (the `[Job("...")]` name); the routine
resolves the definition, stamps the job ready, and writes the `jobs` and `runtimes` rows in one
transaction.

**Postgres**: a set-returning function; call it with named arguments:

```sql
SELECT job_id, job_ref, action  /* action: 1 inserted, 2 idempotency-deduped */
  FROM acta.enqueue_one(
    p_namespace_name => 'billing',
    p_job_name       => 'send-invoice',
    p_input          => convert_to('{"invoiceId":42}', 'UTF8')
  );
```

**SQL Server**, a stored procedure; tags arrive through a table-valued parameter that has no default,
so declare an empty one even for a tag-free enqueue:

```sql
DECLARE @tags acta.job_enqueue_tag_batch;
DECLARE @payload varbinary(max) = CAST('{"invoiceId":42}' AS varbinary(max));
EXEC acta.enqueue_one
    @p_namespace_name = 'billing',
    @p_job_name       = 'send-invoice',
    @p_input          = @payload,
    @p_tag_batch      = @tags;
```

SQLite has no callable enqueue routine; enqueue through the application (`IJobs.EnqueueAsync`).

## Recent Jobs

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

## Backlog By Definition

```sql
SELECT namespace, job_name, COUNT(*) AS ready_count, MIN(next_run_at_utc) AS oldest_due_at_utc
  FROM acta.jobs_view
 WHERE status = 'ready'
 GROUP BY namespace, job_name
 ORDER BY ready_count DESC, oldest_due_at_utc;
```

Use this when work is not draining. If backlog exists but workers are alive, compare executor count,
claim batch, DB capacity, and whether jobs are due yet.

## In-Flight Jobs And Owners

```sql
SELECT job_id, job_ref, namespace, job_name, status, leased_by_worker_id, leased_by_worker_host, lease_expires_at_utc
  FROM acta.jobs_view
 WHERE status IN ('dispatched', 'executing')
 ORDER BY lease_expires_at_utc, job_id;
```

If the worker is gone and the lease has expired, `sys.recovery` should reclaim the job within its
maintenance cadence.

## Event Timeline

```sql
SELECT event_id, created_at_utc, job_id, job_ref, event, actor, from_status, to_status, execution_status, reason, reason_message, detail_format, detail_text, duration_ms
  FROM acta.events_view
 WHERE job_id = :job_id
 ORDER BY created_at_utc, event_id;
```

`events_view` is a readable ledger, not a rollup. The `jobs_view` row tells you where work is now;
events tell you why it moved. `detail_text` decodes built-in `json` and `text` event details; binary
and custom detail payloads stay opaque.

## Recent Terminal Failures

```sql
SELECT job_id, job_ref, namespace, job_name, failure_count, modified_at_utc
  FROM acta.jobs_view
 WHERE status = 'failed'
   AND modified_at_utc > now() - interval '24 hours'
 ORDER BY modified_at_utc DESC;
```

Follow with the event timeline for the specific `job_id`.

## Suspended Jobs Waiting On Signals

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

## Durable Sleeps And Timers

```sql
SELECT job_id, job_ref, namespace, job_name, checkpoint_name AS timer_name, state, due_at_utc
  FROM acta.checkpoints_view
 WHERE kind = 'timer'
 ORDER BY due_at_utc, job_id;
```

Timers are job-internal slots. The owning job becomes claimable when the timer is due and consumed.

## Step State

```sql
SELECT job_id, job_ref, namespace, job_name, step_name, state, attempt_number, next_retry_at_utc, reason, reason_message, result_format, result_text
  FROM acta.steps_view
 WHERE job_id = :job_id
 ORDER BY step_name;
```

A succeeded step replays from stored result. A failed step may re-arm independently of the parent
job's attempt budget until its own step budget is exhausted.

## Recurring Schedules

```sql
SELECT namespace, job_name, schedule_name, status, source, expression_effective, time_zone_id_effective, next_run_at_utc, paused_until_utc, description
  FROM acta.schedules_view
 ORDER BY namespace, next_run_at_utc NULLS LAST, schedule_name;
```

The schedule row carries the per-schedule cursor. The recurring slot job uses the minimum live
schedule cursor as its `next_run_at_utc`.

## Job Definitions And Effective Policy

```sql
SELECT namespace, job_name, status, input_type_name, input_format_name, output_type_name, output_format_name, priority, max_attempts, execution_timeout_seconds, deadline_seconds, retention_seconds, audit_level, alert_profile, alert_channel_name, runbook_url
  FROM acta.definitions_view
 WHERE namespace = 'billing'
 ORDER BY job_name;
```

Use this when a job behaves differently than expected after a deploy or operator override.

## Worker Liveness

```sql
SELECT namespace, worker_id, host, status, deployment_version, max_concurrency, last_seen_at_utc
  FROM acta.workers_view
 ORDER BY last_seen_at_utc DESC;
```

`sys.recovery` marks workers Dead when `last_seen_at_utc` falls past `JobsOptions.WorkerDeadAfter`.

## Pending Or Failed Alert Delivery

```sql
SELECT alert_id, namespace, job_id, job_ref, severity, kind, delivery_status, title, occurrence_count, retry_count, retry_after_utc
  FROM acta.alerts_view
 WHERE delivery_status IN ('pending', 'retry-after', 'failed')
 ORDER BY created_at_utc DESC;
```

Alert rows are projections of the event stream or manual handler alerts. Delivery retries are driven
by `sys.alerts`.

## Retention Candidates

```sql
SELECT namespace, status, COUNT(*) AS jobs_to_purge, MIN(retention_until_utc) AS oldest_retention_until_utc
  FROM acta.jobs_view
 WHERE retention_until_utc IS NOT NULL
   AND retention_until_utc <= now()
 GROUP BY namespace, status
 ORDER BY jobs_to_purge DESC;
```

`sys.retention` deletes expired terminal jobs and cascading substrate rows. Event and alert retention
have their own windows in `JobsOptions`.

## Tags

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

## Quarantined Outbox Rows

These recipes run against the **producer** database's external-outbox table, not the `acta` ledger
schema. The default physical name is `acta_outbox` in the provider's default schema; substitute your
configured table/schema. Status codes are `10` Pending, `20` Claimed, `90` Quarantined. Only requeue or
delete rows the relay has quarantined (`status_code = 90`); leave Pending and Claimed rows to the relay.
Background: [transactional enqueue and the external outbox](./transactional-enqueue-and-outbox.md#retry-and-quarantine).

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

## Identifier Casing In Raw SQL

Acta-owned identifier and key columns store canonical lowercase. Opaque keys (tenant, idempotency,
exclusive, dedupe, lock) are folded to lowercase at the API boundary; registered names (namespace,
job, schedule, signal, channel, and other catalog names) are validated as lowercase at registration.
Column collations stay at the database default: matching is canonical-by-construction, not
collation-dependent.

For raw SQL this means: compare with lowercase literals, and if you insert rows out of band, supply
lowercase, mixed-case values will never match Acta's equality, uniqueness, or joins. The exceptions
are caller-preserved external values (`correlation_key`, `actor_key`): those are stored verbatim, so
raw-SQL equality on them is collation-dependent, match the exact casing the caller supplied.

```sql
-- finds the tenant regardless of how the caller originally cased the key
SELECT id, tenant_key, description FROM acta.tenants WHERE tenant_key = 'acme-corp';
```
