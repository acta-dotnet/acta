# Release notes

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
