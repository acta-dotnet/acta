# Release notes

## 0.4.0 (preview)

The data model is finished and verified: one status vocabulary, one payload ceiling, scoped durable
settings, DBA-runnable provisioning scripts, and a tenant-aware dashboard.

> **Schema note:** the `M001` baseline was re-cut in this release (extensible code catalogs,
> schedules cleanup, the status vocabulary below; baseline stamp `init-extensible-status-v1`).
> Preview compatibility policy applies: drop and reprovision the Acta database; the bootstrap
> refuses a mismatched baseline.

### One vocabulary

- Terminal success is `succeeded` everywhere (`done` is gone); in-flight execution is `executing`;
  substrate lifecycle columns are `status_code` like every other entity; event names are uniformly
  two-segment kebab.
- The "metadata" notion is retired: you update the entity (`UpdateAsync`, `tenant.updated`,
  `namespace.updated`), and `updated` is the one modification verb.

### One payload ceiling

- `MaxInlinePayloadBytes` (1 MiB) is the single knob for ledger payloads and HTTP request bodies,
  with `PayloadTooLargeException` as the single error. An oversized handler result no longer
  persists silently: the job succeeds, the body is dropped, `job.result-oversized` records why, and
  a typed wait throws instead of returning a default result.

### Durable settings

- `IActaOperations.Settings`: get/set named settings at global, namespace, or definition scope,
  with `setting.updated` evidence events. New setting names in newer Acta versions cost no
  migration.

### DBA-runnable provisioning

- `docs/reference/provision/{pg,mssql,sqlite}.sql`: generated, drift-checked, execution-proven
  scripts carrying the full schema, views, and routines - for deployments where the application
  principal is never allowed DDL.

### Dashboard

- Job rows show and link the tenant key; jobs and events filter by tenant key; the tenant page
  links into both pre-filtered.

### Compatibility

- Open code catalogs (`event`, `job-event-reason`, `alert-kind`): an older build renders codes from
  a newer build as `unspecified` instead of refusing to start.
- Persisted event code renames (destructive-class, final before 1.0): the `*.metadata-changed`
  names became `tenant.updated` / `namespace.updated`; ids unchanged.

## 0.3.0-alpha.1 (preview)

The runtime is reorganized into explicit modules, the release pipeline hardens, and the Bulk
execution profile gets correctness fixes for aborted attempts and batched completions.

> **Schema note:** the `M001` baseline was re-cut in this release (the completion batch TVP is now
> keyed by request ordinal and its job column is named `job_id`). Preview compatibility policy
> applies: drop and reprovision the Acta database; the bootstrap refuses a mismatched baseline.

### Modular architecture

- One flat `Acta` namespace for the whole SDK; module boundaries (Jobs, Execution, Ledger, ...)
  are formalized behind `IActaOperations`, with dependency-graph and SQL-ownership gates in CI.
- Provider SQL trees mirror the module layout; relational store registrations are shared across
  providers; concrete schema migrators are internal.

### Execution correctness

- An attempt aborted mid-flight (lease renewal at risk, or a held handler lock lost) now retries
  under the failure budget with the new `job.attempt-aborted` reason instead of landing terminal
  Failed while the row was still recoverable.
- SQL Server batched completions accept two attempts of the same job in one batch (correlation is
  by request ordinal) and bind failure reason codes correctly, so a terminal failure no longer
  fails the whole flush batch.
- Bulk records the `acta.executions` metric at durable finalization, matching Direct/Buffered
  semantics, and a swallowed fallback completion result is now logged.

### Security and release hardening

- The dashboard/API HTTP ingress perimeter is closed by default; unknown API faults return 500 and
  only the documented transient family returns 503.
- Workflow actions are pinned to commit SHAs with automated pin updates; packages publish to
  nuget.org via Trusted Publishing on release tags; the package-consumer smoke covers every
  shippable package.

### Operator polish

- Job and tenant panels surface the retry budget; the scope selector uses a themed popover;
  automatic retention is lineage-safe and purge sections are uniformly bounded.

## 0.2.0 (preview)

Multi-tenancy lands as a first-class part of the ledger, the external outbox gains ledger-native
observability, and the dashboard grows its operator depth surface.

### Multi-tenancy

- Registration is insert-or-return-existing: `ITenants.RegisterAsync(tenantKey, ...)` is idempotent
  and returns the existing tenant on a repeat call; lifecycle changes go through Suspend/Resume only.
- Tenant reads: `ITenants.GetAsync(tenantKey)` and `GET /tenants/{key}`; job snapshots carry
  `TenantKey`, and handlers see the executing job's tenant via `JobContext.TenantKey`.
- Suspension is admission control with the commit boundary as the guarantee: new work for a
  suspended tenant is rejected at enqueue, while children inherited inside an already-admitted
  lineage still land. Suspension does not stop or cancel work already in the ledger.
- Definitions can require or forbid a tenant: `[Job(TenantRequirement = ...)]` (Optional, Required,
  Forbidden) is enforced at the enqueue boundary on every provider, and `Required` combined with a
  schedule is a startup error.
- Cross-tenant child enqueues need an explicit `OverrideParentTenant` opt-in; new enqueue rejection
  reasons name the tenant failures (required, forbidden, parent mismatch).
- `DeduplicationKey.ForTenant(tenantKey, businessKey)` builds tenant-relative keys.
- The curated `acta.jobs_view` resolves `tenant_key` beside the raw `tenant_id`, so tenant-scoped
  SQL reads no longer need a join.
- The tenant catalog is global per store and a tenant is a routing and reporting dimension, not a
  security or isolation boundary; see [concepts](./guide/concepts.md).

### Outbox observability

- `sys.outbox` records each successful tick's accounting as its job result
  (`claimed=.. relayed=.. dedup=.. quarantined=0 backlog=..`), retained newest-only on the
  recurring slot, so the dashboard job detail shows the last tick at a glance.
- The overview health verdict reports a lagging source ("outbox lagging N rows") once the backlog
  exceeds what one relay tick can move. Everything is read from the ledger; the dashboard never
  opens producer databases.

### Operator depth

- The dashboard reads and amends payloads, enqueues and clones jobs from input templates, drives
  schedule controls, filters the event ledger, and copies any view as SQL; control endpoints sit
  behind an authorization seam and an explicit confirmation header.

### Behavior fixes

- Recurring jobs never terminalize on consecutive failures; the slot keeps rescheduling and the
  failures alert.
- Recurring slots are claimed at their definition's priority.
- Cancellation-shaped provider exceptions surface as `OperationCanceledException`.
- Wait-timeout overshoot and sub-second backoff collapse are fixed; retiring a definition cancels
  its parked jobs; worker catalog and jobs options validate together at startup.

## 0.1.x (early preview)

First public preview of Acta: the SQL-native durable work ledger for .NET.

- Durable jobs: fire-and-forget, delayed, and recurring under one model.
- Durable execution: named run-once steps, `AtMostOnce()` step policy, checkpoint slots, durable
  sleeps, signals, child jobs with lineage, exclusive keys.
- Failure and recovery: worker leases with heartbeats, leaderless reclaim, Explain, restart with
  original input, failure alerts.
- Visibility: SQL-visible state with curated operator views, an append-only event ledger, the
  embedded dashboard and JSON API, the embedded CLI including `jobs debug`.
- Providers: PostgreSQL, SQL Server, SQLite with one operational model; source-generated dispatch;
  NativeAOT support; deterministic test host.
- Atomic enqueue with business data: transactional `IJobs` enqueue overloads that join a caller-owned
  `DbTransaction` (same database), and provider-package outbox staging (`AddToActaOutboxAsync` on the
  caller's own transaction) plus an Acta-owned `sys.outbox` relay for a different database. Neither is a
  universal exactly-once guarantee.
  See [transactional enqueue and the external outbox](./guide/transactional-enqueue-and-outbox.md).

APIs, schema, and behavior may change without deprecation during the preview. Known gaps are
tracked in [known limitations](./technical/known-limitations.md).
